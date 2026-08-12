namespace SemanticKnowledge;

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
    /// <summary>Delete stored documents in the collection whose ExternalId was not present in the incoming stream.</summary>
    public bool DeleteMissing { get; init; } = true;
}

public sealed record KnowledgeSyncResult
{
    public int Inserted { get; init; }
    public int Updated { get; init; }
    public int Unchanged { get; init; }
    public int Deleted { get; init; }
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
}

internal sealed class KnowledgeSynchronizationService(
    ISemanticKnowledgeStore store,
    IKnowledgeStorageProvider storage) : IKnowledgeSynchronizationService
{
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
        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);

        // The incoming corpus itself remains streaming. We retain only the existing collection's lightweight identity/hash
        // information plus the seen ExternalIds so DeleteMissing can be deterministic.
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
            var input = new KnowledgeDocumentInput
            {
                Id = old?.Id,
                ExternalId = incoming.ExternalId,
                KnowledgeBaseId = knowledgeBaseId,
                CollectionId = collectionId,
                SchemaId = schemaId,
                Title = incoming.Title,
                Description = incoming.Description,
                Tags = incoming.Tags,
                Values = incoming.Values
            };

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

        return new KnowledgeSyncResult { Inserted = inserted, Updated = updated, Unchanged = unchanged, Deleted = deleted };
    }
}
