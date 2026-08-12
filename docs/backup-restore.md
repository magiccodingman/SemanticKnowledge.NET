# Backup, restore, and provider portability

Use your database's native backup mechanism for operational disaster recovery. Use `IKnowledgeArchiveService` when you need a backend-neutral logical export, long-term portable artifact, or provider migration.

```csharp
var archive = services.GetRequiredService<IKnowledgeArchiveService>();

await using (var file = File.Create("wiki.sk.zip"))
    await archive.ExportKnowledgeBaseAsync(kb.Id, file);

await using (var file = File.OpenRead("wiki.sk.zip"))
    await archive.ImportAsync(file);
```

Archive format v1 contains:

```text
manifest.json
knowledge-base.json
schemas.json
collections.json
documents.jsonl
embedding-profile.json
```

Documents are emitted as JSONL so export/import works at document granularity rather than building one giant JSON document array. Schema/Collection catalog metadata is small and loaded as catalog records.

Vectors are deliberately omitted. They are derived, backend-specific, and frequently incompatible across providers. Import preserves canonical KnowledgeBase/Collection/Schema/Document GUIDs, then regenerates Collection and Document semantics using the destination embedding provider.

This is what enables SQLite → PostgreSQL or SQLite → SQL Server migration without treating a SQLite file as a universal backup format.

Archives never contain database connection strings or API credentials.
