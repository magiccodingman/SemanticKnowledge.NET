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

Relational filters compile into SQLite SQL and constrain document candidates inside the database. Smart routing searches Collection vectors first, then scopes document KNN retrieval.

SQLite is the reference backend and has real end-to-end tests covering INT8 search, filters, Smart routing, synchronization, migrations, generation rebuilds, content budgets, and portable archives.
