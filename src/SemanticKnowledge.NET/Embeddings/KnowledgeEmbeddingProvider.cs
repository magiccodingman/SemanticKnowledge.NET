using Microsoft.Extensions.DependencyInjection;
using OnnxTextEmbeddings;

namespace SemanticKnowledge;

public sealed record KnowledgeEmbeddingProviderInfo
{
    public required string Provider { get; init; }
    public required string ModelId { get; init; }
    public required string SourceRevision { get; init; }
    public required string EmbeddingSpaceFingerprint { get; init; }
    public required int NativeDimensions { get; init; }
    public required int OutputDimensions { get; init; }
    public bool SupportsTokenCounting { get; init; }
    public bool SupportsChunkedDocuments { get; init; }
}

public interface IKnowledgeEmbeddingProvider
{
    Task<KnowledgeEmbeddingProviderInfo> GetInfoAsync(CancellationToken cancellationToken = default);
    Task<QueryEmbedding> EmbedQueryAsync(string text, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TextEmbedding>> EmbedDocumentAsync(string text, CancellationToken cancellationToken = default);
    Task<int?> TryCountTokensAsync(string text, CancellationToken cancellationToken = default);
}

internal sealed class OnnxKnowledgeEmbeddingProvider(IServiceProvider services, SemanticKnowledgeOptions options) : IKnowledgeEmbeddingProvider
{
    private ITextEmbeddingService Service => services.GetRequiredService<ITextEmbeddingService>();

    public async Task<KnowledgeEmbeddingProviderInfo> GetInfoAsync(CancellationToken cancellationToken = default)
    {
        var service = Service;
        await service.WaitUntilReadyAsync(cancellationToken).ConfigureAwait(false);
        var info = service.ModelInfo ?? throw new InvalidOperationException("The ONNX embedding service is ready but did not expose ModelInfo.");
        var nativeDimensions = info.Dimensions ?? throw new InvalidOperationException("The loaded embedding model did not report dimensions.");
        var output = ResolveOutputDimensions(nativeDimensions);
        var fingerprint = info.EmbeddingSpaceFingerprint;
        if (output != nativeDimensions)
        {
            var probe = await service.EmbedQueryAsync("semantic-knowledge-dimension-probe", EmbeddingVectorFormat.Float32, cancellationToken).ConfigureAwait(false);
            fingerprint = probe.ReduceDimensions(output, EmbeddingVectorFormat.Float32).Identity.EmbeddingSpaceFingerprint;
        }
        return new KnowledgeEmbeddingProviderInfo { Provider = "OnnxTextEmbeddings.NET", ModelId = info.ModelId, SourceRevision = info.SourceRevision, EmbeddingSpaceFingerprint = fingerprint, NativeDimensions = nativeDimensions, OutputDimensions = output, SupportsTokenCounting = true, SupportsChunkedDocuments = true };
    }

    public async Task<QueryEmbedding> EmbedQueryAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var service = Service;
        await service.WaitUntilReadyAsync(cancellationToken).ConfigureAwait(false);
        var query = await service.EmbedQueryAsync(text, EmbeddingVectorFormat.Float32, cancellationToken).ConfigureAwait(false);
        var output = ResolveOutputDimensions(query.Vector.Dimensions);
        return output == query.Vector.Dimensions ? query : query.ReduceDimensions(output, EmbeddingVectorFormat.Float32);
    }

    public async Task<IReadOnlyList<TextEmbedding>> EmbedDocumentAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        var service = Service;
        await service.WaitUntilReadyAsync(cancellationToken).ConfigureAwait(false);
        var records = await service.EmbedDocumentAsync(text, EmbeddingVectorFormat.Float32, cancellationToken).ConfigureAwait(false);
        if (records.Count == 0) return records;
        var output = ResolveOutputDimensions(records[0].Vector.Dimensions);
        var format = options.Embeddings.PersistedRecordFormat;
        return records.Select(record =>
        {
            var reduced = output == record.Vector.Dimensions ? record : record.ReduceDimensions(output, EmbeddingVectorFormat.Float32);
            return reduced with { Vector = reduced.Vector.ConvertTo(format) };
        }).ToArray();
    }

    public Task<int?> TryCountTokensAsync(string text, CancellationToken cancellationToken = default) =>
        CountTokensAsync(Service, text, cancellationToken);

    private static async Task<int?> CountTokensAsync(ITextEmbeddingService service, string text, CancellationToken cancellationToken) =>
        await service.CountTokensAsync(text, cancellationToken).ConfigureAwait(false);

    private int ResolveOutputDimensions(int nativeDimensions)
    {
        var output = options.Embeddings.OutputDimensions ?? nativeDimensions;
        if (output > nativeDimensions) throw new InvalidOperationException($"Configured OutputDimensions {output} exceeds native dimensions {nativeDimensions}. Dimension expansion is not allowed.");
        return output;
    }
}
