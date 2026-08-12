using OnnxTextEmbeddings;

namespace SemanticKnowledge;

/// <summary>Describes whether vectors in a semantic coordinate space are known to be normalized.</summary>
public enum KnowledgeEmbeddingNormalization
{
    Unknown = 0,
    NotNormalized = 1,
    Normalized = 2
}

/// <summary>Lifecycle state of a semantic coordinate space exposed by a store.</summary>
public enum KnowledgeEmbeddingSpaceStatus
{
    ActiveQueryable = 1,
    Rebuilding = 2,
    Retired = 3
}

/// <summary>
/// Public mathematical/capability descriptor for an embedding space accepted by SemanticKnowledge.
/// The fingerprint is the compatibility authority; dimensions/model names are descriptive only.
/// </summary>
public sealed record KnowledgeEmbeddingSpaceDescriptor
{
    public required string EmbeddingSpaceFingerprint { get; init; }
    public required string Provider { get; init; }
    public required string ModelId { get; init; }
    public required string SourceRevision { get; init; }
    public required int NativeDimensions { get; init; }
    public required int Dimensions { get; init; }
    public required string CoordinateSpace { get; init; }
    public KnowledgeEmbeddingNormalization Normalization { get; init; }
    public string? DimensionReductionProfile { get; init; }
    public required string PhysicalVectorStorage { get; init; }
    public required KnowledgeEmbeddingSpaceStatus Status { get; init; }
    public required bool Queryable { get; init; }
    public IReadOnlyList<EmbeddingVectorFormat> SupportedQueryVectorFormats { get; init; } = Array.Empty<EmbeddingVectorFormat>();
}
