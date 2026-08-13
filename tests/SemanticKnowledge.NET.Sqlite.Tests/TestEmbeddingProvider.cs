using OnnxTextEmbeddings;

namespace SemanticKnowledge.Sqlite.Tests;

internal sealed class TestEmbeddingProvider(string fingerprint = "test-space-v1") : IKnowledgeEmbeddingProvider
{
    private readonly EmbeddingIdentity _identity = new()
    {
        ModelId = "semantic-knowledge-tests",
        SourceRevision = fingerprint,
        EmbeddingSpaceFingerprint = fingerprint,
        IsNormalized = true
    };

    public Task<KnowledgeEmbeddingProviderInfo> GetInfoAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new KnowledgeEmbeddingProviderInfo
        {
            Provider = "test",
            ModelId = _identity.ModelId,
            SourceRevision = _identity.SourceRevision,
            EmbeddingSpaceFingerprint = _identity.EmbeddingSpaceFingerprint,
            NativeDimensions = 4,
            OutputDimensions = 4,
            CoordinateSpace = "dense-test",
            IsNormalized = true,
            SupportsTokenCounting = true,
            SupportsChunkedDocuments = true
        });

    public Task<QueryEmbedding> EmbedQueryAsync(string text, CancellationToken cancellationToken = default)
    {
        var tokens = Count(text);
        return Task.FromResult(new QueryEmbedding
        {
            Vector = EmbeddingVector.FromFloat32(VectorFor(text), EmbeddingVectorFormat.Float32),
            Identity = _identity,
            SourceTokenCount = tokens,
            InputTokenCount = tokens
        });
    }

    public Task<IReadOnlyList<TextEmbedding>> EmbedDocumentAsync(string text, CancellationToken cancellationToken = default)
    {
        var tokens = Count(text);
        IReadOnlyList<TextEmbedding> result = [new TextEmbedding
        {
            Vector = EmbeddingVector.FromFloat32(VectorFor(text), EmbeddingVectorFormat.Int8),
            Identity = _identity,
            Source = new EmbeddingSource
            {
                DocumentTokenCount = tokens,
                CharacterRange = new Utf16TextRange(0, text.Length),
                TokenRange = new TokenRange(0, tokens),
                TokenCount = tokens,
                TokenCapacity = Math.Max(tokens, 1)
            },
            Chunk = new EmbeddingChunkInfo
            {
                Index = 0,
                Count = 1,
                BoundaryKind = ChunkBoundaryKind.WholeDocument,
                InputTokenCount = tokens
            },
            Text = text
        }];
        return Task.FromResult(result);
    }

    public Task<int?> TryCountTokensAsync(string text, CancellationToken cancellationToken = default) =>
        Task.FromResult<int?>(Count(text));

    private static int Count(string text) => Math.Max(1, text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);

    private static float[] VectorFor(string text)
    {
        if (text.Contains("alpha", StringComparison.OrdinalIgnoreCase) || text.Contains("postgres", StringComparison.OrdinalIgnoreCase) || text.Contains("backup", StringComparison.OrdinalIgnoreCase))
            return [1f, 0f, 0f, 0f];
        if (text.Contains("beta", StringComparison.OrdinalIgnoreCase) || text.Contains("dragon", StringComparison.OrdinalIgnoreCase))
            return [0f, 1f, 0f, 0f];
        if (text.Contains("gamma", StringComparison.OrdinalIgnoreCase))
            return [0f, 0f, 1f, 0f];
        return [0f, 0f, 0f, 1f];
    }
}
