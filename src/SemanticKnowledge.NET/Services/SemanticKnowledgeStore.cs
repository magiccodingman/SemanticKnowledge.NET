using System.Security.Cryptography;
using System.Text;
using OnnxTextEmbeddings;

namespace SemanticKnowledge;

public interface ISemanticKnowledgeStore
{
    Task<KnowledgeProviderCapabilities> InitializeAsync(CancellationToken cancellationToken = default);
    Task<KnowledgeBaseRecord> GetOrCreateKnowledgeBaseAsync(string title, string? externalId = null, CancellationToken cancellationToken = default);
    Task<KnowledgeCollectionRecord> GetOrCreateCollectionAsync(Guid knowledgeBaseId, string title, Guid? parentCollectionId = null, Guid? defaultSchemaId = null, string? externalId = null, CancellationToken cancellationToken = default);
    Task<KnowledgeSchemaDefinition> EnsureSchemaAsync(KnowledgeSchemaDefinition schema, CancellationToken cancellationToken = default);
    Task<Guid> UpsertDocumentAsync(KnowledgeDocumentInput document, CancellationToken cancellationToken = default);
    Task<KnowledgeDocumentRecord?> GetDocumentAsync(Guid documentId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<KnowledgeSearchHit>> SearchAsync(string query, KnowledgeSearchRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<KnowledgeSearchHit>> SearchAsync(QueryEmbedding query, KnowledgeSearchRequest request, CancellationToken cancellationToken = default);
    Task DeleteDocumentAsync(Guid documentId, CancellationToken cancellationToken = default);
    Task ResetAsync(CancellationToken cancellationToken = default);
}

internal sealed class SemanticKnowledgeStore(SemanticKnowledgeOptions options, IKnowledgeEmbeddingProvider embeddings, IKnowledgeStorageProvider storage) : ISemanticKnowledgeStore
{
    private KnowledgeProviderCapabilities? _capabilities;
    private KnowledgeEmbeddingProviderInfo? _embeddingInfo;
    private readonly SemaphoreSlim _initialization = new(1, 1);

    public async Task<KnowledgeProviderCapabilities> InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_capabilities is not null) return _capabilities;
        await _initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_capabilities is not null) return _capabilities;
            options.Validate();
            _embeddingInfo = await embeddings.GetInfoAsync(cancellationToken).ConfigureAwait(false);
            _capabilities = await storage.InitializeAsync(new KnowledgeStorageInitialization { DatabaseVersion = options.DatabaseVersion, PersistenceMode = options.PersistenceMode, Embedding = _embeddingInfo, StoragePreference = options.Embeddings.Storage }, cancellationToken).ConfigureAwait(false);
            if (_embeddingInfo.OutputDimensions > _capabilities.MaxDimensions) throw new InvalidOperationException($"The configured embedding space is {_embeddingInfo.OutputDimensions}-dimensional, but {_capabilities.Provider} supports at most {_capabilities.MaxDimensions}. Configure Embeddings.OutputDimensions explicitly. Dimension reduction is lossy and is never applied implicitly.");
            return _capabilities;
        }
        finally { _initialization.Release(); }
    }

    public async Task<KnowledgeBaseRecord> GetOrCreateKnowledgeBaseAsync(string title, string? externalId = null, CancellationToken cancellationToken = default) { await InitializeAsync(cancellationToken).ConfigureAwait(false); return await storage.GetOrCreateKnowledgeBaseAsync(title, externalId, cancellationToken).ConfigureAwait(false); }
    public async Task<KnowledgeCollectionRecord> GetOrCreateCollectionAsync(Guid knowledgeBaseId, string title, Guid? parentCollectionId = null, Guid? defaultSchemaId = null, string? externalId = null, CancellationToken cancellationToken = default) { await InitializeAsync(cancellationToken).ConfigureAwait(false); return await storage.GetOrCreateCollectionAsync(knowledgeBaseId, title, parentCollectionId, defaultSchemaId, externalId, cancellationToken).ConfigureAwait(false); }
    public async Task<KnowledgeSchemaDefinition> EnsureSchemaAsync(KnowledgeSchemaDefinition schema, CancellationToken cancellationToken = default) { await InitializeAsync(cancellationToken).ConfigureAwait(false); schema.Validate(); await storage.UpsertSchemaAsync(schema, cancellationToken).ConfigureAwait(false); return schema; }

    public async Task<Guid> UpsertDocumentAsync(KnowledgeDocumentInput input, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var schema = await storage.GetSchemaAsync(input.SchemaId, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException($"Schema {input.SchemaId} is not registered.");
        ValidateDocument(input, schema);
        var record = new KnowledgeDocumentRecord { Id = input.Id ?? Guid.NewGuid(), ExternalId = input.ExternalId, KnowledgeBaseId = input.KnowledgeBaseId, CollectionId = input.CollectionId, SchemaId = input.SchemaId, Title = input.Title, Description = input.Description, Tags = NormalizeTags(input.Tags), Values = input.Values.ToDictionary(x => KnowledgeSchemaBuilder.NormalizeKey(x.Key), x => x.Value, StringComparer.OrdinalIgnoreCase), SourceHash = ComputeSourceHash(input) };
        var sources = await BuildSemanticSourcesAsync(record, schema, cancellationToken).ConfigureAwait(false);
        await storage.UpsertDocumentAsync(record, sources, cancellationToken).ConfigureAwait(false);
        return record.Id;
    }

    public async Task<KnowledgeDocumentRecord?> GetDocumentAsync(Guid documentId, CancellationToken cancellationToken = default) { await InitializeAsync(cancellationToken).ConfigureAwait(false); return await storage.GetDocumentAsync(documentId, cancellationToken).ConfigureAwait(false); }
    public async Task<IReadOnlyList<KnowledgeSearchHit>> SearchAsync(string query, KnowledgeSearchRequest request, CancellationToken cancellationToken = default) { await InitializeAsync(cancellationToken).ConfigureAwait(false); return await SearchAsync(await embeddings.EmbedQueryAsync(query, cancellationToken).ConfigureAwait(false), request, cancellationToken).ConfigureAwait(false); }
    public async Task<IReadOnlyList<KnowledgeSearchHit>> SearchAsync(QueryEmbedding query, KnowledgeSearchRequest request, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (request.Top <= 0) throw new ArgumentOutOfRangeException(nameof(request), "Top must be greater than zero.");
        var info = _embeddingInfo ?? throw new InvalidOperationException("Store not initialized.");
        if (query.Vector.Dimensions != info.OutputDimensions) throw new InvalidOperationException($"Query dimensions {query.Vector.Dimensions} do not match store output dimensions {info.OutputDimensions}.");
        if (!string.Equals(query.Identity.EmbeddingSpaceFingerprint, info.EmbeddingSpaceFingerprint, StringComparison.Ordinal)) throw new InvalidOperationException("The query embedding space does not match this store's active embedding profile.");
        return await storage.SearchAsync(query, request, cancellationToken).ConfigureAwait(false);
    }
    public async Task DeleteDocumentAsync(Guid documentId, CancellationToken cancellationToken = default) { await InitializeAsync(cancellationToken).ConfigureAwait(false); await storage.DeleteDocumentAsync(documentId, cancellationToken).ConfigureAwait(false); }
    public async Task ResetAsync(CancellationToken cancellationToken = default) { await InitializeAsync(cancellationToken).ConfigureAwait(false); await storage.ResetAsync(cancellationToken).ConfigureAwait(false); _capabilities = null; _embeddingInfo = null; }

    private async Task<IReadOnlyList<SemanticSourceRecord>> BuildSemanticSourcesAsync(KnowledgeDocumentRecord document, KnowledgeSchemaDefinition schema, CancellationToken cancellationToken)
    {
        var semanticFields = schema.Fields.Where(x => x.SemanticMode != SemanticMode.None && x.SemanticWeightPercent > 0).ToArray();
        var sources = new List<SemanticSourceRecord>();
        foreach (var field in semanticFields)
        {
            var text = GetSemanticText(document, field); if (string.IsNullOrWhiteSpace(text)) continue;
            var records = await embeddings.EmbedDocumentAsync(text, cancellationToken).ConfigureAwait(false);
            if (field.SemanticMode == SemanticMode.Whole && records.Count > 1) throw new InvalidOperationException($"Semantic field '{field.Key}' is configured as Whole but the embedding provider chunked it. Reduce the field size or configure it as Chunked.");
            var weight = SemanticWeightProfileV1.ToScorerWeight(field.SemanticWeightPercent, semanticFields.Length);
            foreach (var embedding in records) sources.Add(new SemanticSourceRecord { Id = Guid.NewGuid(), KnowledgeBaseId = document.KnowledgeBaseId, CollectionId = document.CollectionId, DocumentId = document.Id, FieldId = field.Id, FieldKey = field.Key, ScorerWeight = weight, Embedding = embedding });
        }
        return sources;
    }

    private static string? GetSemanticText(KnowledgeDocumentRecord document, KnowledgeSchemaField field) => field.Key switch { KnowledgeSystemFields.Title => document.Title, KnowledgeSystemFields.Description => document.Description, KnowledgeSystemFields.Tags => string.Join("\n", document.Tags), _ => document.Values.TryGetValue(field.Key, out var value) ? value.ToSemanticText() : null };
    private static void ValidateDocument(KnowledgeDocumentInput document, KnowledgeSchemaDefinition schema)
    {
        if (document.KnowledgeBaseId == Guid.Empty || document.CollectionId == Guid.Empty || document.SchemaId == Guid.Empty) throw new InvalidOperationException("KnowledgeBaseId, CollectionId and SchemaId are required.");
        if (string.IsNullOrWhiteSpace(document.Title)) throw new InvalidOperationException("Title is required.");
        foreach (var field in schema.Fields.Where(x => !x.System)) { var present = document.Values.TryGetValue(field.Key, out var value); if (field.Required && !present) throw new InvalidOperationException($"Required field '{field.Key}' is missing."); if (present && value.Type != field.Type) throw new InvalidOperationException($"Field '{field.Key}' expects {field.Type} but received {value.Type}."); }
        foreach (var key in document.Values.Keys) _ = schema.GetField(key);
    }
    private static IReadOnlyList<string> NormalizeTags(IEnumerable<string> tags) => tags.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private static string ComputeSourceHash(KnowledgeDocumentInput document) { var builder = new StringBuilder().Append(document.Title).Append('\n').Append(document.Description).Append('\n'); foreach (var tag in document.Tags.Order(StringComparer.OrdinalIgnoreCase)) builder.Append("tag:").Append(tag).Append('\n'); foreach (var value in document.Values.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)) builder.Append(value.Key).Append('=').Append(value.Value.ToObject()).Append('\n'); return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))); }
}
