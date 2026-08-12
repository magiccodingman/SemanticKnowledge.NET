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

Typed filters, recursive Collection scope, Smart routing, and active/pending embedding generations follow the same contracts as the other providers.

The integration workflow targets SQL Server 2025, verifies native vector retrieval, the dimension guard, and SQLite logical-archive import portability.
