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

internal sealed class SemanticKnowledgeStore(
    SemanticKnowledgeOptions options,
    IKnowledgeEmbeddingProvider embeddings,
    IKnowledgeStorageProvider storage,
    IKnowledgeLogicalVersionAccessor logicalVersion,
    IEnumerable<KnowledgeMigrationStep> migrations) : ISemanticKnowledgeStore
{
    private static readonly Guid CollectionTitleFieldId = new("b6a961c4-71a2-41e8-9ab4-8cb51e223201");
    private static readonly Guid CollectionDescriptionFieldId = new("b6a961c4-71a2-41e8-9ab4-8cb51e223202");
    private static readonly Guid CollectionTagsFieldId = new("b6a961c4-71a2-41e8-9ab4-8cb51e223203");
    private readonly KnowledgeMigrationStep[] _migrations = migrations.ToArray();
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

            var storedVersion = await logicalVersion.GetStoredVersionAsync(cancellationToken).ConfigureAwait(false);
            KnowledgeProviderCapabilities capabilities;
            if (storedVersion is null || storedVersion == options.DatabaseVersion)
            {
                capabilities = await InitializeStorageAsync(options.DatabaseVersion, cancellationToken).ConfigureAwait(false);
            }
            else if (storedVersion > options.DatabaseVersion)
            {
                throw new InvalidOperationException($"Stored SemanticKnowledge database version is {storedVersion}, but configured version is {options.DatabaseVersion}. Downgrades are not supported.");
            }
            else if (options.PersistenceMode == KnowledgePersistenceMode.Rebuildable)
            {
                _ = await InitializeStorageAsync(storedVersion.Value, cancellationToken).ConfigureAwait(false);
                await storage.ResetAsync(cancellationToken).ConfigureAwait(false);
                capabilities = await InitializeStorageAsync(options.DatabaseVersion, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                capabilities = await InitializeStorageAsync(storedVersion.Value, cancellationToken).ConfigureAwait(false);
                capabilities = await EnsureEmbeddingGenerationAsync(capabilities, cancellationToken).ConfigureAwait(false);
                await ApplyLogicalMigrationsAsync(storedVersion.Value, options.DatabaseVersion, cancellationToken).ConfigureAwait(false);
                capabilities = await InitializeStorageAsync(options.DatabaseVersion, cancellationToken).ConfigureAwait(false);
            }

            capabilities = await EnsureEmbeddingGenerationAsync(capabilities, cancellationToken).ConfigureAwait(false);
            _capabilities = capabilities;
            return capabilities;
        }
        finally { _initialization.Release(); }
    }

    public async Task<KnowledgeBaseRecord> GetOrCreateKnowledgeBaseAsync(string title, string? externalId = null, CancellationToken cancellationToken = default) { await InitializeAsync(cancellationToken).ConfigureAwait(false); return await storage.GetOrCreateKnowledgeBaseAsync(title, externalId, cancellationToken).ConfigureAwait(false); }

    public async Task<KnowledgeCollectionRecord> GetOrCreateCollectionAsync(Guid knowledgeBaseId, string title, Guid? parentCollectionId = null, Guid? defaultSchemaId = null, string? externalId = null, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var collection = await storage.GetOrCreateCollectionAsync(knowledgeBaseId, title, parentCollectionId, defaultSchemaId, externalId, cancellationToken).ConfigureAwait(false);
        await storage.UpsertCollectionSemanticSourcesAsync(collection, await BuildCollectionSourcesAsync(collection, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        return collection;
    }

    public async Task<KnowledgeSchemaDefinition> EnsureSchemaAsync(KnowledgeSchemaDefinition schema, CancellationToken cancellationToken = default) { await InitializeAsync(cancellationToken).ConfigureAwait(false); schema.Validate(); await storage.UpsertSchemaAsync(schema, cancellationToken).ConfigureAwait(false); return schema; }

    public async Task<Guid> UpsertDocumentAsync(KnowledgeDocumentInput input, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var schema = await storage.GetSchemaAsync(input.SchemaId, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException($"Schema {input.SchemaId} is not registered.");
        ValidateDocument(input, schema);
        var record = CreateDocumentRecord(input, input.Id ?? Guid.NewGuid());
        await storage.UpsertDocumentAsync(record, await BuildSemanticSourcesAsync(record, schema, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
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
        var hits = await storage.SearchAsync(query, request, cancellationToken).ConfigureAwait(false);
        return hits.Where(hit => hit.Score > 0f).Take(request.Top).ToArray();
    }
    public async Task DeleteDocumentAsync(Guid documentId, CancellationToken cancellationToken = default) { await InitializeAsync(cancellationToken).ConfigureAwait(false); await storage.DeleteDocumentAsync(documentId, cancellationToken).ConfigureAwait(false); }
    public async Task ResetAsync(CancellationToken cancellationToken = default) { await InitializeAsync(cancellationToken).ConfigureAwait(false); await storage.ResetAsync(cancellationToken).ConfigureAwait(false); _capabilities = null; _embeddingInfo = null; }

    private async Task<KnowledgeProviderCapabilities> InitializeStorageAsync(int databaseVersion, CancellationToken cancellationToken)
    {
        var info = _embeddingInfo ?? throw new InvalidOperationException("Embedding provider information is unavailable during store initialization.");
        var capabilities = await storage.InitializeAsync(new KnowledgeStorageInitialization
        {
            DatabaseVersion = databaseVersion,
            PersistenceMode = options.PersistenceMode,
            Embedding = info,
            StoragePreference = options.Embeddings.Storage
        }, cancellationToken).ConfigureAwait(false);
        if (info.OutputDimensions > capabilities.MaxDimensions)
            throw new InvalidOperationException($"The configured embedding space is {info.OutputDimensions}-dimensional, but {capabilities.Provider} supports at most {capabilities.MaxDimensions}. Configure Embeddings.OutputDimensions explicitly. Dimension reduction is lossy and is never applied implicitly.");
        return capabilities;
    }

    private async Task<KnowledgeProviderCapabilities> EnsureEmbeddingGenerationAsync(KnowledgeProviderCapabilities capabilities, CancellationToken cancellationToken)
    {
        if (!capabilities.RequiresEmbeddingRebuild)
            return capabilities;
        await RebuildEmbeddingGenerationAsync(cancellationToken).ConfigureAwait(false);
        await storage.CompleteEmbeddingRebuildAsync(cancellationToken).ConfigureAwait(false);
        return capabilities with { RequiresEmbeddingRebuild = false };
    }

    private async Task ApplyLogicalMigrationsAsync(int storedVersion, int targetVersion, CancellationToken cancellationToken)
    {
        var currentVersion = storedVersion;
        while (currentVersion < targetVersion)
        {
            var candidates = _migrations
                .Where(step => step.FromVersion == currentVersion && step.ToVersion <= targetVersion)
                .ToArray();
            if (candidates.Length == 0)
                throw new InvalidOperationException($"SemanticKnowledge database version {currentVersion} must migrate to {targetVersion}, but no migration starting at version {currentVersion} is registered.");
            if (candidates.Length > 1)
                throw new InvalidOperationException($"SemanticKnowledge database version {currentVersion} has multiple registered migration paths. Register exactly one unambiguous next step toward version {targetVersion}.");

            var step = candidates[0];
            var context = new KnowledgeMigrationContext(storage, UpsertMigratedDocumentAsync);
            await step.ApplyAsync(context, cancellationToken).ConfigureAwait(false);
            await logicalVersion.SetStoredVersionAsync(step.ToVersion, cancellationToken).ConfigureAwait(false);
            currentVersion = step.ToVersion;
        }
        if (currentVersion != targetVersion)
            throw new InvalidOperationException($"Registered SemanticKnowledge migrations ended at version {currentVersion}, but configured version is {targetVersion}.");
    }

    private async Task UpsertMigratedDocumentAsync(KnowledgeDocumentRecord document, CancellationToken cancellationToken)
    {
        var schema = await storage.GetSchemaAsync(document.SchemaId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Migrated document {document.Id} references missing schema {document.SchemaId}.");
        var input = new KnowledgeDocumentInput
        {
            Id = document.Id,
            ExternalId = document.ExternalId,
            KnowledgeBaseId = document.KnowledgeBaseId,
            CollectionId = document.CollectionId,
            SchemaId = document.SchemaId,
            Title = document.Title,
            Description = document.Description,
            Tags = document.Tags,
            Values = document.Values
        };
        ValidateDocument(input, schema);
        var normalized = CreateDocumentRecord(input, document.Id);
        await storage.UpsertDocumentAsync(normalized, await BuildSemanticSourcesAsync(normalized, schema, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    private async Task RebuildEmbeddingGenerationAsync(CancellationToken cancellationToken)
    {
        foreach (var collection in await storage.GetCollectionsAsync(null, cancellationToken).ConfigureAwait(false))
            await storage.UpsertCollectionSemanticSourcesAsync(collection, await BuildCollectionSourcesAsync(collection, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        foreach (var document in await storage.GetDocumentsAsync(null, cancellationToken).ConfigureAwait(false))
        {
            var schema = await storage.GetSchemaAsync(document.SchemaId, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException($"Stored document {document.Id} references missing schema {document.SchemaId}.");
            await storage.UpsertDocumentAsync(document, await BuildSemanticSourcesAsync(document, schema, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<SemanticSourceRecord>> BuildCollectionSourcesAsync(KnowledgeCollectionRecord collection, CancellationToken cancellationToken)
    {
        var sources = new List<SemanticSourceRecord>();
        await AddCollectionSourceAsync(collection, CollectionTitleFieldId, KnowledgeSystemFields.Title, collection.Title, 1.35f, sources, cancellationToken).ConfigureAwait(false);
        await AddCollectionSourceAsync(collection, CollectionDescriptionFieldId, KnowledgeSystemFields.Description, collection.Description, 1f, sources, cancellationToken).ConfigureAwait(false);
        await AddCollectionSourceAsync(collection, CollectionTagsFieldId, KnowledgeSystemFields.Tags, string.Join("\n", collection.Tags), 1.15f, sources, cancellationToken).ConfigureAwait(false);
        return sources;
    }

    private async Task AddCollectionSourceAsync(KnowledgeCollectionRecord collection, Guid fieldId, string fieldKey, string? text, float weight, List<SemanticSourceRecord> destination, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        foreach (var embedding in await embeddings.EmbedDocumentAsync(text, cancellationToken).ConfigureAwait(false)) destination.Add(new SemanticSourceRecord { Id = Guid.NewGuid(), KnowledgeBaseId = collection.KnowledgeBaseId, CollectionId = collection.Id, ItemId = collection.Id, EntityKind = SemanticEntityKind.Collection, FieldId = fieldId, FieldKey = fieldKey, ScorerWeight = weight, Embedding = embedding });
    }

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
            foreach (var embedding in records) sources.Add(new SemanticSourceRecord { Id = Guid.NewGuid(), KnowledgeBaseId = document.KnowledgeBaseId, CollectionId = document.CollectionId, ItemId = document.Id, EntityKind = SemanticEntityKind.Document, DocumentId = document.Id, FieldId = field.Id, FieldKey = field.Key, ScorerWeight = weight, Embedding = embedding });
        }
        return sources;
    }

    private static KnowledgeDocumentRecord CreateDocumentRecord(KnowledgeDocumentInput input, Guid id)
    {
        var normalizedInput = new KnowledgeDocumentInput
        {
            Id = id,
            ExternalId = input.ExternalId,
            KnowledgeBaseId = input.KnowledgeBaseId,
            CollectionId = input.CollectionId,
            SchemaId = input.SchemaId,
            Title = input.Title,
            Description = input.Description,
            Tags = NormalizeTags(input.Tags),
            Values = input.Values.ToDictionary(x => KnowledgeSchemaBuilder.NormalizeKey(x.Key), x => x.Value, StringComparer.OrdinalIgnoreCase)
        };
        return new KnowledgeDocumentRecord
        {
            Id = id,
            ExternalId = normalizedInput.ExternalId,
            KnowledgeBaseId = normalizedInput.KnowledgeBaseId,
            CollectionId = normalizedInput.CollectionId,
            SchemaId = normalizedInput.SchemaId,
            Title = normalizedInput.Title,
            Description = normalizedInput.Description,
            Tags = normalizedInput.Tags,
            Values = normalizedInput.Values,
            SourceHash = KnowledgeSourceHash.Compute(normalizedInput)
        };
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
}
