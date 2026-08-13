using OnnxTextEmbeddings;

namespace SemanticKnowledge;

/// <summary>
/// Keeps optional snapshot persistence outside the base storage contract while ensuring destructive ResetAsync
/// removes snapshot tables before providers drop their canonical Collection tables.
/// </summary>
internal sealed class SnapshotAwareSemanticKnowledgeStore(
    SemanticKnowledgeStore inner,
    IEnumerable<IKnowledgeCollectionSnapshotResetter> snapshotResetters) : ISemanticKnowledgeStore
{
    private readonly IKnowledgeCollectionSnapshotResetter[] _snapshotResetters = snapshotResetters.ToArray();

    public Task<KnowledgeProviderCapabilities> InitializeAsync(CancellationToken cancellationToken = default) => inner.InitializeAsync(cancellationToken);
    public Task<KnowledgeBaseRecord> GetOrCreateKnowledgeBaseAsync(string title, string? externalId = null, CancellationToken cancellationToken = default) => inner.GetOrCreateKnowledgeBaseAsync(title, externalId, cancellationToken);
    public Task<KnowledgeCollectionRecord> GetOrCreateCollectionAsync(Guid knowledgeBaseId, string title, Guid? parentCollectionId = null, Guid? defaultSchemaId = null, string? externalId = null, CancellationToken cancellationToken = default) => inner.GetOrCreateCollectionAsync(knowledgeBaseId, title, parentCollectionId, defaultSchemaId, externalId, cancellationToken);
    public Task<KnowledgeSchemaDefinition> EnsureSchemaAsync(KnowledgeSchemaDefinition schema, CancellationToken cancellationToken = default) => inner.EnsureSchemaAsync(schema, cancellationToken);
    public Task<Guid> UpsertDocumentAsync(KnowledgeDocumentInput document, CancellationToken cancellationToken = default) => inner.UpsertDocumentAsync(document, cancellationToken);
    public Task<KnowledgeDocumentRecord?> GetDocumentAsync(Guid documentId, CancellationToken cancellationToken = default) => inner.GetDocumentAsync(documentId, cancellationToken);
    public Task<IReadOnlyList<KnowledgeSearchHit>> SearchAsync(string query, KnowledgeSearchRequest request, CancellationToken cancellationToken = default) => inner.SearchAsync(query, request, cancellationToken);
    public Task<IReadOnlyList<KnowledgeSearchHit>> SearchAsync(QueryEmbedding query, KnowledgeSearchRequest request, CancellationToken cancellationToken = default) => inner.SearchAsync(query, request, cancellationToken);
    public Task<IReadOnlyList<KnowledgeSearchHit>> SearchAsync(KnowledgeSearchQuery query, CancellationToken cancellationToken = default) => inner.SearchAsync(query, cancellationToken);
    public Task DeleteDocumentAsync(Guid documentId, CancellationToken cancellationToken = default) => inner.DeleteDocumentAsync(documentId, cancellationToken);

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await inner.InitializeAsync(cancellationToken).ConfigureAwait(false);
        foreach (var resetter in _snapshotResetters)
            await resetter.ResetAsync(cancellationToken).ConfigureAwait(false);
        await inner.ResetAsync(cancellationToken).ConfigureAwait(false);
    }
}
