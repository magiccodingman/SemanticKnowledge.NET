namespace SemanticKnowledge;

/// <summary>
/// Updates canonical KnowledgeBase and Collection metadata while keeping derived Collection semantics synchronized.
/// Use this when metadata is richer than the title-only GetOrCreate convenience APIs.
/// </summary>
public interface IKnowledgeCatalog
{
    Task<KnowledgeBaseRecord> UpsertKnowledgeBaseAsync(KnowledgeBaseRecord knowledgeBase, CancellationToken cancellationToken = default);
    Task<KnowledgeCollectionRecord> UpsertCollectionAsync(KnowledgeCollectionRecord collection, CancellationToken cancellationToken = default);
}

internal sealed class KnowledgeCatalog(
    ISemanticKnowledgeStore store,
    IKnowledgeArchiveStorage catalogStorage) : IKnowledgeCatalog
{
    public async Task<KnowledgeBaseRecord> UpsertKnowledgeBaseAsync(KnowledgeBaseRecord knowledgeBase, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(knowledgeBase);
        if (knowledgeBase.Id == Guid.Empty) throw new ArgumentException("KnowledgeBase ID cannot be empty.", nameof(knowledgeBase));
        ArgumentException.ThrowIfNullOrWhiteSpace(knowledgeBase.Title);
        if (knowledgeBase.Tags.Count != 0)
            throw new NotSupportedException("KnowledgeBase tag persistence is not part of the v1 engine schema. Collection and Document tags are fully supported.");

        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await catalogStorage.UpsertKnowledgeBaseAsync(knowledgeBase, cancellationToken).ConfigureAwait(false);
        return await catalogStorage.GetKnowledgeBaseAsync(knowledgeBase.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"KnowledgeBase {knowledgeBase.Id} was not readable after upsert.");
    }

    public async Task<KnowledgeCollectionRecord> UpsertCollectionAsync(KnowledgeCollectionRecord collection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);
        if (collection.Id == Guid.Empty || collection.KnowledgeBaseId == Guid.Empty)
            throw new ArgumentException("Collection and KnowledgeBase IDs cannot be empty.", nameof(collection));
        ArgumentException.ThrowIfNullOrWhiteSpace(collection.Title);

        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await catalogStorage.UpsertCollectionAsync(collection, cancellationToken).ConfigureAwait(false);

        // Re-enter the normal Store path after the canonical update. The provider resolves the existing Collection,
        // and the Store rebuilds Title/Description/Tags semantic sources using the shared routing contract.
        return await store.GetOrCreateCollectionAsync(
            collection.KnowledgeBaseId,
            collection.Title,
            collection.ParentCollectionId,
            collection.DefaultSchemaId,
            collection.ExternalId,
            cancellationToken).ConfigureAwait(false);
    }
}
