using OnnxTextEmbeddings;

namespace SemanticKnowledge;

/// <summary>
/// Supplies the pinned OnnxTextEmbeddings.NET 0.1.1 DefaultV1 candidate scoring contract when a caller uses
/// a non-ONNX embedding provider. UseOnnxEmbeddings registers the upstream implementation later and therefore
/// supersedes this fallback through normal Microsoft DI last-registration resolution.
/// </summary>
internal sealed class DefaultCandidateReranker : ISemanticCandidateReranker
{
    private const float MinimumLengthConfidence = 0.96f;
    private const float SupportWindow = 0.12f;
    private const float SecondSupportWeight = 0.25f;
    private const float ThirdSupportWeight = 0.10f;

    public Task<DatabaseSemanticSearchResult<TKey>> RerankAsync<TKey>(
        QueryEmbedding query,
        SemanticCandidateBatch<TKey> candidates,
        DatabaseSemanticSearchOptions? options = null,
        CancellationToken cancellationToken = default)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(candidates);
        options ??= new DatabaseSemanticSearchOptions();
        _ = options.ResolveCandidateCount();

        var ranked = new List<SemanticSearchResult<TKey>>();
        foreach (var itemGroup in candidates.Candidates.GroupBy(candidate => candidate.ItemKey))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ScoreItem(query, itemGroup.Key, itemGroup, options.IncludeAllChunkMatches) is { } scored)
                ranked.Add(scored);
        }

        ranked.Sort((left, right) => right.Score.CompareTo(left.Score));
        if (ranked.Count > options.Top)
            ranked.RemoveRange(options.Top, ranked.Count - options.Top);

        return Task.FromResult(new DatabaseSemanticSearchResult<TKey>
        {
            Results = ranked,
            Retrieval = candidates.Retrieval
        });
    }

    private static SemanticSearchResult<TKey>? ScoreItem<TKey>(
        QueryEmbedding query,
        TKey item,
        IEnumerable<SemanticCandidate<TKey>> candidates,
        bool includeAllMatches)
        where TKey : notnull
    {
        var fieldMatches = new List<SemanticFieldMatch>();
        SemanticChunkMatch? bestOverall = null;

        foreach (var fieldGroup in candidates.GroupBy(candidate => candidate.FieldName, StringComparer.Ordinal))
        {
            var weights = fieldGroup.Select(candidate => candidate.FieldWeight).Distinct().ToArray();
            if (weights.Length != 1)
                throw new ArgumentException($"Candidate field '{fieldGroup.Key}' contains conflicting field weights.", nameof(candidates));
            var weight = weights[0];
            if (weight < 0)
                throw new ArgumentOutOfRangeException(nameof(candidates), "Semantic field weight cannot be negative.");
            if (weight == 0)
                continue;

            var matches = fieldGroup
                .Select(candidate => ScoreChunk(query, candidate.Embedding))
                .OrderByDescending(match => match.AdjustedSimilarity)
                .ToArray();
            if (matches.Length == 0)
                continue;

            var fieldScore = AggregateEvidence(matches.Select(match => match.AdjustedSimilarity).ToArray());
            var weightedScore = ApplyFieldWeight(fieldScore, weight);
            fieldMatches.Add(new SemanticFieldMatch
            {
                Name = fieldGroup.Key,
                Weight = weight,
                Score = fieldScore,
                WeightedScore = weightedScore,
                Matches = includeAllMatches ? matches : matches.Take(3).ToArray()
            });
            if (bestOverall is null || matches[0].AdjustedSimilarity > bestOverall.AdjustedSimilarity)
                bestOverall = matches[0];
        }

        if (fieldMatches.Count == 0 || bestOverall is null)
            return null;

        return new SemanticSearchResult<TKey>
        {
            Item = item,
            Score = AggregateEvidence(fieldMatches.Select(match => match.WeightedScore).OrderByDescending(score => score).ToArray()),
            BestMatch = bestOverall,
            Fields = fieldMatches.OrderByDescending(match => match.WeightedScore).ToArray(),
            Scoring = new SemanticScoringInfo(SemanticScoringProfiles.DefaultV1, 1)
        };
    }

    private static SemanticChunkMatch ScoreChunk(QueryEmbedding query, TextEmbedding embedding)
    {
        if (!string.Equals(query.Identity.EmbeddingSpaceFingerprint, embedding.Identity.EmbeddingSpaceFingerprint, StringComparison.Ordinal))
            throw new EmbeddingSpaceMismatchException($"Query embedding space '{query.Identity.EmbeddingSpaceFingerprint}' does not match candidate space '{embedding.Identity.EmbeddingSpaceFingerprint}'.");

        var rawSimilarity = EmbeddingVectorMath.CosineSimilarity(query.Vector, embedding.Vector);
        var coverage = embedding.Source.TokenCapacity <= 0
            ? 1f
            : Math.Clamp((float)embedding.Source.TokenCount / embedding.Source.TokenCapacity, 0f, 1f);
        var confidence = MinimumLengthConfidence + (1f - MinimumLengthConfidence) * MathF.Sqrt(coverage);
        var adjustedSimilarity = Math.Max(0f, rawSimilarity) * confidence;
        return new SemanticChunkMatch
        {
            Embedding = embedding,
            RawSimilarity = rawSimilarity,
            LengthConfidence = confidence,
            AdjustedSimilarity = adjustedSimilarity
        };
    }

    private static float AggregateEvidence(IReadOnlyList<float> scores)
    {
        if (scores.Count == 0)
            return 0f;
        var best = scores[0];
        var total = best;
        if (scores.Count > 1)
            total += SupportBonus(best, scores[1], SecondSupportWeight);
        if (scores.Count > 2)
            total += SupportBonus(best, scores[2], ThirdSupportWeight);
        return Math.Min(1f, total);
    }

    private static float SupportBonus(float best, float support, float weight)
    {
        var strength = Math.Clamp(1f - ((best - support) / SupportWindow), 0f, 1f);
        return (1f - best) * weight * strength * support;
    }

    private static float ApplyFieldWeight(float score, float weight) =>
        weight == 0f ? 0f : 1f - MathF.Pow(1f - Math.Clamp(score, 0f, 1f), weight);
}
