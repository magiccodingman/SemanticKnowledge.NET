using OnnxTextEmbeddings;

namespace SemanticKnowledge;

public enum SemanticEntityKind { Document = 1, Collection = 2 }

public sealed record SemanticSourceRecord
{
    public required Guid Id { get; init; }
    public required Guid KnowledgeBaseId { get; init; }
    public required Guid CollectionId { get; init; }
    public required Guid ItemId { get; init; }
    public required SemanticEntityKind EntityKind { get; init; }
    public Guid? DocumentId { get; init; }
    public required Guid FieldId { get; init; }
    public required string FieldKey { get; init; }
    public required float ScorerWeight { get; init; }
    public required TextEmbedding Embedding { get; init; }
}

public sealed record KnowledgeStorageInitialization
{
    public required int DatabaseVersion { get; init; }
    public required KnowledgePersistenceMode PersistenceMode { get; init; }
    public required KnowledgeEmbeddingProviderInfo Embedding { get; init; }
    public required VectorStoragePreference StoragePreference { get; init; }
}

public sealed record KnowledgeProviderCapabilities
{
    public required string Provider { get; init; }
    public required int MaxDimensions { get; init; }
    public required string PhysicalVectorStorage { get; init; }
    public required bool ExactVectorSearch { get; init; }
    public bool ApproximateVectorSearch { get; init; }
    public bool NativeAotSupported { get; init; }
    public bool RequiresEmbeddingRebuild { get; init; }
    public bool LexicalSearchSupported { get; init; }
    public string? LexicalSearchProvider { get; init; }
}

public interface IKnowledgeStorageProvider
{
    Task<KnowledgeProviderCapabilities> InitializeAsync(KnowledgeStorageInitialization initialization, CancellationToken cancellationToken = default);
    Task CompleteEmbeddingRebuildAsync(CancellationToken cancellationToken = default);
    Task<KnowledgeBaseRecord> GetOrCreateKnowledgeBaseAsync(string title, string? externalId = null, CancellationToken cancellationToken = default);
    Task<KnowledgeCollectionRecord> GetOrCreateCollectionAsync(Guid knowledgeBaseId, string title, Guid? parentCollectionId = null, Guid? defaultSchemaId = null, string? externalId = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<KnowledgeCollectionRecord>> GetCollectionsAsync(Guid? knowledgeBaseId = null, CancellationToken cancellationToken = default);
    Task UpsertCollectionSemanticSourcesAsync(KnowledgeCollectionRecord collection, IReadOnlyList<SemanticSourceRecord> semanticSources, CancellationToken cancellationToken = default);
    Task UpsertSchemaAsync(KnowledgeSchemaDefinition schema, CancellationToken cancellationToken = default);
    Task<KnowledgeSchemaDefinition?> GetSchemaAsync(Guid schemaId, CancellationToken cancellationToken = default);
    Task UpsertDocumentAsync(KnowledgeDocumentRecord document, IReadOnlyList<SemanticSourceRecord> semanticSources, CancellationToken cancellationToken = default);
    Task<KnowledgeDocumentRecord?> GetDocumentAsync(Guid documentId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<KnowledgeDocumentRecord>> GetDocumentsAsync(Guid? knowledgeBaseId = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<KnowledgeSearchHit>> SearchAsync(QueryEmbedding query, KnowledgeSearchRequest request, CancellationToken cancellationToken = default);
    Task DeleteDocumentAsync(Guid documentId, CancellationToken cancellationToken = default);
    Task ResetAsync(CancellationToken cancellationToken = default);
}
