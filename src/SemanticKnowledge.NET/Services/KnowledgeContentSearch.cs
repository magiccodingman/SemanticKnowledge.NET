namespace SemanticKnowledge;

public interface IKnowledgeContentSearch
{
    Task<KnowledgeContentResult> SearchContentAsync(
        string query,
        KnowledgeSearchRequest request,
        int maxContentTokens,
        CancellationToken cancellationToken = default);
}

internal sealed class KnowledgeContentSearch(ISemanticKnowledgeStore store) : IKnowledgeContentSearch
{
    public async Task<KnowledgeContentResult> SearchContentAsync(
        string query,
        KnowledgeSearchRequest request,
        int maxContentTokens,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (maxContentTokens <= 0) throw new ArgumentOutOfRangeException(nameof(maxContentTokens));

        var searchRequest = request with { Include = KnowledgeResultInclude.MatchedChunks };
        var hits = await store.SearchAsync(query, searchRequest, cancellationToken).ConfigureAwait(false);

        var evidence = new List<KnowledgeContentEvidence>();
        var seen = new HashSet<EvidenceKey>();
        var usedTokens = 0;

        foreach (var hit in hits)
        {
            foreach (var match in hit.Matches.OrderByDescending(x => x.AdjustedSimilarity))
            {
                if (string.IsNullOrWhiteSpace(match.Text) || match.TokenCount <= 0) continue;
                var key = new EvidenceKey(hit.DocumentId, match.FieldKey, match.CharacterRange.Start, match.CharacterRange.Length);
                if (!seen.Add(key)) continue;
                if (usedTokens + match.TokenCount > maxContentTokens) continue;

                evidence.Add(new KnowledgeContentEvidence
                {
                    DocumentId = hit.DocumentId,
                    FieldKey = match.FieldKey,
                    Text = match.Text!,
                    TokenCount = match.TokenCount,
                    CharacterRange = match.CharacterRange
                });
                usedTokens += match.TokenCount;
                if (usedTokens >= maxContentTokens) break;
            }
            if (usedTokens >= maxContentTokens) break;
        }

        return new KnowledgeContentResult
        {
            Hits = hits,
            Evidence = evidence,
            ApproximateTokenCount = usedTokens
        };
    }

    private readonly record struct EvidenceKey(Guid DocumentId, string FieldKey, int Start, int Length);
}
