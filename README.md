# SemanticKnowledge.NET

[![NuGet](https://img.shields.io/nuget/v/SemanticKnowledge.NET.svg)](https://www.nuget.org/packages/SemanticKnowledge.NET)

**A small, opinionated semantic knowledge store for .NET 10.**

SemanticKnowledge.NET is for applications that want structured documents, recursive knowledge organization, relational filtering, and semantic search without deploying a full RAG/vector infrastructure stack.

It is especially suited to personal/private wikis, local AI applications, documentation, notes, campaign/game lore, and small-to-medium knowledge stores where the application should be able to say **“store this, organize it, and search it intelligently”** without owning vector plumbing.

SemanticKnowledge.NET is intentionally **not** a RAG framework, graph database, crawler, distributed vector database, or agent framework.

## What it gives you

```text
KnowledgeBase
└── Collection                    ← semantically searchable hierarchy
    ├── Collection
    └── Document
        ├── Title                 ← required system field
        ├── Description           ← required system field
        ├── Tags                  ← required system field
        ├── custom relational fields
        └── custom semantic fields/chunks
```

The store handles:

- recursive semantic Collections and Smart Search routing;
- strongly typed or runtime-defined document schemas;
- required `Title`, `Description`, and `Tags` semantic identity fields;
- exact relational filters for dates, numbers, booleans, IDs, strings, and tags;
- weighted semantic fields whose enabled weights must total exactly **100**;
- whole-field or automatically chunked semantic text;
- database-native vector candidate search;
- shared/versioned scoring from `OnnxTextEmbeddings.NET`;
- automatic local text embedding through `OnnxTextEmbeddings.NET`;
- optional HTTP/custom embedding APIs;
- precomputed `QueryEmbedding` input when a caller already embedded the query;
- embedding-space fingerprints and dimension compatibility checks;
- optional deterministic, explicitly lossy dimension reduction;
- active/pending vector generations so model changes do not destroy the old index mid-rebuild;
- GUID identities plus optional caller-controlled `ExternalId` values;
- streaming synchronization for knowledge sourced elsewhere;
- compact search hits, matched-chunk retrieval, and token-budget evidence retrieval;
- NativeAOT compatibility and a versioned C ABI for non-.NET bindings.

## Storage providers

SemanticKnowledge.NET uses the database as the database. Broad candidate filtering and vector comparison stay **inside the selected backend** rather than pulling the corpus into managed memory.

| Provider | Package | Compact vector storage | Search |
|---|---|---|---|
| SQLite + sqlite-vec | `SemanticKnowledge.NET.Sqlite` | native INT8 | exact native cosine |
| PostgreSQL + pgvector | `SemanticKnowledge.NET.PostgreSql` | `halfvec` | exact by default |
| SQL Server 2025 / Azure SQL | `SemanticKnowledge.NET.SqlServer` | native float32 `VECTOR` | exact by default |

SQLite/sqlite-vec is the frictionless/default target. PostgreSQL and SQL Server use the same logical SemanticKnowledge model and API.

## Install

For the normal local path:

```bash
dotnet add package SemanticKnowledge.NET
dotnet add package SemanticKnowledge.NET.Sqlite
```

PostgreSQL:

```bash
dotnet add package SemanticKnowledge.NET.PostgreSql
```

SQL Server 2025 / Azure SQL:

```bash
dotnet add package SemanticKnowledge.NET.SqlServer
```

Remote/custom embedding APIs:

```bash
dotnet add package SemanticKnowledge.NET.Http
```

The managed core consumes the published `OnnxTextEmbeddings.NET` NuGet package. SemanticKnowledge.NET does not copy its tokenizer, chunking, vector math, dimensional-reduction, fingerprint, or scoring implementations.

## Five-minute SQLite start

```csharp
using SemanticKnowledge;
using SemanticKnowledge.Sqlite;

builder.Services
    .AddSemanticKnowledge()
    .UseSqlite("knowledge.db")
    .UseOnnxEmbeddings();
```

`UseOnnxEmbeddings()` uses `OnnxTextEmbeddings.NET`, including its default Jasper model/download/cache behavior.

Create a knowledge space and Collection:

```csharp
var store = services.GetRequiredService<ISemanticKnowledgeStore>();

var wiki = await store.GetOrCreateKnowledgeBaseAsync("Personal Wiki");
var notes = await store.GetOrCreateCollectionAsync(wiki.Id, "Database Notes");
```

Define a schema:

```csharp
var articleSchema = new KnowledgeSchemaBuilder("article")
    .SetSemanticWeight(KnowledgeSystemFields.Title, 30)
    .SetSemanticWeight(KnowledgeSystemFields.Description, 20)
    .SetSemanticWeight(KnowledgeSystemFields.Tags, 20)
    .Text(KnowledgeSystemFields.Body, 30, SemanticMode.Chunked)
    .Int64("version")
    .DateTimeOffset("published_at")
    .Build();

await store.EnsureSchemaAsync(articleSchema);
```

Semantic weights intentionally fail early unless the enabled fields total exactly `100`.

Store a document:

```csharp
Guid id = await store.UpsertDocumentAsync(new KnowledgeDocumentInput
{
    ExternalId = "docs/postgres-restore",
    KnowledgeBaseId = wiki.Id,
    CollectionId = notes.Id,
    SchemaId = articleSchema.Id,
    Title = "Restoring PostgreSQL",
    Description = "The restore procedure used after a server failure.",
    Tags = ["postgres", "backup"],
    Values = new Dictionary<string, KnowledgeValue>
    {
        [KnowledgeSystemFields.Body] = KnowledgeValue.From(markdown),
        ["version"] = KnowledgeValue.From(2L),
        ["published_at"] = KnowledgeValue.From(DateTimeOffset.UtcNow)
    }
});
```

The Body may produce several direct chunk embeddings. SemanticKnowledge persists the direct chunk/source metadata rather than flattening a large document into one vector.

Search:

```csharp
var results = await store.SearchAsync(
    "How did I restore PostgreSQL?",
    new KnowledgeSearchRequest
    {
        KnowledgeBaseId = wiki.Id,
        Top = 10
    });
```

The text query is embedded automatically.

If you already have a compatible `QueryEmbedding`, use it directly and avoid another model call:

```csharp
QueryEmbedding query = await embeddingProvider.EmbedQueryAsync("restore PostgreSQL");
var results = await store.SearchAsync(query, request);
```

SemanticKnowledge rejects a query whose dimensions or embedding-space fingerprint do not match the active store.

## Relational + semantic filtering

Exact facts should normally be exact filters, not embeddings.

```csharp
var results = await store.SearchAsync(
    "PostgreSQL restore after a failure",
    new KnowledgeSearchRequest
    {
        KnowledgeBaseId = wiki.Id,
        Filter = KnowledgeFilters.And(
            KnowledgeFilters.Gte("version", KnowledgeValue.From(2L)),
            KnowledgeFilters.HasTag("backup")),
        Top = 10
    });
```

The structured predicate is applied inside the database **before** the semantic candidate cut.

## Strongly typed schemas and filters

Dynamic schemas are useful, but normal C# applications do not need stringly typed field definitions.

```csharp
public sealed class Npc : KnowledgeDocument
{
    public string Biography { get; init; } = "";
    public string Faction { get; init; } = "";
    public int Level { get; init; }
    public DateTimeOffset IntroducedAt { get; init; }
}

var schema = new TypedKnowledgeSchemaBuilder<Npc>("npc")
    .Semantic(x => x.Title, 30)
    .Semantic(x => x.Description, 20)
    .Semantic(x => x.Tags, 20)
    .Semantic(x => x.Biography, 30, SemanticMode.Chunked)
    .Filterable(x => x.Faction)
    .Filterable(x => x.Level)
    .Filterable(x => x.IntroducedAt)
    .Build();
```

The supported expression subset is inspected as an expression tree and is **not dynamically compiled**, keeping the API friendly to NativeAOT.

```csharp
var request = new KnowledgeSearchRequestBuilder<Npc>(wiki.Id)
    .Where(x => x.Level >= 10 && x.Faction == "Red Wizards")
    .Top(10)
    .Build();
```

Unsupported arbitrary method execution fails during query construction rather than becoming client-side evaluation.

## Search modes

### Global

Search all applicable document semantic sources in the KnowledgeBase.

### Scoped

Search one or more known Collections, optionally including descendants.

### Smart

When the application does not know the correct branch, Smart Search first searches the semantic identity of Collections, then searches documents inside the relevant branches.

```csharp
var results = await store.SearchAsync(
    "Who was the wizard we met near the ruined tower?",
    new KnowledgeSearchRequest
    {
        KnowledgeBaseId = campaign.Id,
        Mode = KnowledgeSearchMode.Smart,
        Top = 10
    });
```

Collections themselves are embedded from their semantic identity rather than acting as passive folders.

## Compact results and matched content

Search returns compact metadata by default. It does **not** attach a giant document Body merely because one chunk matched.

Request matched chunks when useful:

```csharp
request = request with
{
    Include = KnowledgeResultInclude.MatchedChunks
};
```

Or retrieve the strongest evidence up to a content budget:

```csharp
var contentSearch = services.GetRequiredService<IKnowledgeContentSearch>();

var context = await contentSearch.SearchContentAsync(
    "Why did we reject Redis?",
    request,
    maxContentTokens: 8_000);
```

This uses the token/source ranges already preserved in direct `TextEmbedding` records.

## Synchronizing an external wiki/source

If Markdown/files/another database are authoritative, use caller-controlled `ExternalId` values and the synchronization service:

```csharp
var sync = services.GetRequiredService<IKnowledgeSynchronizationService>();

KnowledgeSyncResult result = await sync.SyncCollectionAsync(
    wiki.Id,
    notes.Id,
    articleSchema.Id,
    StreamPagesAsync(),
    new KnowledgeSyncOptions { DeleteMissing = true });
```

The input is an `IAsyncEnumerable<ExternalKnowledgeDocumentInput>`. Source hashes skip unchanged documents so they are not re-embedded on every synchronization.

## Embedding dimensions

The model's native dimensions are used by default.

You may explicitly request fewer dimensions:

```csharp
builder.Services.AddSemanticKnowledge(options =>
{
    options.Embeddings.OutputDimensions = 1024;
});
```

Dimension reduction is **lossy**. SemanticKnowledge uses the deterministic direct-chunk/query reduction supplied by `OnnxTextEmbeddings.NET` and persists the resulting child embedding-space fingerprint.

It never silently reduces dimensions simply because a backend has a limit.

This matters for SQL Server: the current native SQL Server vector surface supports at most 1,998 dimensions, so a 2,048-dimensional model requires an explicit choice such as 1,998, 1,536, or 1,024.

## Vector storage

The normal configuration expresses intent instead of pretending every database has the same numeric primitive:

```csharp
options.Embeddings.Storage = VectorStoragePreference.Compact; // default
```

The provider maps that intent to its safe native representation:

```text
SQLite       -> INT8
PostgreSQL   -> halfvec
SQL Server   -> native float32 VECTOR
```

Use `MaximumPrecision` when the storage increase is worth it.

## Embedding profile changes

Embedding vectors are derived data. Canonical text/typed fields remain the source of truth.

If the model/fingerprint/dimensions/vector representation changes, providers build a **pending vector generation** while the previous generation remains active. SemanticKnowledge re-embeds canonical Collections/Documents and only switches the active generation after the replacement completes.

A failed rebuild therefore does not require dropping the last valid vector index first.

## PostgreSQL

```csharp
using SemanticKnowledge.PostgreSql;

builder.Services
    .AddSemanticKnowledge()
    .UsePostgreSql(configuration.GetConnectionString("SemanticKnowledge")!)
    .UseOnnxEmbeddings();
```

The PostgreSQL provider expects the `vector` extension to already be installed. It does not attempt privileged `CREATE EXTENSION` operations automatically.

For credentials, prefer `IConfiguration`, environment variables, user-secrets during development, or your normal production secret manager rather than hard-coding connection strings.

## SQL Server 2025 / Azure SQL

```csharp
using SemanticKnowledge.SqlServer;

builder.Services
    .AddSemanticKnowledge(options =>
    {
        options.Embeddings.OutputDimensions = 1024;
    })
    .UseSqlServer(configuration.GetConnectionString("SemanticKnowledge")!)
    .UseOnnxEmbeddings();
```

Exact native `VECTOR_DISTANCE` retrieval is the normal path. Preview-only server features are not silently enabled.

## Custom/remote embedding API

```csharp
using SemanticKnowledge.Http;

builder.Services
    .AddSemanticKnowledge()
    .UseSqlite("knowledge.db")
    .UseHttpEmbeddings(options =>
    {
        options.Endpoint = new Uri(configuration["Embeddings:Endpoint"]!);
        options.ModelId = "my-model";
        options.SpaceId = "my-model-v3";
        options.Dimensions = 2048;
        options.BearerToken = configuration["Embeddings:Token"];
    });
```

A tokenizer endpoint is optional. Exact token-count-dependent features can use one when supplied; basic embedding/search does not require every remote API to expose its tokenizer.

Remote FP32 responses are validated, reduced if explicitly configured, and converted to the database provider's storage representation by SemanticKnowledge.

## NativeAOT and other languages

The managed core is designed and continuously published/tested for NativeAOT compatibility.

The repository also includes `SemanticKnowledge.Native`, a NativeAOT shared-library facade with a versioned C ABI:

```text
SemanticKnowledge.Native
       ↓
.dll / .so / .dylib
       ↓
stable C ABI
       ↓
Rust / C++ / Go / Zig / Python FFI / other bindings
```

The project promises the C ABI and cross-platform C smoke tests; it does not claim every third-party language wrapper as a first-party SDK.

See `native/include/semantic_knowledge.h`.

## Rerankers

A second neural/cross-encoder reranker is **not part of the default v1 search pipeline**.

At the intended store size, exact native retrieval + relational filtering + semantic Collection routing + weighted fields + direct chunk scoring already provide a strong retrieval environment without paying a second model-inference cost on every query.

The architecture intentionally leaves a clean future reranker/evidence extension point. Future rerankers should operate only over a bounded final candidate set, never digest the full corpus.

## Scope

Good fits:

- personal and private wikis;
- local desktop AI knowledge/memory;
- documentation search;
- structured notes;
- game/campaign lore and histories;
- private company knowledge;
- embedded application semantic search;
- tens-of-thousands-ish document/chunk workloads where simple deployment matters.

When you need distributed vector shards, enormous ingestion fleets, graph-native traversal, tens/hundreds of millions of vectors, or multi-region vector infrastructure, use a purpose-built system such as Weaviate/Qdrant/Milvus/FalkorDB instead.

## Documentation

- [Architecture and implementation design](docs/architecture-design.md)
- [Getting started](docs/getting-started.md)
- [Best practices](docs/best-practices.md)
- [Storage providers](docs/storage-providers.md)
- [Native interoperability](docs/native-interop.md)
- [Migrations and rebuilds](docs/migrations.md)

## License

Apache-2.0.
