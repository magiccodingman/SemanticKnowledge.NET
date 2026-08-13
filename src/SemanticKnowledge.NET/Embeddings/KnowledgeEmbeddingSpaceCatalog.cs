using OnnxTextEmbeddings;

namespace SemanticKnowledge;

/// <summary>
/// Enumerates the semantic coordinate spaces currently accepted by a SemanticKnowledge store.
/// Today a store has one active queryable space; the collection shape is intentionally future-compatible
/// with stores that may expose multiple independently queryable spaces.
/// </summary>
public interface IKnowledgeEmbeddingSpaceCatalog
{
    Task<IReadOnlyList<KnowledgeEmbeddingSpaceDescriptor>> GetEmbeddingSpacesAsync(CancellationToken cancellationToken = default);
}

internal sealed class KnowledgeEmbeddingSpaceCatalog(
    ISemanticKnowledgeStore store,
    IKnowledgeEmbeddingProvider embeddings) : IKnowledgeEmbeddingSpaceCatalog
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<KnowledgeEmbeddingSpaceDescriptor>? _cached;

    public async Task<IReadOnlyList<KnowledgeEmbeddingSpaceDescriptor>> GetEmbeddingSpacesAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is not null) return _cached;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is not null) return _cached;
            var capabilities = await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var info = await embeddings.GetInfoAsync(cancellationToken).ConfigureAwait(false);
            var normalization = info.IsNormalized switch
            {
                true => KnowledgeEmbeddingNormalization.Normalized,
                false => KnowledgeEmbeddingNormalization.NotNormalized,
                null => KnowledgeEmbeddingNormalization.Unknown
            };

            _cached =
            [
                new KnowledgeEmbeddingSpaceDescriptor
                {
                    EmbeddingSpaceFingerprint = info.EmbeddingSpaceFingerprint,
                    Provider = info.Provider,
                    ModelId = info.ModelId,
                    SourceRevision = info.SourceRevision,
                    NativeDimensions = info.NativeDimensions,
                    Dimensions = info.OutputDimensions,
                    CoordinateSpace = info.CoordinateSpace,
                    Normalization = normalization,
                    DimensionReductionProfile = info.DimensionReductionProfile,
                    PhysicalVectorStorage = capabilities.PhysicalVectorStorage,
                    Status = KnowledgeEmbeddingSpaceStatus.ActiveQueryable,
                    Queryable = true,
                    SupportedQueryVectorFormats = Enum.GetValues<EmbeddingVectorFormat>()
                }
            ];
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }
}
