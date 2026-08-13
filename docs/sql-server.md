# SQL Server 2025 / Azure SQL provider

Package: `SemanticKnowledge.NET.SqlServer`

```csharp
services.AddSemanticKnowledge()
    .UseSqlServer(options =>
    {
        options.ConnectionString = configuration.GetConnectionString("Knowledge")!;
        options.Schema = "SemanticKnowledge";
    })
    .UseOnnxEmbeddings();
```

The provider uses native SQL Server VECTOR search and stores Float32 vectors. SQL Server's native vector dimensional limit is 1,998. SemanticKnowledge never silently reduces a larger embedding to fit:

```csharp
options.Embeddings.OutputDimensions = 1024; // explicit decision
```

A dimension mismatch produces an actionable startup error before application search proceeds.

## SQL Server Full-Text Search

Lexical/hybrid search requires the SQL Server Full-Text Search component to be installed on the server. **Semantic-only stores do not require Full-Text Search.** If the component is unavailable, startup remains valid, `KnowledgeProviderCapabilities.LexicalSearchSupported` is `false`, and the existing semantic APIs continue to work. A lexical or hybrid retrieval plan returns an actionable error explaining that the Full-Text component is required.

If Full-Text Search is installed later, restarting the application detects the capability, creates the derived lexical index, and backfills it from canonical Collection/Document data.

When available, the provider automatically owns the Full-Text catalog/index and a derived row-per-field text table with a native integer full-text key. Application code does not create catalogs, key indexes, or physical columns for runtime schema fields.

Natural-language lexical stages use native `FREETEXTTABLE`; explicit `UseNativeSyntax()` uses `CONTAINSTABLE` syntax.

```csharp
var hits = await store.SearchAsync(
    KnowledgeSearchQuery.Create(kb.Id, "restore sql backup")
        .Smart()
        .Hybrid()
        .Where(KnowledgeFilters.Gte("version", KnowledgeValue.From(2L)))
        .Take(20));
```

Lexical-only retrieval does not request a query embedding:

```csharp
var hits = await store.SearchAsync(
    KnowledgeSearchQuery.Create(kb.Id, "restore backup")
        .Lexical(KnowledgeSearchField.Body()));
```

SQL Server Full-Text `RANK` and cosine similarity are different score spaces, so hybrid retrieval uses RRF instead of adding raw values together.

Typed filters, recursive Collection scope, Smart hybrid routing, lexical-index rebuilds, and active/pending embedding generations follow the same contracts as the other providers.

The integration workflow targets SQL Server 2025 with the Full-Text component installed and verifies native vector retrieval, native Full-Text/hybrid search, the 1,998-dimension guard, and SQLite logical-archive import portability.
