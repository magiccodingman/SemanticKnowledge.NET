# PostgreSQL / pgvector provider

Package: `SemanticKnowledge.NET.PostgreSql`

```csharp
services.AddSemanticKnowledge()
    .UsePostgreSql(options =>
    {
        options.ConnectionString = configuration.GetConnectionString("Knowledge")!;
        options.Schema = "semantic_knowledge";
    })
    .UseOnnxEmbeddings();
```

The `vector` extension must already be installed. SemanticKnowledge deliberately does not run privileged `CREATE EXTENSION` during application startup.

Compact storage maps to pgvector `halfvec`; MaximumPrecision maps to `vector`. v1 uses exact native vector retrieval. Approximate-index policy is intentionally not hidden behind an automatic threshold.

## Native text search and hybrid retrieval

The provider creates a derived row-per-field text index using generated `tsvector` values and a GIN index. Natural-language lexical search uses PostgreSQL web-search parsing and `ts_rank_cd`; explicit native syntax uses `to_tsquery` semantics.

```csharp
var hits = await store.SearchAsync(
    KnowledgeSearchQuery.Create(kb.Id, "restore postgres backup")
        .Hybrid()
        .Where(KnowledgeFilters.Gte("version", KnowledgeValue.From(2L)))
        .Take(20));
```

Every canonical text field can be targeted independently even though logical schemas remain runtime data:

```csharp
var lexical = await store.SearchAsync(
    KnowledgeSearchQuery.Create(kb.Id, "pg_restore")
        .Lexical(
            KnowledgeSearchField.Title(4),
            KnowledgeSearchField.Body()));
```

Semantic pgvector ranks and PostgreSQL text ranks are never mixed numerically. SemanticKnowledge delegates stage fusion to the RRF implementation from `OnnxTextEmbeddings.NET`.

Canonical schemas, typed EAV values, filters, recursive Collection scope, Smart hybrid routing, lexical-index rebuilds, and vector generations use the same provider-neutral semantics as SQLite.

The integration workflow runs against a real pgvector PostgreSQL service and verifies native lexical/hybrid retrieval plus SQLite logical-archive import portability.
