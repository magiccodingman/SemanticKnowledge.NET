namespace SemanticKnowledge;

public enum KnowledgeSyncPublication
{
    Incremental = 1,
    AtomicSnapshot = 2
}

public sealed record ExternalKnowledgeDocumentInput
{
    public required string ExternalId { get; init; }
    public required string Title { get; init; }
    public string Description { get; init; } = string.Empty;
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, KnowledgeValue> Values { get; init; } = new Dictionary<string, KnowledgeValue>();
}

public sealed record KnowledgeSyncOptions
{
    /// <summary>Delete stored externally-keyed documents in the collection whose ExternalId was not present in the incoming stream.</summary>
    public bool DeleteMissing { get; init; } = true;

    /// <summary>Incremental is the backward-compatible default. AtomicSnapshot stages a coherent corpus and publishes it all at once.</summary>
    public KnowledgeSyncPublication Publication { get; init; } = KnowledgeSyncPublication.Incremental;

    /// <summary>Opaque caller-owned revision associated with an atomic snapshot, for example a crawl revision or source commit.</summary>
    public string? SourceRevision { get; init; }
}

public sealed record KnowledgeSyncResult
{
    public int Inserted { get; init; }
    public int Updated { get; init; }
    public int Unchanged { get; init; }
    public int Deleted { get; init; }
    public KnowledgeSyncPublication Publication { get; init; } = KnowledgeSyncPublication.Incremental;
    public Guid? SnapshotId { get; init; }
    public string? SourceRevision { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
}

public interface IKnowledgeSynchronizationService
{
    Task<KnowledgeSyncResult> SyncCollectionAsync(
        Guid knowledgeBaseId,
        Guid collectionId,
        Guid schemaId,
        IAsyncEnumerable<ExternalKnowledgeDocumentInput> documents,
        KnowledgeSyncOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<KnowledgeSyncResult> SyncCollectionSnapshotAsync(
        Guid knowledgeBaseId,
        Guid collectionId,
        Guid schemaId,
        string sourceRevision,
        IAsyncEnumerable<ExternalKnowledgeDocumentInput> documents,
        bool deleteMissing = true,
        CancellationToken cancellationToken = default);

    Task<KnowledgeCollectionSnapshotState?> GetCollectionSnapshotStateAsync(Guid collectionId, CancellationToken cancellationToken = default);
}

internal sealed class KnowledgeSynchronizationService(
    ISemanticKnowledgeStore store,
    IKnowledgeStorageProvider storage,
    IKnowledgeEmbeddingProvider embeddings,
    IEnumerable<IKnowledgeCollectionSnapshotProvider> snapshotProviders) : IKnowledgeSynchronizationService
{
    private readonly IKnowledgeCollectionSnapshotProvider? _snapshotProvider = snapshotProviders.SingleOrDefault();

    public async Task<KnowledgeSyncResult> SyncCollectionAsync(
        Guid knowledgeBaseId,
        Guid collectionId,
        Guid schemaId,
        IAsyncEnumerable<ExternalKnowledgeDocumentInput> documents,
        KnowledgeSyncOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        options ??= new KnowledgeSyncOptions();
        return options.Publication switch
        {
            KnowledgeSyncPublication.Incremental => await SyncIncrementalAsync(knowledgeBaseId, collectionId, schemaId, documents, options, cancellationToken).ConfigureAwait(false),
            KnowledgeSyncPublication.AtomicSnapshot => await SyncAtomicSnapshotAsync(knowledgeBaseId, collectionId, schemaId, documents, options, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(options), $"Unknown synchronization publication mode {options.Publication}.")
        };
    }

    public Task<KnowledgeSyncResult> SyncCollectionSnapshotAsync(
        Guid knowledgeBaseId,
        Guid collectionId,
        Guid schemaId,
        string sourceRevision,
        IAsyncEnumerable<ExternalKnowledgeDocumentInput> documents,
        bool deleteMissing = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRevision);
        return SyncCollectionAsync(knowledgeBaseId, collectionId, schemaId, documents, new KnowledgeSyncOptions
        {
            DeleteMissing = deleteMissing,
            Publication = KnowledgeSyncPublication.AtomicSnapshot,
            SourceRevision = sourceRevision
        }, cancellationToken);
    }

    public async Task<KnowledgeCollectionSnapshotState?> GetCollectionSnapshotStateAsync(Guid collectionId, CancellationToken cancellationToken = default)
    {
        if (collectionId == Guid.Empty) throw new ArgumentException("CollectionId cannot be empty.", nameof(collectionId));
        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var provider = _snapshotProvider ?? throw new NotSupportedException("The configured SemanticKnowledge storage provider does not support atomic Collection snapshots.");
        return await provider.GetCollectionSnapshotStateAsync(collectionId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<KnowledgeSyncResult> SyncIncrementalAsync(
        Guid knowledgeBaseId,
        Guid collectionId,
        Guid schemaId,
        IAsyncEnumerable<ExternalKnowledgeDocumentInput> documents,
        KnowledgeSyncOptions options,
        CancellationToken cancellationToken)
    {
        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var existingRecords = await storage.GetDocumentsAsync(knowledgeBaseId, cancellationToken).ConfigureAwait(false);
        var existing = existingRecords
            .Where(document => document.CollectionId == collectionId && !string.IsNullOrWhiteSpace(document.ExternalId))
            .ToDictionary(document => document.ExternalId!, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var inserted = 0;
        var updated = 0;
        var unchanged = 0;
        var deleted = 0;

        await foreach (var incoming in documents.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(incoming.ExternalId);
            if (!seen.Add(incoming.ExternalId))
                throw new InvalidOperationException($"The synchronization stream contains duplicate ExternalId '{incoming.ExternalId}'.");

            existing.TryGetValue(incoming.ExternalId, out var old);
            var input = ToInput(knowledgeBaseId, collectionId, schemaId, incoming, old?.Id);
            if (old is not null && KnowledgeSourceHash.Compute(input) == old.SourceHash)
            {
                unchanged++;
                continue;
            }

            await store.UpsertDocumentAsync(input, cancellationToken).ConfigureAwait(false);
            if (old is null) inserted++; else updated++;
        }

        if (options.DeleteMissing)
        {
            foreach (var pair in existing)
            {
                if (seen.Contains(pair.Key)) continue;
                await store.DeleteDocumentAsync(pair.Value.Id, cancellationToken).ConfigureAwait(false);
                deleted++;
            }
        }

        return new KnowledgeSyncResult { Inserted = inserted, Updated = updated, Unchanged = unchanged, Deleted = deleted, Publication = KnowledgeSyncPublication.Incremental };
    }

    private async Task<KnowledgeSyncResult> SyncAtomicSnapshotAsync(
        Guid knowledgeBaseId,
        Guid collectionId,
        Guid schemaId,
        IAsyncEnumerable<ExternalKnowledgeDocumentInput> documents,
        KnowledgeSyncOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SourceRevision);
        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var provider = _snapshotProvider ?? throw new NotSupportedException("The configured SemanticKnowledge storage provider does not support atomic Collection snapshots.");
        var schema = await storage.GetSchemaAsync(schemaId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Schema {schemaId} is not registered.");

        var collectionDocuments = (await storage.GetDocumentsAsync(knowledgeBaseId, cancellationToken).ConfigureAwait(false))
            .Where(document => document.CollectionId == collectionId)
            .ToArray();
        var existing = collectionDocuments
            .Where(document => !string.IsNullOrWhiteSpace(document.ExternalId))
            .ToDictionary(document => document.ExternalId!, StringComparer.Ordinal);
        var unkeyed = collectionDocuments.Where(document => string.IsNullOrWhiteSpace(document.ExternalId)).ToArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var snapshot = await provider.BeginCollectionSnapshotAsync(knowledgeBaseId, collectionId, schemaId, options.SourceRevision!, cancellationToken).ConfigureAwait(false);
        var inserted = 0;
        var updated = 0;
        var unchanged = 0;

        try
        {
            await foreach (var incoming in documents.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(incoming.ExternalId);
                if (!seen.Add(incoming.ExternalId))
                    throw new InvalidOperationException($"The synchronization stream contains duplicate ExternalId '{incoming.ExternalId}'.");

                existing.TryGetValue(incoming.ExternalId, out var old);
                var input = ToInput(knowledgeBaseId, collectionId, schemaId, incoming, old?.Id);
                var record = CreateDocumentRecord(input, old?.Id ?? Guid.NewGuid());
                ValidateDocument(input, schema);

                if (old is not null && string.Equals(record.SourceHash, old.SourceHash, StringComparison.Ordinal))
                {
                    await provider.StageExistingCollectionSnapshotDocumentAsync(snapshot, old.Id, cancellationToken).ConfigureAwait(false);
                    unchanged++;
                    continue;
                }

                var semanticSources = await BuildSemanticSourcesAsync(record, schema, cancellationToken).ConfigureAwait(false);
                var prepared = new KnowledgePreparedSnapshotDocument
                {
                    Document = record,
                    SemanticSources = semanticSources,
                    LexicalSources = SemanticKnowledgeStore.BuildDocumentLexicalSources(record, schema)
                };
                await provider.StageCollectionSnapshotDocumentAsync(snapshot, prepared, cancellationToken).ConfigureAwait(false);
                if (old is null) inserted++; else updated++;
            }

            // Locally-authored/non-external documents are outside the source's reconciliation namespace and always survive.
            foreach (var document in unkeyed)
                await provider.StageExistingCollectionSnapshotDocumentAsync(snapshot, document.Id, cancellationToken).ConfigureAwait(false);

            if (!options.DeleteMissing)
            {
                foreach (var pair in existing)
                    if (!seen.Contains(pair.Key))
                        await provider.StageExistingCollectionSnapshotDocumentAsync(snapshot, pair.Value.Id, cancellationToken).ConfigureAwait(false);
            }

            var deleted = options.DeleteMissing ? existing.Keys.Count(key => !seen.Contains(key)) : 0;
            var state = await provider.PublishCollectionSnapshotAsync(snapshot, cancellationToken).ConfigureAwait(false);
            return new KnowledgeSyncResult
            {
                Inserted = inserted,
                Updated = updated,
                Unchanged = unchanged,
                Deleted = deleted,
                Publication = KnowledgeSyncPublication.AtomicSnapshot,
                SnapshotId = state.ActiveSnapshotId,
                SourceRevision = state.SourceRevision,
                PublishedAt = state.PublishedAt
            };
        }
        catch
        {
            await provider.AbortCollectionSnapshotAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<IReadOnlyList<SemanticSourceRecord>> BuildSemanticSourcesAsync(KnowledgeDocumentRecord document, KnowledgeSchemaDefinition schema, CancellationToken cancellationToken)
    {
        var semanticFields = schema.Fields.Where(field => field.SemanticMode != SemanticMode.None && field.SemanticWeightPercent > 0).ToArray();
        var sources = new List<SemanticSourceRecord>();
        foreach (var field in semanticFields)
        {
            var text = GetText(document, field);
            if (string.IsNullOrWhiteSpace(text)) continue;
            var records = await embeddings.EmbedDocumentAsync(text, cancellationToken).ConfigureAwait(false);
            if (field.SemanticMode == SemanticMode.Whole && records.Count > 1)
                throw new InvalidOperationException($"Semantic field '{field.Key}' is configured as Whole but the embedding provider chunked it. Reduce the field size or configure it as Chunked.");
            var weight = SemanticWeightProfileV1.ToScorerWeight(field.SemanticWeightPercent, semanticFields.Length);
            foreach (var embedding in records)
                sources.Add(new SemanticSourceRecord
                {
                    Id = Guid.NewGuid(),
                    KnowledgeBaseId = document.KnowledgeBaseId,
                    CollectionId = document.CollectionId,
                    ItemId = document.Id,
                    EntityKind = SemanticEntityKind.Document,
                    DocumentId = document.Id,
                    FieldId = field.Id,
                    FieldKey = field.Key,
                    ScorerWeight = weight,
                    Embedding = embedding
                });
        }
        return sources;
    }

    private static KnowledgeDocumentInput ToInput(Guid knowledgeBaseId, Guid collectionId, Guid schemaId, ExternalKnowledgeDocumentInput incoming, Guid? id) => new()
    {
        Id = id,
        ExternalId = incoming.ExternalId,
        KnowledgeBaseId = knowledgeBaseId,
        CollectionId = collectionId,
        SchemaId = schemaId,
        Title = incoming.Title,
        Description = incoming.Description,
        Tags = incoming.Tags,
        Values = incoming.Values
    };

    private static KnowledgeDocumentRecord CreateDocumentRecord(KnowledgeDocumentInput input, Guid id)
    {
        var tags = input.Tags.Where(tag => !string.IsNullOrWhiteSpace(tag)).Select(tag => tag.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var values = input.Values.ToDictionary(pair => KnowledgeSchemaBuilder.NormalizeKey(pair.Key), pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var normalized = new KnowledgeDocumentInput
        {
            Id = id,
            ExternalId = input.ExternalId,
            KnowledgeBaseId = input.KnowledgeBaseId,
            CollectionId = input.CollectionId,
            SchemaId = input.SchemaId,
            Title = input.Title,
            Description = input.Description,
            Tags = tags,
            Values = values
        };
        return new KnowledgeDocumentRecord
        {
            Id = id,
            ExternalId = normalized.ExternalId,
            KnowledgeBaseId = normalized.KnowledgeBaseId,
            CollectionId = normalized.CollectionId,
            SchemaId = normalized.SchemaId,
            Title = normalized.Title,
            Description = normalized.Description,
            Tags = tags,
            Values = values,
            SourceHash = KnowledgeSourceHash.Compute(normalized)
        };
    }

    private static string? GetText(KnowledgeDocumentRecord document, KnowledgeSchemaField field) => field.Key switch
    {
        KnowledgeSystemFields.Title => document.Title,
        KnowledgeSystemFields.Description => document.Description,
        KnowledgeSystemFields.Tags => string.Join("\n", document.Tags),
        _ => document.Values.TryGetValue(field.Key, out var value) ? value.ToSemanticText() : null
    };

    private static void ValidateDocument(KnowledgeDocumentInput document, KnowledgeSchemaDefinition schema)
    {
        if (document.KnowledgeBaseId == Guid.Empty || document.CollectionId == Guid.Empty || document.SchemaId == Guid.Empty)
            throw new InvalidOperationException("KnowledgeBaseId, CollectionId and SchemaId are required.");
        if (string.IsNullOrWhiteSpace(document.Title)) throw new InvalidOperationException("Title is required.");
        foreach (var field in schema.Fields.Where(field => !field.System))
        {
            var present = document.Values.TryGetValue(field.Key, out var value);
            if (field.Required && !present) throw new InvalidOperationException($"Required field '{field.Key}' is missing.");
            if (present && value.Type != field.Type) throw new InvalidOperationException($"Field '{field.Key}' expects {field.Type} but received {value.Type}.");
        }
        foreach (var key in document.Values.Keys) _ = schema.GetField(key);
    }
}
