# Getting started

SemanticKnowledge.NET is a .NET 10 semantic knowledge store. SQLite + sqlite-vec is the easiest deployment and is the recommended starting point.

## Packages

```bash
dotnet add package SemanticKnowledge.NET
dotnet add package SemanticKnowledge.NET.Sqlite
```

The SQLite package uses the published `OnnxTextEmbeddings.NET` family for embeddings and sqlite-vec integration.

## Register the store

```csharp
using SemanticKnowledge;
using SemanticKnowledge.Sqlite;

builder.Services
    .AddSemanticKnowledge(options =>
    {
        options.PersistenceMode = KnowledgePersistenceMode.Authoritative;
    })
    .UseSqlite("knowledge.db")
    .UseOnnxEmbeddings();
```

For disposable indexes that can always be recreated from another source, use `Rebuildable` instead of `Authoritative`.

## Store a document

```csharp
var store = services.GetRequiredService<ISemanticKnowledgeStore>();
await store.InitializeAsync();

var kb = await store.GetOrCreateKnowledgeBaseAsync("Personal Wiki");
var schema = await store.EnsureBuiltInDocumentSchemaAsync();
var notes = await store.GetOrCreateCollectionAsync(
    kb.Id,
    "Notes",
    defaultSchemaId: schema.Id);

await store.UpsertDocumentAsync(
    kb.Id,
    notes.Id,
    title: "SQLite backup notes",
    body: "Use the online backup API before copying the file.",
    tags: ["sqlite", "backup"]);
```

## Search

```csharp
var hits = await store.SearchAsync(
    "how did I back up sqlite?",
    new KnowledgeSearchRequest
    {
        KnowledgeBaseId = kb.Id,
        Mode = KnowledgeSearchMode.Smart,
        Top = 10,
        Include = KnowledgeResultInclude.MatchedChunks
    });
```

`Global` searches the KnowledgeBase, `Scoped` searches selected Collections, and `Smart` first routes through semantic Collection metadata and then searches the selected subtrees.

## Next

- [Concepts](concepts.md)
- [Schemas](schemas.md)
- [Collections](collections.md)
- [Search](search.md)
- [SQLite](sqlite.md)
- [Migrations](migrations.md)
- [Backup and restore](backup-restore.md)
