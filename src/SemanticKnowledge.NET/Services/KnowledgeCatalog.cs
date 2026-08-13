namespace SemanticKnowledge;

/// <summary>
/// Updates canonical KnowledgeBase and Collection metadata while keeping derived Collection search indexes synchronized.
/// Use this when metadata is richer than the title-only GetOrCreate convenience APIs.
/// </summary>
public interface IKnowledgeCatalog
{
    Task<KnowledgeBaseRecord> UpsertKnowledgeBaseAsync(KnowledgeBaseRecord knowledgeBase, CancellationToken cancellationToken = default);
    Task<KnowledgeCollectionRecord> UpsertCollectionAsync(KnowledgeCollectionRecord collection, CancellationToken cancellationToken = default);
}

internal sealed class KnowledgeCatalog(
    ISemanticKnowledgeStore store,
    IKnowledgeArchiveStorage catalogStorage,
    IKnowledgeStorageProvider storage,
    IKnowledgeEmbeddingProvider embeddings,
    IEnumerable<IKnowledgeAdvancedSearchProvider> advancedSearchProviders) : IKnowledgeCatalog
{
    private static readonly Guid CollectionTitleFieldId = new("b6a961c4-71a2-41e8-9ab4-8cb51e223201");
    private static readonly Guid CollectionDescriptionFieldId = new("b6a961c4-71a2-41e8-9ab4-8cb51e223202");
    private static readonly Guid CollectionTagsFieldId = new("b6a961c4-71a2-41e8-9ab4-8cb51e223203");
    private readonly IKnowledgeAdvancedSearchProvider? _advancedSearch = advancedSearchProviders.SingleOrDefault();

    public async Task<KnowledgeBaseRecord> UpsertKnowledgeBaseAsync(KnowledgeBaseRecord knowledgeBase, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(knowledgeBase);
        if (knowledgeBase.Id == Guid.Empty) throw new ArgumentException("KnowledgeBase ID cannot be empty.", nameof(knowledgeBase));
        ArgumentException.ThrowIfNullOrWhiteSpace(knowledgeBase.Title);

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

        var persisted = (await storage.GetCollectionsAsync(collection.KnowledgeBaseId, cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(item => item.Id == collection.Id)
            ?? throw new InvalidOperationException($"Collection {collection.Id} was not readable after upsert.");

        await storage.UpsertCollectionSemanticSourcesAsync(
            persisted,
            await BuildSemanticSourcesAsync(persisted, cancellationToken).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

        if (_advancedSearch is not null)
            await _advancedSearch.UpsertSourcesAsync(
                persisted.Id,
                SemanticEntityKind.Collection,
                SemanticKnowledgeStore.BuildCollectionLexicalSources(persisted),
                cancellationToken).ConfigureAwait(false);

        return persisted;
    }

    private async Task<IReadOnlyList<SemanticSourceRecord>> BuildSemanticSourcesAsync(KnowledgeCollectionRecord collection, CancellationToken cancellationToken)
    {
        var sources = new List<SemanticSourceRecord>();
        await AddSourceAsync(collection, CollectionTitleFieldId, KnowledgeSystemFields.Title, collection.Title, 1.35f, sources, cancellationToken).ConfigureAwait(false);
        await AddSourceAsync(collection, CollectionDescriptionFieldId, KnowledgeSystemFields.Description, collection.Description, 1f, sources, cancellationToken).ConfigureAwait(false);
        await AddSourceAsync(collection, CollectionTagsFieldId, KnowledgeSystemFields.Tags, string.Join("\n", collection.Tags), 1.15f, sources, cancellationToken).ConfigureAwait(false);
        return sources;
    }

    private async Task AddSourceAsync(
        KnowledgeCollectionRecord collection,
        Guid fieldId,
        string fieldKey,
        string? text,
        float weight,
        List<SemanticSourceRecord> destination,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        foreach (var embedding in await embeddings.EmbedDocumentAsync(text, cancellationToken).ConfigureAwait(false))
            destination.Add(new SemanticSourceRecord
            {
                Id = Guid.NewGuid(),
                KnowledgeBaseId = collection.KnowledgeBaseId,
                CollectionId = collection.Id,
                ItemId = collection.Id,
                EntityKind = SemanticEntityKind.Collection,
                FieldId = fieldId,
                FieldKey = fieldKey,
                ScorerWeight = weight,
                Embedding = embedding
            });
    }
}
