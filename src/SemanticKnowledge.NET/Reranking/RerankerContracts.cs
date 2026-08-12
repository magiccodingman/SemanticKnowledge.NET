namespace SemanticKnowledge;

public sealed record KnowledgeRerankerCapabilities
{
    public required string Provider { get; init; }
    public bool ReturnsContribution { get; init; }
    public bool ReturnsEvidence { get; init; }
    public int? MaximumInputTokens { get; init; }
}

public sealed record KnowledgeRerankCandidate
{
    public required Guid DocumentId { get; init; }
    public required string Title { get; init; }
    public required IReadOnlyList<KnowledgeContentEvidence> Evidence { get; init; }
    public required float RetrievalScore { get; init; }
}

public sealed record KnowledgeRerankRequest
{
    public required string Query { get; init; }
    public required IReadOnlyList<KnowledgeRerankCandidate> Candidates { get; init; }
}

public sealed record KnowledgeRerankedCandidate
{
    public required Guid DocumentId { get; init; }
    public required float Score { get; init; }
    public string? Contribution { get; init; }
    public string? Evidence { get; init; }
}

/// <summary>
/// Optional future second-stage neural reranker. SemanticKnowledge v1 never resolves or invokes this interface by
/// default. Implementations must receive an already bounded candidate set rather than the stored corpus.
/// </summary>
public interface IKnowledgeReranker
{
    KnowledgeRerankerCapabilities Capabilities { get; }

    Task<IReadOnlyList<KnowledgeRerankedCandidate>> RerankAsync(
        KnowledgeRerankRequest request,
        CancellationToken cancellationToken = default);
}
