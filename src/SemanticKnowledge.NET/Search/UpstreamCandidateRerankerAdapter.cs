using Microsoft.Extensions.DependencyInjection;
using OnnxTextEmbeddings;

namespace SemanticKnowledge;

/// <summary>
/// Resolves the pinned OnnxTextEmbeddings.NET candidate reranker in an isolated container for deployments that
/// use HTTP/custom embedding providers. The upstream reranker receives a scorer-only embedding-service stub because
/// its precomputed-query rerank path never embeds text. No ONNX model service is constructed, loaded, or downloaded.
/// </summary>
internal sealed class UpstreamCandidateRerankerAdapter : ISemanticCandidateReranker, IDisposable
{
    private readonly IServiceProvider _provider;
    private readonly ISemanticCandidateReranker _inner;

    public UpstreamCandidateRerankerAdapter()
    {
        var services = new ServiceCollection();
        services.AddOnnxTextEmbeddings(options =>
        {
            options.Initialization.WarmupOnStartup = false;
            options.Initialization.BlockHostStartupUntilReady = false;
        });
        services.AddSingleton<ITextEmbeddingService, ScorerOnlyEmbeddingService>();
        _provider = services.BuildServiceProvider();
        _inner = _provider.GetRequiredService<ISemanticCandidateReranker>();
    }

    public Task<DatabaseSemanticSearchResult<TKey>> RerankAsync<TKey>(
        QueryEmbedding query,
        SemanticCandidateBatch<TKey> candidates,
        DatabaseSemanticSearchOptions? options = null,
        CancellationToken cancellationToken = default)
        where TKey : notnull
        => _inner.RerankAsync(query, candidates, options, cancellationToken);

    public void Dispose()
    {
        if (_provider is IDisposable disposable)
            disposable.Dispose();
    }

    private sealed class ScorerOnlyEmbeddingService : ITextEmbeddingService, IDisposable
    {
        public EmbeddingServiceStatus Status { get; } = new(EmbeddingServiceState.Uninitialized, "Scorer-only adapter; embedding is disabled.");
        public ModelRuntimeInfo? ModelInfo => null;

        public Task WaitUntilReadyAsync(CancellationToken cancellationToken = default) => Disabled();
        public Task<bool> UpdateModelAsync(CancellationToken cancellationToken = default) => Disabled<bool>();
        public Task<int> CountTokensAsync(string text, CancellationToken cancellationToken = default) => Disabled<int>();
        public Task<QueryTokenCount> CountQueryTokensAsync(string query, CancellationToken cancellationToken = default) => Disabled<QueryTokenCount>();
        public Task<IReadOnlyList<TextEmbedding>> EmbedAsync(string text, CancellationToken cancellationToken = default) => Disabled<IReadOnlyList<TextEmbedding>>();
        public Task<IReadOnlyList<TextEmbedding>> EmbedDocumentAsync(string text, CancellationToken cancellationToken = default) => Disabled<IReadOnlyList<TextEmbedding>>();
        public Task<QueryEmbedding> EmbedQueryAsync(string query, CancellationToken cancellationToken = default) => Disabled<QueryEmbedding>();

        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static Task Disabled() => Task.FromException(new InvalidOperationException("The internal scorer-only embedding service cannot perform embedding operations."));
        private static Task<T> Disabled<T>() => Task.FromException<T>(new InvalidOperationException("The internal scorer-only embedding service cannot perform embedding operations."));
    }
}
