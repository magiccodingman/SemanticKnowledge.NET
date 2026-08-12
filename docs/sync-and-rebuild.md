# Synchronization and rebuilds

`IKnowledgeSynchronizationService` is for authoritative external sources such as Git repositories, wikis, CMS data, or application records.

Input is `IAsyncEnumerable<ExternalKnowledgeDocumentInput>` so the caller does not need to materialize the incoming corpus.

```csharp
var result = await sync.SyncCollectionAsync(
    kb.Id,
    collection.Id,
    schema.Id,
    ReadPagesAsync());
```

`ExternalId` is the synchronization key. A deterministic canonical source hash lets unchanged records skip re-embedding. Changed records update, new records insert, and—when delete-missing behavior is enabled—stored external IDs absent from the completed source stream are deleted. Duplicate external IDs in one source stream fail explicitly.

Embedding profile changes are different from logical data migrations. They rebuild only derived semantic generations from canonical data and atomically swap the active index when complete.

`ResetAsync` is destructive. In `Rebuildable` deployments it is a normal recovery primitive because an authoritative source exists elsewhere. In `Authoritative` deployments, prefer logical migrations and portable archives.
