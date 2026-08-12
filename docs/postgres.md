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

Canonical schemas, typed EAV values, filters, recursive Collection scope, Smart routing, and vector generations use the same provider-neutral semantics as SQLite.

The integration workflow runs against a real pgvector PostgreSQL service and includes a SQLite logical-archive import test to guard backend portability.
