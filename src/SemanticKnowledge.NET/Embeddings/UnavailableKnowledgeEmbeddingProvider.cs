using OnnxTextEmbeddings;

namespace SemanticKnowledge;

// Deliberately never supplies dummy vectors or model identities. Every caller
// must honor lexical-only capability before reaching this guard.
internal sealed class UnavailableKnowledgeEmbeddingProvider : IKnowledgeEmbeddingProvider
{
    private static NotSupportedException Disabled() => new("Embeddings are unavailable in lexical-only mode.");
    public Task<KnowledgeEmbeddingProviderInfo> GetInfoAsync(CancellationToken cancellationToken = default) => throw Disabled();
    public Task<QueryEmbedding> EmbedQueryAsync(string text, CancellationToken cancellationToken = default) => throw Disabled();
    public Task<IReadOnlyList<TextEmbedding>> EmbedDocumentAsync(string text, CancellationToken cancellationToken = default) => throw Disabled();
    public Task<int?> TryCountTokensAsync(string text, CancellationToken cancellationToken = default) => throw Disabled();
}
