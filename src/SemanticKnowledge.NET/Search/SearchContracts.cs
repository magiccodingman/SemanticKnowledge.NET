using OnnxTextEmbeddings;

namespace SemanticKnowledge;

public enum KnowledgeSearchMode { Global = 1, Scoped = 2, Smart = 3 }
public enum KnowledgeResultInclude { MetadataOnly = 1, MatchedChunks = 2, FullDocument = 3 }

public sealed record KnowledgeSearchRequest
{
    public required Guid KnowledgeBaseId { get; init; }
    public KnowledgeSearchMode Mode { get; init; } = KnowledgeSearchMode.Global;
    public IReadOnlyList<Guid> CollectionIds { get; init; } = Array.Empty<Guid>();
    public bool IncludeDescendants { get; init; } = true;
    public KnowledgeFilter? Filter { get; init; }
    public int Top { get; init; } = 10;
    public int? CandidateCount { get; init; }
    public KnowledgeResultInclude Include { get; init; } = KnowledgeResultInclude.MetadataOnly;
}

public sealed record KnowledgeMatchedChunk
{
    public required string FieldKey { get; init; }
    public required float RawSimilarity { get; init; }
    public required float AdjustedSimilarity { get; init; }
    public required int TokenCount { get; init; }
    public required Utf16TextRange CharacterRange { get; init; }
    public string? Text { get; init; }
}

public sealed record KnowledgeSearchHit
{
    public required Guid DocumentId { get; init; }
    public required Guid CollectionId { get; init; }
    public required Guid SchemaId { get; init; }
    public required float Score { get; init; }
    public required string Title { get; init; }
    public string Description { get; init; } = string.Empty;
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public IReadOnlyList<KnowledgeMatchedChunk> Matches { get; init; } = Array.Empty<KnowledgeMatchedChunk>();
    public SemanticScoringInfo? Scoring { get; init; }
}

public sealed record KnowledgeContentEvidence
{
    public required Guid DocumentId { get; init; }
    public required string FieldKey { get; init; }
    public required string Text { get; init; }
    public required int TokenCount { get; init; }
    public required Utf16TextRange CharacterRange { get; init; }
}

public sealed record KnowledgeContentResult
{
    public required IReadOnlyList<KnowledgeSearchHit> Hits { get; init; }
    public required IReadOnlyList<KnowledgeContentEvidence> Evidence { get; init; }
    public required int ApproximateTokenCount { get; init; }
}
