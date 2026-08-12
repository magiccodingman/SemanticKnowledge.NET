# Logical database migrations

`DatabaseVersion` versions *your logical knowledge shape*. It is separate from embedding-generation rebuilds.

For authoritative stores, version changes require an explicit, unambiguous migration chain:

```csharp
services
    .AddSemanticKnowledge(options =>
    {
        options.DatabaseVersion = 2;
        options.PersistenceMode = KnowledgePersistenceMode.Authoritative;
    })
    .Migrate(1, 2, async (migration, ct) =>
    {
        foreach (var document in await migration.GetDocumentsAsync(cancellationToken: ct))
        {
            // transform canonical values/schema references as required
            await migration.UpsertDocumentAsync(document, ct);
        }
    })
    .UseSqlite("wiki.db")
    .UseOnnxEmbeddings();
```

Startup reads the stored version before normal target-version initialization. Each migration step is executed and the stored version advances **only after that step succeeds**. Missing or ambiguous paths fail closed. Downgrades are rejected.

Migrated documents are validated, source hashes are recomputed, and semantic evidence is regenerated. This prevents a later external sync from incorrectly treating transformed data as unchanged.

For `Rebuildable` stores, a logical version mismatch clears the store and recreates it at the configured version instead of running business-data transformations. That mode is appropriate only when another authoritative source can repopulate the data.
