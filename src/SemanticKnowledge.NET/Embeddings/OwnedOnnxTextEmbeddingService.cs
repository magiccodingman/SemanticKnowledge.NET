using Microsoft.Extensions.DependencyInjection;
using OnnxTextEmbeddings;

namespace SemanticKnowledge;

/// <summary>
/// Owns an upstream OnnxTextEmbeddings.NET service graph when SemanticKnowledge is asked to configure ONNX
/// embeddings itself. The facade keeps the upstream service reusable through ITextEmbeddingService while making
/// synchronous host disposal safe by bridging the child provider's async disposal.
/// </summary>
internal sealed class OwnedOnnxTextEmbeddingService : ITextEmbeddingService, IDisposable, IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly ITextEmbeddingService _inner;
    private int _disposed;

    public OwnedOnnxTextEmbeddingService(Action<OnnxTextEmbeddingsOptions>? configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOnnxTextEmbeddings(configure);
        _provider = services.BuildServiceProvider();
        _inner = _provider.GetRequiredService<ITextEmbeddingService>();
    }

    public EmbeddingServiceStatus Status => _inner.Status;
    public ModelRuntimeInfo? ModelInfo => _inner.ModelInfo;

    public Task WaitUntilReadyAsync(CancellationToken cancellationToken = default) => _inner.WaitUntilReadyAsync(cancellationToken);
    public Task<bool> UpdateModelAsync(CancellationToken cancellationToken = default) => _inner.UpdateModelAsync(cancellationToken);
    public Task<int> CountTokensAsync(string text, CancellationToken cancellationToken = default) => _inner.CountTokensAsync(text, cancellationToken);
    public Task<QueryTokenCount> CountQueryTokensAsync(string query, CancellationToken cancellationToken = default) => _inner.CountQueryTokensAsync(query, cancellationToken);
    public Task<IReadOnlyList<TextEmbedding>> EmbedAsync(string text, CancellationToken cancellationToken = default) => _inner.EmbedAsync(text, cancellationToken);
    public Task<IReadOnlyList<TextEmbedding>> EmbedDocumentAsync(string text, CancellationToken cancellationToken = default) => _inner.EmbedDocumentAsync(text, cancellationToken);
    public Task<QueryEmbedding> EmbedQueryAsync(string query, CancellationToken cancellationToken = default) => _inner.EmbedQueryAsync(query, cancellationToken);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _provider.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _provider.DisposeAsync().ConfigureAwait(false);
    }
}
