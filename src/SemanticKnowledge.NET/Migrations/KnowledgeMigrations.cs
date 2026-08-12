namespace SemanticKnowledge;

public interface IKnowledgeLogicalVersionAccessor
{
    Task<int?> GetStoredVersionAsync(CancellationToken cancellationToken = default);
    Task SetStoredVersionAsync(int version, CancellationToken cancellationToken = default);
}

public sealed record KnowledgeMigrationStep
{
    public required int FromVersion { get; init; }
    public required int ToVersion { get; init; }
    public required Func<KnowledgeMigrationContext, CancellationToken, Task> ApplyAsync { get; init; }
}

public sealed class KnowledgeMigrationContext
{
    private readonly IKnowledgeStorageProvider _storage;
    private readonly Func<KnowledgeDocumentRecord, CancellationToken, Task> _upsertDocument;

    internal KnowledgeMigrationContext(
        IKnowledgeStorageProvider storage,
        Func<KnowledgeDocumentRecord, CancellationToken, Task> upsertDocument)
    {
        _storage = storage;
        _upsertDocument = upsertDocument;
    }

    public Task<IReadOnlyList<KnowledgeDocumentRecord>> GetDocumentsAsync(Guid? knowledgeBaseId = null, CancellationToken cancellationToken = default)
        => _storage.GetDocumentsAsync(knowledgeBaseId, cancellationToken);

    public Task<IReadOnlyList<KnowledgeCollectionRecord>> GetCollectionsAsync(Guid? knowledgeBaseId = null, CancellationToken cancellationToken = default)
        => _storage.GetCollectionsAsync(knowledgeBaseId, cancellationToken);

    public Task<KnowledgeSchemaDefinition?> GetSchemaAsync(Guid schemaId, CancellationToken cancellationToken = default)
        => _storage.GetSchemaAsync(schemaId, cancellationToken);

    public Task UpsertSchemaAsync(KnowledgeSchemaDefinition schema, CancellationToken cancellationToken = default)
        => _storage.UpsertSchemaAsync(schema, cancellationToken);

    /// <summary>
    /// Rewrites canonical document data and rebuilds that document's semantic sources using the schema currently stored
    /// under Document.SchemaId. This is the normal migration helper after changing values, collection, or schema id.
    /// </summary>
    public Task UpsertDocumentAsync(KnowledgeDocumentRecord document, CancellationToken cancellationToken = default)
        => _upsertDocument(document, cancellationToken);

    public Task DeleteDocumentAsync(Guid documentId, CancellationToken cancellationToken = default)
        => _storage.DeleteDocumentAsync(documentId, cancellationToken);
}

public static class SemanticKnowledgeMigrationBuilderExtensions
{
    public static SemanticKnowledgeBuilder Migrate(
        this SemanticKnowledgeBuilder builder,
        int fromVersion,
        int toVersion,
        Func<KnowledgeMigrationContext, CancellationToken, Task> migration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(migration);
        if (fromVersion <= 0 || toVersion <= fromVersion)
            throw new ArgumentOutOfRangeException(nameof(toVersion), "A migration must move from a positive version to a greater version.");

        builder.Services.AddSingleton(new KnowledgeMigrationStep
        {
            FromVersion = fromVersion,
            ToVersion = toVersion,
            ApplyAsync = migration
        });
        return builder;
    }
}
