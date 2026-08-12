# SQLite provider

Package: `SemanticKnowledge.NET.Sqlite`

SQLite is the default recommendation for desktop apps, personal/team wikis, games, tools, and other embedded deployments.

```csharp
services.AddSemanticKnowledge()
    .UseSqlite("knowledge.db")
    .UseOnnxEmbeddings();
```

The provider loads sqlite-vec through the published `OnnxTextEmbeddings.NET.SqliteVec` package. Canonical data uses normal SQLite tables; vectors live in a generation-specific `vec0` virtual table.

Compact storage is native INT8 with cosine distance. MaximumPrecision uses Float32. FP16/INT4 are not advertised as sqlite-vec index formats because sqlite-vec does not natively provide those searchable types.

## Semantic, BM25, and hybrid search

SQLite also owns a rebuildable FTS5 index for every canonical text field. Lexical retrieval uses native FTS5 BM25 ranking; hybrid search combines lexical and semantic rankings with RRF.

```csharp
var hits = await store.SearchAsync(
    KnowledgeSearchQuery.Create(kb.Id, "postgres backup")
        .Smart()
        .Hybrid()
        .Where(KnowledgeFilters.HasTag("operations"))
        .Take(20));
```

A lexical-only plan does not call the embedding provider:

```csharp
var hits = await store.SearchAsync(
    KnowledgeSearchQuery.Create(kb.Id, "pg_restore")
        .Lexical(KnowledgeSearchField.Body()));
```

`UseNativeSyntax()` on a lexical stage enables SQLite FTS5 query syntax explicitly. Natural-language mode is the safe default.

The FTS5 index is derived data, just like the vector index. Existing databases automatically create and backfill it from canonical documents/Collections on first startup after upgrading. Logical schemas do not gain physical FTS columns; runtime custom text fields are indexed as rows and remain independently targetable.

Relational filters compile into SQLite SQL and constrain document candidates inside the database. Smart hybrid routing can combine Collection vector matches with Collection Title/Description/Tags lexical matches before document retrieval.

SQLite is the reference backend and has real end-to-end tests covering INT8 search, FTS5 BM25, hybrid RRF, field targeting, filters, Smart routing, synchronization, migrations, generation rebuilds, content budgets, and portable archives.
