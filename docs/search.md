# Search

SemanticKnowledge has two search surfaces: the original semantic API for the common case, and a composable retrieval-plan API when you want lexical/BM25, hybrid retrieval, per-stage filters, field targeting, or explicit fusion control.

## Simple semantic search

`KnowledgeSearchRequest` remains semantic-only for backward compatibility.

**Global** searches documents across the selected KnowledgeBase. **Scoped** restricts retrieval to explicit Collections, optionally including descendants. **Smart** first searches Collection semantic identities and then searches documents inside the routed Collections.

```csharp
var request = new KnowledgeSearchRequest
{
    KnowledgeBaseId = kb.Id,
    Mode = KnowledgeSearchMode.Smart,
    Top = 10,
    CandidateCount = 100,
    Include = KnowledgeResultInclude.MatchedChunks
};

var hits = await store.SearchAsync("restore the postgres backup", request);
```

`CandidateCount` is optional. Providers use bounded candidate retrieval and rerank evidence using the pinned `OnnxTextEmbeddings.NET` scoring contract. `Top` controls final documents, not raw chunks.

You may also call the `QueryEmbedding` overload. SemanticKnowledge validates dimensions and embedding-space fingerprint before search; same dimensions alone are not considered compatibility.

## Lexical/BM25 search

All canonical `Text` fields are placed in a rebuildable native lexical index automatically. That includes `Title`, `Description`, `Tags`, `Body`, and custom text fields—even a custom field whose `SemanticMode` is `None`.

This is intentionally a query-time decision rather than another schema flag. You can decide later that a field should be searched lexically without changing the logical schema or re-embedding documents.

```csharp
var hits = await store.SearchAsync(
    KnowledgeSearchQuery.Create(kb.Id, "pg_restore production backup")
        .Lexical(
            KnowledgeSearchField.Title(4),
            KnowledgeSearchField.Tags(2),
            KnowledgeSearchField.Body())
        .Take(20));
```

A lexical-only retrieval plan does **not** call `EmbedQueryAsync` or create a query embedding. The store may still initialize/use its configured embedding provider for the semantic indexes it owns; lexical-only changes the query path, not the store's configured indexing capabilities.

The provider uses its native text-search engine:

- SQLite: FTS5 + BM25;
- PostgreSQL: `tsvector` + `ts_rank_cd`;
- SQL Server: Full-Text Search + native `RANK`.

Raw scores from those engines are provider-specific. SemanticKnowledge does not pretend a SQLite BM25 value is numerically comparable to a PostgreSQL rank or cosine similarity.

## Easy hybrid search

`Hybrid()` is convenience syntax for one semantic stage plus one lexical stage. Results are combined with Reciprocal Rank Fusion (RRF), using the RRF implementation supplied by `OnnxTextEmbeddings.NET` 0.1.2.

```csharp
var hits = await store.SearchAsync(
    KnowledgeSearchQuery.Create(kb.Id, "restore postgres backup")
        .Smart()
        .Hybrid()
        .Where(KnowledgeFilters.HasTag("operations"))
        .Take(20));
```

Smart hybrid routing can use both semantic Collection identity and lexical Collection matches. Exact Collection names/tags can therefore help routing while semantic Collection search still catches conceptually related branches.

## Explicit retrieval stages

For full control, add named stages yourself. Stages may independently choose semantic or lexical retrieval, selected fields, field weights, a structured filter, a candidate budget, and an RRF stage weight.

```csharp
var query = KnowledgeSearchQuery.Create(kb.Id, "restore postgres backup")
    .Smart()
    .Add(
        KnowledgeRetrievalStage.Semantic(
                "semantic-body",
                KnowledgeSearchField.Body(3),
                KnowledgeSearchField.Description())
            .Where(KnowledgeFilters.Gte("version", KnowledgeValue.From(2L)))
            .Candidates(150)
            .WithWeight(1.5f))
    .Add(
        KnowledgeRetrievalStage.Lexical(
                "exact-identity",
                KnowledgeSearchField.Title(8),
                KnowledgeSearchField.Tags(4))
            .Candidates(80)
            .WithWeight(2f))
    .UseReciprocalRankFusion(rankConstant: 60)
    .Take(20);

var hits = await store.SearchAsync(query);
```

Multiple semantic stages and multiple lexical stages are valid. This is useful when, for example, identity fields and long-form content should have different candidate budgets or filters.

### Field weights

Field weights affect ranking *within a retrieval stage*. Because runtime schema fields are stored as rows rather than permanent database columns, lexical field searches are performed natively per selected logical field and then rank-fused. You still get a single logical stage in the final result diagnostics.

A stage with no explicit fields searches every applicable text field in the KnowledgeBase.

## Structured filters

`Where(...)` is the common/global prefilter. It is pushed into database eligibility before candidate retrieval.

```csharp
var query = KnowledgeSearchQuery.Create(kb.Id, "incident recovery")
    .Hybrid()
    .Where(KnowledgeFilters.And(
        KnowledgeFilters.Gte("version", KnowledgeValue.From(2L)),
        KnowledgeFilters.HasTag("production")));
```

A stage can add another filter:

```csharp
KnowledgeRetrievalStage.Lexical("recent", KnowledgeSearchField.Body())
    .Where(KnowledgeFilters.Gte("version", KnowledgeValue.From(5L)));
```

The stage filter is ANDed with the global prefilter for that stage.

`PostWhere(...)` applies after stage fusion and canonical document hydration. It is useful when you deliberately want retrieval/fusion to happen first, but it can reduce the final count below `Top` because filtered-out candidates are not replaced by a second retrieval pass.

```csharp
query.PostWhere(KnowledgeFilters.HasTag("approved"));
```

## Scoped and Smart advanced search

The same hierarchy modes are available from `KnowledgeSearchQuery`:

```csharp
query.Global();
query.Scoped([collection.Id], includeDescendants: true);
query.Smart();
```

For Smart search, Collection Title, Description, and Tags participate in routing. A hybrid plan unions relevant Collections discovered by semantic and lexical routing before document retrieval.

## Provider-native lexical syntax

Natural-language lexical mode is the safe default. If the caller intentionally wants the underlying database query language, opt in per lexical stage:

```csharp
var lexical = KnowledgeRetrievalStage
    .Lexical("native", KnowledgeSearchField.Body())
    .UseNativeSyntax();
```

Native syntax is deliberately provider-specific:

- SQLite: FTS5 query syntax;
- PostgreSQL: native `to_tsquery` syntax;
- SQL Server: `CONTAINSTABLE` syntax.

Do not pass arbitrary end-user input to native syntax mode without treating it as a provider query language. Use normal lexical mode for ordinary search boxes.

## Search diagnostics

Advanced results expose how each retrieval stage contributed:

```csharp
foreach (var hit in hits)
{
    Console.WriteLine($"{hit.Title}: fused={hit.Score}");

    foreach (var contribution in hit.Contributions)
        Console.WriteLine(
            $"  {contribution.StageName} {contribution.Kind} " +
            $"rank={contribution.Rank} raw={contribution.RawScore} " +
            $"rrf={contribution.FusionContribution}");
}
```

`KnowledgeSearchHit.Score` on the advanced path is the fused RRF score. `RawScore` remains available for diagnostics, but it should only be interpreted in the context of its retrieval stage/provider.

Lexical contributions also expose the logical fields that matched. Semantic contributions continue to expose matched chunks when `MatchedChunks` is requested.

## Result content

Search results collapse evidence back to Documents. A document may have evidence from several fields/chunks without flooding the result list with duplicate rows.

By default results are metadata-oriented. Request matched chunks when you need source evidence for RAG or UI excerpts:

```csharp
var query = KnowledgeSearchQuery.Create(kb.Id, "restore backup")
    .Hybrid()
    .IncludeResults(KnowledgeResultInclude.MatchedChunks);
```

For a bounded context payload from the original semantic API, prefer `IKnowledgeContentSearch` rather than hydrating whole documents.
