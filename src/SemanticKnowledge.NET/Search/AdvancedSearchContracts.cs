using OnnxTextEmbeddings;

namespace SemanticKnowledge;

public enum KnowledgeRetrievalKind { Semantic = 1, Lexical = 2 }
public enum KnowledgeLexicalQueryMode { NaturalLanguage = 1, NativeSyntax = 2 }

public sealed record KnowledgeSearchField
{
    public KnowledgeSearchField(string fieldKey, float weight = 1f)
    {
        FieldKey = KnowledgeSchemaBuilder.NormalizeKey(fieldKey);
        if (!float.IsFinite(weight) || weight <= 0) throw new ArgumentOutOfRangeException(nameof(weight), "Search field weight must be finite and greater than zero.");
        Weight = weight;
    }

    public string FieldKey { get; init; }
    public float Weight { get; init; }

    public static KnowledgeSearchField Create(string fieldKey, float weight = 1f) => new(fieldKey, weight);
    public static KnowledgeSearchField Title(float weight = 1f) => Create(KnowledgeSystemFields.Title, weight);
    public static KnowledgeSearchField Description(float weight = 1f) => Create(KnowledgeSystemFields.Description, weight);
    public static KnowledgeSearchField Tags(float weight = 1f) => Create(KnowledgeSystemFields.Tags, weight);
    public static KnowledgeSearchField Body(float weight = 1f) => Create(KnowledgeSystemFields.Body, weight);
}

public sealed record KnowledgeRetrievalStage
{
    public required string Name { get; init; }
    public required KnowledgeRetrievalKind Kind { get; init; }
    public IReadOnlyList<KnowledgeSearchField> Fields { get; init; } = Array.Empty<KnowledgeSearchField>();
    public KnowledgeFilter? Filter { get; init; }
    public int? CandidateCount { get; init; }
    public float Weight { get; init; } = 1f;
    public KnowledgeLexicalQueryMode LexicalMode { get; init; } = KnowledgeLexicalQueryMode.NaturalLanguage;

    public static KnowledgeRetrievalStage Semantic(params KnowledgeSearchField[] fields) => Semantic("semantic", fields);
    public static KnowledgeRetrievalStage Semantic(string name, params KnowledgeSearchField[] fields) => new() { Name = name, Kind = KnowledgeRetrievalKind.Semantic, Fields = fields };
    public static KnowledgeRetrievalStage Lexical(params KnowledgeSearchField[] fields) => Lexical("lexical", fields);
    public static KnowledgeRetrievalStage Lexical(string name, params KnowledgeSearchField[] fields) => new() { Name = name, Kind = KnowledgeRetrievalKind.Lexical, Fields = fields };
    public KnowledgeRetrievalStage Where(KnowledgeFilter filter) => this with { Filter = KnowledgeFilters.CombineAnd(Filter, filter) };
    public KnowledgeRetrievalStage Candidates(int count) => count > 0 ? this with { CandidateCount = count } : throw new ArgumentOutOfRangeException(nameof(count));
    public KnowledgeRetrievalStage WithWeight(float weight) => float.IsFinite(weight) && weight >= 0 ? this with { Weight = weight } : throw new ArgumentOutOfRangeException(nameof(weight));
    public KnowledgeRetrievalStage UseNativeSyntax() => Kind == KnowledgeRetrievalKind.Lexical ? this with { LexicalMode = KnowledgeLexicalQueryMode.NativeSyntax } : throw new InvalidOperationException("Native lexical syntax only applies to lexical retrieval stages.");
}

public sealed class KnowledgeSearchQuery
{
    private readonly List<KnowledgeRetrievalStage> _stages = [];

    private KnowledgeSearchQuery(Guid knowledgeBaseId, string text)
    {
        if (knowledgeBaseId == Guid.Empty) throw new ArgumentException("KnowledgeBaseId cannot be empty.", nameof(knowledgeBaseId));
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        KnowledgeBaseId = knowledgeBaseId;
        Text = text;
    }

    public Guid KnowledgeBaseId { get; }
    public string Text { get; }
    public KnowledgeSearchMode Mode { get; private set; } = KnowledgeSearchMode.Global;
    public IReadOnlyList<Guid> CollectionIds { get; private set; } = Array.Empty<Guid>();
    public bool IncludeDescendants { get; private set; } = true;
    public KnowledgeFilter? Filter { get; private set; }
    public KnowledgeFilter? PostFilter { get; private set; }
    public int Top { get; private set; } = 10;
    public int FusionRankConstant { get; private set; } = 60;
    public KnowledgeResultInclude Include { get; private set; } = KnowledgeResultInclude.MetadataOnly;
    public IReadOnlyList<KnowledgeRetrievalStage> Retrievals => _stages;

    public static KnowledgeSearchQuery Create(Guid knowledgeBaseId, string text) => new(knowledgeBaseId, text);

    public KnowledgeSearchQuery Add(KnowledgeRetrievalStage stage) { ArgumentNullException.ThrowIfNull(stage); _stages.Add(stage); return this; }
    public KnowledgeSearchQuery Semantic(params KnowledgeSearchField[] fields) => Add(KnowledgeRetrievalStage.Semantic(fields));
    public KnowledgeSearchQuery Lexical(params KnowledgeSearchField[] fields) => Add(KnowledgeRetrievalStage.Lexical(fields));
    public KnowledgeSearchQuery Hybrid()
    {
        if (_stages.Count != 0) throw new InvalidOperationException("Hybrid() is convenience syntax for an empty query plan. Add explicit stages instead when composing a custom plan.");
        _stages.Add(KnowledgeRetrievalStage.Semantic());
        _stages.Add(KnowledgeRetrievalStage.Lexical());
        return this;
    }
    public KnowledgeSearchQuery Where(KnowledgeFilter filter) { Filter = KnowledgeFilters.CombineAnd(Filter, filter); return this; }
    public KnowledgeSearchQuery PostWhere(KnowledgeFilter filter) { PostFilter = KnowledgeFilters.CombineAnd(PostFilter, filter); return this; }
    public KnowledgeSearchQuery Global() { Mode = KnowledgeSearchMode.Global; CollectionIds = Array.Empty<Guid>(); return this; }
    public KnowledgeSearchQuery Smart() { Mode = KnowledgeSearchMode.Smart; return this; }
    public KnowledgeSearchQuery Scoped(IEnumerable<Guid> collectionIds, bool includeDescendants = true)
    {
        ArgumentNullException.ThrowIfNull(collectionIds);
        var ids = collectionIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        if (ids.Length == 0) throw new ArgumentException("Scoped search requires at least one collection.", nameof(collectionIds));
        Mode = KnowledgeSearchMode.Scoped; CollectionIds = ids; IncludeDescendants = includeDescendants; return this;
    }
    public KnowledgeSearchQuery Take(int top) { if (top <= 0) throw new ArgumentOutOfRangeException(nameof(top)); Top = top; return this; }
    public KnowledgeSearchQuery UseReciprocalRankFusion(int rankConstant = 60) { if (rankConstant < 0) throw new ArgumentOutOfRangeException(nameof(rankConstant)); FusionRankConstant = rankConstant; return this; }
    public KnowledgeSearchQuery IncludeResults(KnowledgeResultInclude include) { Include = include; return this; }

    public void Validate()
    {
        if (_stages.Count == 0) throw new InvalidOperationException("A KnowledgeSearchQuery requires at least one retrieval stage. Call Semantic(), Lexical(), Hybrid(), or Add().");
        if (!_stages.Any(stage => stage.Weight > 0)) throw new InvalidOperationException("A KnowledgeSearchQuery requires at least one retrieval stage with a positive weight.");
        if (_stages.Select(stage => stage.Name).Distinct(StringComparer.Ordinal).Count() != _stages.Count) throw new InvalidOperationException("Knowledge retrieval stage names must be unique.");
        foreach (var stage in _stages)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(stage.Name);
            if (!float.IsFinite(stage.Weight) || stage.Weight < 0) throw new InvalidOperationException($"Stage '{stage.Name}' has an invalid weight.");
            if (stage.CandidateCount is <= 0) throw new InvalidOperationException($"Stage '{stage.Name}' candidate count must be positive.");
            if (stage.Fields.Select(field => field.FieldKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() != stage.Fields.Count) throw new InvalidOperationException($"Stage '{stage.Name}' contains duplicate fields.");
            foreach (var field in stage.Fields)
            {
                if (string.IsNullOrWhiteSpace(field.FieldKey)) throw new InvalidOperationException($"Stage '{stage.Name}' contains an empty field key.");
                if (!float.IsFinite(field.Weight) || field.Weight <= 0) throw new InvalidOperationException($"Field '{field.FieldKey}' must have a finite positive weight.");
            }
        }
    }

    public int ResolveCandidateCount(KnowledgeRetrievalStage stage) => stage.CandidateCount ?? (int)Math.Min(int.MaxValue, Math.Max(100L, (long)Top * 10L));
}

public sealed record KnowledgeSearchContribution
{
    public required string StageName { get; init; }
    public required KnowledgeRetrievalKind Kind { get; init; }
    public required int Rank { get; init; }
    public required float RawScore { get; init; }
    public required float FusionContribution { get; init; }
    public IReadOnlyList<KnowledgeLexicalFieldMatch> LexicalFields { get; init; } = Array.Empty<KnowledgeLexicalFieldMatch>();
}

public sealed record KnowledgeLexicalFieldMatch
{
    public required string FieldKey { get; init; }
    public required float Weight { get; init; }
    public required float Score { get; init; }
}

/// <summary>Provider SPI record for rebuildable lexical source text.</summary>
public sealed record LexicalSourceRecord
{
    public required Guid Id { get; init; }
    public required Guid KnowledgeBaseId { get; init; }
    public required Guid CollectionId { get; init; }
    public required Guid ItemId { get; init; }
    public required SemanticEntityKind EntityKind { get; init; }
    public Guid? DocumentId { get; init; }
    public required Guid FieldId { get; init; }
    public required string FieldKey { get; init; }
    public required string Text { get; init; }
}

/// <summary>Provider SPI candidate returned before canonical document hydration and optional post-filtering.</summary>
public sealed record KnowledgeAdvancedSearchCandidate
{
    public required Guid DocumentId { get; init; }
    public required float Score { get; init; }
    public IReadOnlyList<KnowledgeMatchedChunk> Matches { get; init; } = Array.Empty<KnowledgeMatchedChunk>();
    public IReadOnlyList<KnowledgeSearchContribution> Contributions { get; init; } = Array.Empty<KnowledgeSearchContribution>();
}

/// <summary>Storage-provider SPI for derived lexical indexing and composable semantic/lexical retrieval.</summary>
public interface IKnowledgeAdvancedSearchProvider
{
    string ProviderName { get; }
    Task<bool> InitializeAsync(CancellationToken cancellationToken = default);
    Task UpsertSourcesAsync(Guid itemId, SemanticEntityKind kind, IReadOnlyList<LexicalSourceRecord> sources, CancellationToken cancellationToken = default);
    Task DeleteSourcesAsync(Guid itemId, SemanticEntityKind kind, CancellationToken cancellationToken = default);
    Task ResetAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<KnowledgeAdvancedSearchCandidate>> SearchAsync(KnowledgeSearchQuery query, QueryEmbedding? semanticQuery, CancellationToken cancellationToken = default);
}
