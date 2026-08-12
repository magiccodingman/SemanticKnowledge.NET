# Synchronization and rebuilds

`IKnowledgeSynchronizationService` is for authoritative external sources such as repositories, wikis, crawlers, CMS data, mail indexes, or application records.

Input is `IAsyncEnumerable<ExternalKnowledgeDocumentInput>` so the caller does not need to materialize the incoming corpus.

## Incremental synchronization

The existing behavior remains the default:

```csharp
var result = await sync.SyncCollectionAsync(
    kb.Id,
    collection.Id,
    schema.Id,
    ReadPagesAsync());
```

`ExternalId` is the synchronization key. A deterministic canonical source hash lets unchanged records skip re-embedding. Changed records update, new records insert, and—when delete-missing behavior is enabled—stored external IDs absent from the completed source stream are deleted. Duplicate external IDs in one source stream fail explicitly.

Incremental synchronization intentionally exposes each successful mutation as it is applied. It is appropriate when progressive visibility is acceptable.

## Atomic Collection snapshots

When a logical corpus must become queryable as one coherent revision, use atomic snapshot publication:

```csharp
var result = await sync.SyncCollectionSnapshotAsync(
    kb.Id,
    collection.Id,
    schema.Id,
    sourceRevision: sourceRevision,
    documents: ReadPagesAsync());
```

or equivalently:

```csharp
await sync.SyncCollectionAsync(
    kb.Id,
    collection.Id,
    schema.Id,
    ReadPagesAsync(),
    new KnowledgeSyncOptions
    {
        Publication = KnowledgeSyncPublication.AtomicSnapshot,
        SourceRevision = sourceRevision,
        DeleteMissing = true
    });
```

`SourceRevision` is an **opaque caller-owned token**. SemanticKnowledge does not know or care whether it represents a Git commit, crawler revision, CMS export, mailbox state token, or another source identity.

Atomic publication has these semantics:

1. the current published Collection remains queryable while the new corpus streams in;
2. changed/new documents are embedded and staged outside the live corpus;
3. unchanged documents are reused without re-embedding;
4. semantic and native lexical/BM25 records are prepared before publication;
5. failure or cancellation discards the staged revision and leaves the old corpus/revision active;
6. after the complete input succeeds, the provider publishes canonical documents, vectors, and lexical eligibility in one short database transaction;
7. readers therefore observe the old revision or the new revision, never a half-updated Collection.

Locally authored documents without `ExternalId` are outside the external reconciliation namespace and survive snapshots. `DeleteMissing=false` also preserves externally-keyed documents omitted from the incoming revision.

### Inspect publication state

```csharp
var state = await sync.GetCollectionSnapshotStateAsync(collection.Id);

Console.WriteLine(state?.SourceRevision);
Console.WriteLine(state?.ActiveSnapshotId);
Console.WriteLine(state?.PublishedAt);
```

This is useful when an application and SemanticKnowledge use physically independent databases. The application does **not** need a distributed ACID transaction: after restart it can compare the external source revision it expects with SemanticKnowledge's last atomically published revision and retry/reconcile idempotently when needed.

A `SourceRevision` describes the last successful snapshot publication. Ordinary direct `UpsertDocumentAsync` calls remain immediate mutations and do not pretend to participate in that external-source revision protocol.

### Provider behavior

All first-party providers implement the same snapshot contract:

- SQLite stages canonical/semantic state plus invisible FTS5 rows and flips publication transactionally;
- PostgreSQL does the same with pgvector + transactionally maintained `tsvector`/GIN data;
- SQL Server stages Full-Text rows under an invisible internal entity kind, waits for Full-Text indexing to finish, then atomically flips their eligibility together with canonical/VECTOR publication.

No embedding/model inference transaction is kept open while the incoming corpus is processed.

## Embedding rebuilds are separate

Embedding profile changes are different from logical corpus snapshots. They rebuild only derived semantic generations from canonical data and atomically swap the active vector index when complete.

`ResetAsync` is destructive. In `Rebuildable` deployments it is a normal recovery primitive because an authoritative source exists elsewhere. In `Authoritative` deployments, prefer logical migrations and portable archives.
