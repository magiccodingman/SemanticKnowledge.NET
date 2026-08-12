using Microsoft.Extensions.DependencyInjection;
using OnnxTextEmbeddings;

namespace SemanticKnowledge;

/// <summary>
/// Resolves the pinned OnnxTextEmbeddings.NET candidate reranker in an isolated container for deployments that
/// use HTTP/custom embedding providers. Warmup is disabled and the isolated embedding service is never invoked;
/// only the upstream candidate-scoring implementation is delegated to.
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
}
