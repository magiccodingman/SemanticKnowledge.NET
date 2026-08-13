# SemanticKnowledge.NET — Architecture and Implementation Design

> **Status:** Canonical implementation blueprint for the initial SemanticKnowledge.NET architecture.  
> **Repository:** `magiccodingman/SemanticKnowledge.NET`  
> **Target:** .NET 10, NativeAOT-friendly, provider-based storage, opinionated semantic knowledge retrieval.  
> **Primary dependency:** the published `OnnxTextEmbeddings.NET` NuGet ecosystem.  
> **Implementation note:** this document intentionally specifies the intended product and engineering contract before implementation begins.

---

## 1. Executive summary

SemanticKnowledge.NET is an opinionated, embedded **semantic knowledge store for structured documents**. It is designed for applications such as personal wikis, private/company wikis, local AI applications, game/campaign lore, notes, documentation, and other small-to-medium knowledge workloads where installing and operating a full RAG/vector infrastructure stack would be unnecessary overhead.

It is deliberately **not** a RAG framework, graph database, distributed vector database, ingestion platform, crawler, agent framework, or replacement for systems such as Weaviate, Qdrant, Milvus, or FalkorDB.

The product goal is much simpler:

> Install a NuGet package, define how the application's knowledge is organized, store or synchronize documents, filter them using normal structured facts, and search them semantically. SemanticKnowledge.NET handles embedding generation, chunk persistence, vector-space compatibility, database-native vector search, scoring, schema metadata, migrations, and storage details.

The important architectural distinction from a conventional vector store is that **the hierarchy itself is semantic**. A KnowledgeBase contains a recursive tree of Collections. Collections have Title, Description, and Tags, are embedded, and can therefore participate in routing. Documents have required system metadata plus strongly typed or runtime-defined custom fields. Semantic search can be global, explicitly scoped, or use Smart Search to determine which collections are relevant before searching their contents.

All supported database providers should keep broad filtering and vector candidate retrieval **inside the database**. Only a bounded candidate set crosses into .NET for the shared, versioned scoring logic already supplied by `OnnxTextEmbeddings.NET`.

SQLite/sqlite-vec is the frictionless default and primary target. PostgreSQL/pgvector and SQL Server 2025/Azure SQL are first-class providers using the same logical model and public API.

---

## 2. Product philosophy

### 2.1 What SemanticKnowledge.NET should feel like

The normal developer experience should feel much closer to adding BM25/full-text search to an application than deploying AI infrastructure.

A basic application should be able to reach a useful semantic store with a small amount of code:

```csharp
builder.Services
    .AddSemanticKnowledge(options =>
    {
        options.DatabaseVersion = 1;
    })
    .UseSqlite("knowledge.db")
    .UseOnnxEmbeddings();
```

Then:

```csharp
var wiki = await store.GetOrCreateKnowledgeBaseAsync("Personal Wiki");
var notes = await wiki.Collections.GetOrCreateAsync("Notes");

await notes.UpsertAsync(new KnowledgeDocumentInput
{
    Title = "Restoring PostgreSQL backups",
    Description = "Notes about the restore procedure used on my servers.",
    Tags = ["postgres", "backup"],
    Body = markdown
});

var results = await wiki.SearchAsync("How did I restore PostgreSQL?");
```

Internally that may involve schema validation, source hashing, chunking, ONNX inference, deterministic dimensional reduction, native vector storage, hierarchical routing, relational filtering, candidate retrieval, and multi-field scoring. The caller should not need to orchestrate those pieces.

### 2.2 Non-goals

SemanticKnowledge.NET must resist scope creep. It should not grow features merely because enterprise vector products contain them.

Use a different product when the workload requires:

- distributed vector shards;
- tens or hundreds of millions of vectors;
- multi-region vector infrastructure;
- graph-native traversal/reasoning;
- Kafka-style ingestion fleets;
- distributed consensus or cluster membership;
- generalized RAG orchestration;
- document crawling/parsing pipelines;
- agent execution;
- model hosting as a network service.

SemanticKnowledge.NET may be used inside a larger system, but it should remain a focused knowledge/search component.

---

## 3. Core terminology and hierarchy

The logical hierarchy is:

```text
SemanticKnowledge Store
└── KnowledgeBase
    ├── Collection
    │   ├── Collection
    │   │   ├── Document
    │   │   └── Document
    │   └── Document
    └── Collection
        └── Document
```

### 3.1 Store

A configured physical SemanticKnowledge instance. A store has:

- one storage provider;
- one configured active embedding profile;
- engine schema version;
- caller-defined logical database version;
- one or more KnowledgeBases;
- one active embedding generation at a time.

### 3.2 KnowledgeBase

The top-level logical namespace. Examples:

- `Personal Wiki`
- `Acme Internal Knowledge`
- `Curse of Strahd`
- `Product Documentation`

KnowledgeBases allow one physical database to contain independent logical knowledge spaces without requiring multiple database files or databases.

### 3.3 Collection

A recursive semantic organizational node. A Collection is intentionally broader than a folder: it is organization **and data**.

Every Collection has system metadata:

- `Guid Id`
- optional caller-controlled `ExternalId`
- `Title`
- `Description`
- `Tags`
- parent Collection ID, nullable for roots
- KnowledgeBase ID
- optional default Schema ID
- timestamps/revision metadata

Title, Description, and Tags participate in semantic routing.

Collections may contain documents and child collections simultaneously unless a future schema policy explicitly forbids it.

### 3.4 Schema

A reusable logical document shape. A Schema is data stored by SemanticKnowledge.NET, not a dynamically generated physical SQL table.

Multiple Collections can use the same Schema.

### 3.5 Document

A structured knowledge item with required system fields and optional custom fields.

Every document has:

- `Guid Id`
- optional caller-controlled `ExternalId`
- `KnowledgeBaseId`
- `CollectionId`
- `SchemaId`
- `Title`
- `Description`
- `Tags`
- custom typed values
- timestamps/source revision/hash metadata

### 3.6 Semantic source

A persisted unit of semantic evidence derived from a Collection or Document field.

Examples:

- Collection Title/Description/Tags identity text;
- Document Title;
- Document Description;
- Document Tags;
- one whole-field embedding;
- one chunk of a chunked Body/Biography field.

Semantic sources preserve the complete direct `TextEmbedding` metadata needed for current scoring and future evidence/excerpt workflows.

---

## 4. Identity rules

### 4.1 GUIDs are the public identity contract

All externally meaningful objects should use `Guid` identifiers:

- Store/instance identity where useful;
- KnowledgeBase;
- Collection;
- Schema;
- SchemaField;
- Document;
- SemanticSource;
- EmbeddingGeneration.

Providers MAY use internal integer surrogate row keys for physical efficiency, but those IDs must never replace the stable public GUID contract.

This enables:

- durable references from a caller's relational database;
- export/import without key collisions;
- cross-provider migration;
- multiple independent SemanticKnowledge stores;
- native interop without leaking provider-specific row identity.

### 4.2 ExternalId

Documents and Collections should optionally support a caller-controlled `ExternalId`.

Example:

```text
Document.Id       = 24a3...          // SemanticKnowledge-owned GUID
Document.ExternalId = docs/postgres.md // caller-owned identity
```

This is especially useful for rebuildable/synchronized stores. A wiki importer can repeatedly upsert `docs/postgres.md` without separately persisting the generated SemanticKnowledge GUID.

Recommended initial uniqueness rule:

- document `ExternalId`: unique within a KnowledgeBase when non-null;
- collection `ExternalId`: unique within a KnowledgeBase when non-null.

The exact scope should remain provider-independent and enforced with database uniqueness constraints.

---

## 5. Package and solution architecture

Proposed repository layout:

```text
SemanticKnowledge.NET.slnx
Directory.Build.props
Directory.Packages.props
README.md
128x128_compressed.png
LICENSE

docs/
    architecture-design.md
    getting-started.md
    concepts.md
    schemas.md
    collections.md
    search.md
    filtering.md
    embedding-profiles.md
    migrations.md
    sync-and-rebuild.md
    sqlite.md
    postgres.md
    sql-server.md
    backup-restore.md
    native-aot.md
    native-interop.md
    best-practices.md
    troubleshooting.md

src/
    SemanticKnowledge.NET/
    SemanticKnowledge.NET.Sqlite/
    SemanticKnowledge.NET.PostgreSql/
    SemanticKnowledge.NET.SqlServer/
    SemanticKnowledge.NET.Http/
    SemanticKnowledge.Native/

tests/
    SemanticKnowledge.NET.Tests/
    SemanticKnowledge.NET.Sqlite.Tests/
    SemanticKnowledge.NET.PostgreSql.Tests/
    SemanticKnowledge.NET.SqlServer.Tests/
    SemanticKnowledge.AotSmoke/
    SemanticKnowledge.ProviderConformance.Tests/

native/
    include/semantic_knowledge.h
    smoke/c/main.c

eng/
    release.json
    DetectPackageChanges.ps1
    ResolveReleaseVersion.ps1
    ValidatePackages.ps1

.github/workflows/
    ci.yml
    aot-integration.yml
    sqlite-integration.yml
    postgres-integration.yml
    sqlserver-integration.yml
    publish-nuget.yml
```

### 5.1 `SemanticKnowledge.NET`

Owns the provider-independent product:

- domain model;
- schema definitions;
- validation;
- collection hierarchy;
- document CRUD/sync orchestration;
- embedding profile compatibility;
- search requests/results;
- strongly typed filter-expression parser;
- provider-independent filter AST;
- migration contracts;
- embedding-generation orchestration;
- export/import logical model;
- shared diagnostics;
- AOT-safe serialization contracts;
- integration with the published `OnnxTextEmbeddings.NET` semantic primitives/scoring model.

The v1 design deliberately depends on the **published NuGet package**, not a project reference to the sibling repository.

If implementation exposes a defect in `OnnxTextEmbeddings.NET`, work that depends on the defect should stop and report the upstream issue rather than copying/fixing upstream behavior inside SemanticKnowledge.NET.

### 5.2 Provider packages

Each provider package references `SemanticKnowledge.NET` and the corresponding published OnnxTextEmbeddings database adapter where useful:

```text
SemanticKnowledge.NET.Sqlite
  ├── SemanticKnowledge.NET
  └── OnnxTextEmbeddings.NET.SqliteVec

SemanticKnowledge.NET.PostgreSql
  ├── SemanticKnowledge.NET
  └── OnnxTextEmbeddings.NET.PgVector

SemanticKnowledge.NET.SqlServer
  ├── SemanticKnowledge.NET
  └── OnnxTextEmbeddings.NET.SqlServer
```

The provider owns:

- connection lifecycle abstraction;
- engine schema creation/migrations;
- provider-specific data types;
- query compilation from filter AST;
- vector generation tables;
- capabilities validation;
- native vector candidate retrieval;
- provider-specific indexes;
- transactions;
- provider integration tests.

### 5.3 `SemanticKnowledge.NET.Http`

Optional implementation of the generic embedding-provider contract for remote embedding APIs. Keeping it separate allows retry/HTTP concerns to remain isolated.

### 5.4 `SemanticKnowledge.Native`

A NativeAOT shared-library facade exposing a stable C ABI over the canonical C# implementation.

It should not mirror generic CLR APIs. It is a deliberate interoperability surface, modeled after the successful shape already used by `OnnxTextEmbeddings.Native`.

It is not necessarily a NuGet package. Native release artifacts should be published as platform bundles through GitHub Releases.

---

## 6. NativeAOT and implementation constraints

NativeAOT is a first-class project goal, not an afterthought.

### 6.1 Do not build the persistence core around EF Core

The public experience is code-first, but **code-first does not require EF Core**.

SemanticKnowledge.NET should use a small provider-owned ADO.NET/query/migration layer because:

- the logical engine schema is small and controlled;
- vector syntax differs substantially by backend;
- runtime-defined semantic filters are inherently dynamic;
- NativeAOT must be continuously proven;
- EF Core's current NativeAOT/query-precompilation support remains experimental and particularly awkward for dynamically composed queries;
- the library already needs a provider-neutral filter AST and provider-specific SQL compiler.

### 6.2 AOT rules

Core and providers should:

- set `IsAotCompatible=true` only when that project is actually continuously proven;
- avoid runtime code generation;
- parse expression trees rather than calling `Compile()`;
- use System.Text.Json source generation for known persisted/interop DTOs;
- avoid reflection-based object materialization where practical;
- avoid assembly scanning as a required registration mechanism;
- expose explicit registration APIs;
- publish AOT smoke apps in CI.

Provider AOT compatibility is a **tested capability**, not an assumption. If SQL Server's current client/runtime dependency chain fails NativeAOT publication, managed SQL Server support may remain available while SQL Server NativeAOT support is explicitly withheld until CI proves it.

---

## 7. Physical persistence model

User-defined logical schemas should NOT cause physical SQL tables to be created per Collection or document type.

The engine should use a stable relational model. Suggested logical tables are below; exact column names can evolve during implementation.

### 7.1 Namespacing

To coexist safely with application databases:

- SQLite: use `sk_` table prefixes;
- PostgreSQL: default dedicated schema such as `semantic_knowledge`;
- SQL Server: default dedicated schema such as `[SemanticKnowledge]`.

Provider options should permit overriding the server-database schema name where appropriate.

### 7.2 Engine metadata

`store_metadata`

- store GUID;
- engine schema version;
- caller logical database version;
- active embedding generation GUID;
- creation/update timestamps;
- persistence mode.

### 7.3 Knowledge bases

`knowledge_bases`

- GUID ID;
- ExternalId if supported;
- title/name;
- description;
- timestamps.

`knowledge_base_tags`

- KnowledgeBase ID;
- normalized tag value.

### 7.4 Collections

`collections`

- GUID ID;
- KnowledgeBase ID;
- parent Collection ID nullable;
- default Schema ID nullable;
- ExternalId nullable;
- Title;
- Description;
- source/revision metadata;
- timestamps.

`collection_tags`

- Collection ID;
- normalized tag value.

### 7.5 Schemas

`schemas`

- GUID ID;
- stable key/name;
- display name;
- revision;
- optional KnowledgeBase scope;
- timestamps.

`schema_fields`

- GUID field ID;
- Schema ID;
- stable field key;
- display name;
- field type;
- required flag;
- system-field flag;
- semantic mode;
- semantic weight percentage;
- filterability/index hints;
- ordinal;
- optional default metadata.

### 7.6 Documents

`documents`

- GUID ID;
- KnowledgeBase ID;
- Collection ID;
- Schema ID;
- ExternalId nullable;
- Title;
- Description;
- source hash/revision;
- timestamps.

`document_tags`

- Document ID;
- normalized tag value.

### 7.7 Typed dynamic field values

Use a typed EAV-style table rather than one JSON blob as the only queryable representation.

`document_values`

- Document ID;
- Field ID;
- one discriminated typed value among Text, Int64, Decimal/Double, Boolean, DateTime/DateTimeOffset, Guid;
- optional ordinal for future multi-value fields.

Providers add appropriate composite indexes such as `(FieldId, ValueInt64)` or `(FieldId, ValueText)`.

This gives runtime schema flexibility while preserving provider-native filtering and indexes.

JSON MAY be used as an auxiliary serialization/cache format, but v1 filtering must not depend on each database's incompatible JSON-query semantics.

### 7.8 Semantic sources

`semantic_sources`

- GUID SemanticSource ID;
- KnowledgeBase ID;
- Collection ID nullable;
- Document ID nullable;
- Field ID nullable;
- semantic role;
- field key/name;
- chunk index;
- source start/end metadata;
- token start/end metadata when available;
- token count;
- embedding-space fingerprint;
- embedding generation ID;
- complete versioned direct `TextEmbedding` payload/metadata needed by shared scoring;
- optional source/context text if required to faithfully round-trip the direct embedding record;
- source hash.

The exact serialized payload should use the existing `OnnxTextEmbeddings.NET` serialization/protocol facilities where possible instead of inventing a second representation.

### 7.9 Vector generation tables

Vector dimension/type is physically part of native vector-table definitions. This is the one area where runtime physical tables are justified.

Each embedding generation may own a provider-specific vector table such as:

```text
sk_vectors_<safe-generation-key>
```

Rows map SemanticSource ID and document/field metadata to the provider-native vector representation.

The table name is generated internally from a GUID/hash and never from unsanitized user input.

---

## 8. Canonical data versus derived data

Canonical text and typed values are the source of truth.

Embeddings are derived data.

```text
Canonical document/collection fields
              ↓
        semantic source
              ↓
     direct TextEmbedding(s)
              ↓
 provider-native vector generation
```

Therefore:

- source text must be stored;
- searchable/filterable text must remain normal database text, not application-compressed opaque blobs;
- the complete vector index may be deleted and rebuilt from canonical data;
- embedding-profile changes rebuild derived generations, not canonical documents;
- export may omit embeddings and still be complete.

Do not application-compress ordinary source text by default. Transparent provider/database compression is acceptable where it remains transparent to normal indexing/querying.

---

## 9. Schema model

### 9.1 Required system fields

Every document schema has these system fields:

- Title;
- Description;
- Tags.

They always exist. Title should normally be required/non-empty. Description and Tags may be empty, but should remain present in the model.

These fields are protected from incompatible removal because Smart Search and baseline semantic retrieval depend on a common semantic identity.

Collections have the same semantic identity trio.

### 9.2 Custom field types

Initial supported custom types should remain intentionally small:

```text
Text
Int64
Decimal/Double
Boolean
DateTimeOffset
Guid
```

String is represented by Text with semantic mode `None` when only exact/relational filtering is intended.

Future types such as enums, arrays, and multi-value fields should be versioned extensions rather than v1 complexity.

### 9.3 Semantic modes

Text-like fields support:

```text
None     // never embedded
Whole    // one semantic source/embedding operation for the field
Chunked  // OnnxTextEmbeddings.NET document chunking; one direct source per chunk
```

Numeric/date/bool/Guid fields are relational facts and should reject semantic configuration in v1.

### 9.4 Semantic field weights: percentages

SemanticKnowledge.NET should expose weights as human-understandable percentages from 0 through 100.

For a schema, enabled semantic fields MUST total exactly 100.

Example:

```text
Title        30
Description  20
Tags         20
Biography    30
             ---
             100
```

Invalid totals fail during schema registration/update. Do not silently normalize them because silent normalization hides configuration mistakes.

The existing `OnnxTextEmbeddings.NET` scorer uses its own versioned weight semantics rather than percentage multiplication. SemanticKnowledge therefore defines a versioned conversion profile, e.g. `SemanticWeightProfileV1`, that converts percentages into the scorer's effective field weights deterministically while retaining the developer-facing sum-to-100 model.

One reasonable V1 mapping is:

```text
scorerWeight = (percentage / 100) × enabledSemanticFieldCount
```

This preserves a mean scorer weight of 1.0 across enabled semantic fields while preserving relative emphasis. The exact mapping must be covered by unit tests and versioned before shipping; changing it later requires a new scoring/weight profile ID rather than silently changing old results.

A zero-weight field is disabled semantically and does not need embeddings.

### 9.5 Schema-time defaults versus query-time overrides

Schema weights define normal behavior.

Search requests may provide a complete query-specific weight profile. A custom override should also total exactly 100.

Do not mutate persisted schema weights for a single query.

Prefer complete override profiles over ambiguous partial overrides in v1. A later separate concept such as `Boost` may support convenient partial adjustments without weakening the percentage contract.

---

## 10. Strongly typed and dynamic schemas

Both programming styles lower to the same internal SchemaDefinition.

### 10.1 Typed C# schema

Example conceptual API:

```csharp
public sealed class NpcDocument : KnowledgeDocument
{
    public string Biography { get; init; } = "";
    public string Faction { get; init; } = "";
    public int Level { get; init; }
    public DateTimeOffset IntroducedAt { get; init; }
}

services.AddSemanticSchema<NpcDocument>(schema =>
{
    schema.Semantic(x => x.Title, 30);
    schema.Semantic(x => x.Description, 20);
    schema.Semantic(x => x.Tags, 20);
    schema.Semantic(x => x.Biography, 30, SemanticMode.Chunked);

    schema.Filterable(x => x.Faction);
    schema.Filterable(x => x.Level);
    schema.Filterable(x => x.IntroducedAt);
});
```

Expression trees are inspected for member identity. They are not dynamically compiled.

### 10.2 Runtime schema

```csharp
await schemas.CreateAsync("NPC", schema =>
{
    schema.Text("Biography")
        .Semantic(30)
        .Chunked();

    schema.Text("Faction")
        .Filterable();

    schema.Int64("Level")
        .Filterable();
});
```

Dynamic fields receive stable Field GUIDs and stable keys.

### 10.3 Built-in simple document schema

The zero-configuration Collection experience should have a built-in generic document schema containing:

- Title;
- Description;
- Tags;
- Body (chunked semantic).

This makes the easy path remain easy while advanced developers can register typed schemas.

---

## 11. Embedding architecture

### 11.1 One active embedding profile per Store in v1

All Collections, fields, documents, and queries in a store must live in the same active semantic space.

Do not permit arbitrary per-Collection dimensions/models in v1. That makes global and Smart Search unnecessarily complex and creates incompatible vector spaces.

### 11.2 Persisted embedding profile

Persist enough information to prove compatibility and know when derived metadata must be rebuilt:

- provider kind/identity;
- model ID/name/version for diagnostics;
- authoritative `EmbeddingSpaceFingerprint` or external `SpaceId`;
- native/source dimensions;
- configured output dimensions;
- reduction profile ID/version;
- logical storage preference;
- provider physical storage representation;
- tokenizer identity/capabilities where known;
- document chunking profile;
- document max tokens/chunk capacity;
- query max-token information where relevant;
- scoring profile ID/version;
- semantic-weight mapping profile ID/version.

Dimension equality alone is never considered compatibility.

### 11.3 Native dimensions are the default

SemanticKnowledge.NET should use the embedding model's native dimensions unless the user explicitly requests reduction.

Example:

```csharp
options.Embeddings.OutputDimensions = 1024;
```

If Jasper is natively 2048-dimensional, this creates a deterministic 1024-dimensional child space.

The library must document clearly that dimensional reduction is lossy. It should not imply that 1024 is semantically identical to 2048.

### 11.4 Reuse OnnxTextEmbeddings.NET direct reduction

For direct document chunks, use:

```csharp
TextEmbedding reduced = chunk.ReduceDimensions(...);
```

and equivalent query reduction.

Do NOT use `CombineToSingle()` merely to satisfy provider dimensional limits; document chunk arrays are the preferred representation, and direct reduction preserves chunk/source metadata.

### 11.5 Never silently reduce for a provider limit

If the selected provider cannot accept the configured active dimensions, startup must fail with an actionable message.

Example for SQL Server + native 2048 Jasper:

```text
The configured embedding space is 2048-dimensional, but this SQL Server
vector provider supports at most 1998 dimensions. Configure an explicit
OutputDimensions such as 1998 or 1024. Dimension reduction is lossy and
is never applied implicitly.
```

This preserves the user's control over fidelity.

---

## 12. Vector storage preferences

Do not expose a fake promise that every backend has the same native numeric format.

The normal configuration should express intent:

```csharp
VectorStoragePreference.Compact   // default
VectorStoragePreference.MaximumPrecision
VectorStoragePreference.ProviderDefault
```

Provider-specific advanced overrides may be available separately.

### 12.1 SQLite/sqlite-vec

Default compact representation: native signed INT8 vectors with cosine search.

FP32 remains available for maximum precision.

INT4/FP16 may exist in the upstream embedding library but do not map directly to sqlite-vec's native vector columns and should not be presented as native SQLite search formats.

### 12.2 PostgreSQL/pgvector

Default compact representation: `halfvec` when supported by the configured pgvector path.

Maximum precision: `vector`/float32.

A 2048-dimensional native Jasper vector can be stored in normal `vector`, but current pgvector approximate index dimension limits make `halfvec(2048)` or explicit reduction useful if approximate indexing is ever enabled.

### 12.3 SQL Server 2025/Azure SQL

Default: float32 native VECTOR.

Current SQL Server native VECTOR maximum is 1998 dimensions. Float16 support is preview-sensitive and must never be enabled automatically merely to save space.

### 12.4 Why INT8 is not universal

INT8 is an excellent compact choice for sqlite-vec because that backend directly supports native signed-INT8 cosine search and the upstream embedding package already has a compatible quantized representation.

It is not the universal database default because other vector engines expose different native storage/index primitives, most ecosystems standardize around float32/float16 vectors, and some ANN systems perform their own quantization internally. SemanticKnowledge should therefore optimize per provider instead of forcing an artificial common physical format.

---

## 13. Embedding providers

### 13.1 Default local ONNX provider

The blessed easy path uses the published `OnnxTextEmbeddings.NET` NuGet.

Reuse upstream abstractions for:

- document chunking;
- query embedding;
- token counting;
- `TextEmbedding` metadata;
- `QueryEmbedding`;
- embedding-space fingerprints;
- vector conversion;
- deterministic dimensional reduction;
- shared DefaultV1 scoring;
- database candidate adapters where applicable.

Do not copy these algorithms into SemanticKnowledge.NET.

### 13.2 Provider-neutral contract

Conceptual contract:

```csharp
public interface IKnowledgeEmbeddingProvider
{
    KnowledgeEmbeddingProviderInfo Info { get; }

    Task<QueryEmbedding> EmbedQueryAsync(
        string text,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TextEmbedding>> EmbedDocumentAsync(
        string text,
        CancellationToken cancellationToken = default);

    Task<TokenCountInfo?> TryCountTokensAsync(
        string text,
        CancellationToken cancellationToken = default);
}
```

The exact shape may use capabilities rather than nullable methods.

### 13.3 HTTP/custom API embeddings

Users may choose a remote API instead of local ONNX.

Configuration should require:

- endpoint/base URL;
- model/space identity;
- dimensions;
- authentication callback/configuration rather than hard-coded secrets;
- response mapping or built-in compatible protocol;
- optional tokenizer endpoint/callback;
- timeout/retry policy.

Remote APIs commonly return FP32. SemanticKnowledge validates dimensions/space then converts to the provider-native configured storage representation automatically.

### 13.4 Tokenizer capability is optional

Do not require every custom embedding API to expose tokenization.

Token counting is a capability. Features that require exact token budgets must fail early or explicitly use a caller-approved approximation if the configured provider cannot count tokens.

The default ONNX provider can expose the strong path because it already owns the tokenizer.

---

## 14. Ingestion, CRUD, and synchronization

### 14.1 CRUD

Core operations should include:

- create/update/delete KnowledgeBase;
- create/move/delete Collection;
- create/update/delete Schema;
- insert/upsert/delete Document;
- batch insert/upsert;
- fetch document(s);
- reset/rebuild store or KnowledgeBase;
- synchronize external authoritative sources.

### 14.2 Dirty semantic work

The library decides what must be re-embedded.

Examples:

- change nonsemantic DateTime field -> no embedding work;
- change semantic Biography -> re-embed only Biography sources;
- change semantic weights -> no inference; update scoring metadata;
- disable semantic field -> remove/retire its semantic sources;
- change Collection Description -> re-embed Collection identity;
- move document to another Collection -> routing metadata changes; no document re-embed unless v1 deliberately embeds collection-path context (recommended: do not couple document text to collection path in v1).

### 14.3 Transaction semantics

Canonical writes should be transactional.

Embedding inference is external/expensive work and may fail. Avoid keeping a database transaction open across long model calls.

Recommended pattern:

1. validate input/schema;
2. stage/calculate source hashes;
3. produce required embeddings with bounded work;
4. transactionally write canonical changes + semantic source/vector rows;
5. commit;
6. if a bulk generation/migration is being built, activate only when complete.

For very large syncs, use bounded batches and explicit progress/generation state rather than one enormous transaction.

### 14.4 `SyncAsync` is a first-class feature

Rebuildable wiki/document sources should have an easy synchronization API using ExternalId or a supplied identity selector.

Conceptual:

```csharp
await collection.SyncAsync(
    sourcePages,
    page => page.Path,
    page => new KnowledgeDocumentInput
    {
        Title = page.Title,
        Description = page.Summary,
        Tags = page.Tags,
        Body = page.Markdown
    });
```

Sync semantics:

- unseen ExternalId -> insert/embed;
- existing but changed source hash -> update only affected sources;
- unchanged hash -> no-op;
- missing source item -> optionally delete when `DeleteMissing=true`;
- caller can choose additive-only synchronization.

### 14.5 Sync must stream

Accept `IAsyncEnumerable<T>` where practical. Do not require materializing the entire external corpus in memory.

Providers may use a temporary/staging table of seen ExternalIds to calculate deletions without retaining an unbounded in-process HashSet.

---

## 15. Search input forms

SemanticKnowledge should support three levels of query input.

### 15.1 Plain text

```csharp
await wiki.SearchAsync("wizard encountered near the ruined tower");
```

The configured embedding provider embeds the query automatically.

### 15.2 Precomputed `QueryEmbedding`

```csharp
var queryEmbedding = await embeddings.EmbedQueryAsync(query);

await wiki.SearchAsync(queryEmbedding, ...);
```

This is valuable when the same query is used for multiple scopes or stages; it avoids repeated model inference.

### 15.3 Low-level precomputed vector

A low-level API may accept a vector only when accompanied by enough semantic-space identity to validate compatibility.

Do not make naked `float[]` with no fingerprint/SpaceId the normal API. Equal dimensions do not prove semantic compatibility.

---

## 16. Search modes

### 16.1 Scoped Search

Caller chooses one or more Collections and optionally descendants.

Use this whenever the application already knows where to look.

```csharp
var results = await npcs.SearchAsync(
    query,
    options => options.IncludeDescendants());
```

### 16.2 Global Search

Search all applicable semantic sources in the KnowledgeBase.

This should remain available and exact by default at the project's intended scale.

### 16.3 Smart Search

Smart Search performs semantic routing over Collections before document retrieval.

Conceptual pipeline:

```text
QueryEmbedding
      ↓
Collection identity search
      ↓
Relevant branches / collections
      ↓
Relational filters
      ↓
Document/chunk candidate retrieval
      ↓
Shared semantic scoring
      ↓
Results
```

Useful options may include:

- `MaxRoutedCollections`;
- `MinRoutingScore`;
- `IncludeDescendants`;
- `MaxRoutingDepth`;
- `AlwaysIncludeCollections`;
- `ExcludeCollections`.

Smart Search should be conservative about exclusion. Its purpose is to remove obviously unrelated semantic regions, not to create a brittle hard router that destroys recall.

### 16.4 Collection semantic identity

V1 Collection routing uses Title, Description, and Tags.

Future versions MAY maintain a collection-content aggregate/centroid/summary derived from descendants, but do not require an LLM-generated summary in v1.

---

## 17. Relational filtering

Relational filtering is a first-class feature, not an escape hatch.

Examples:

- only documents tagged `boss`;
- dates after a campaign start;
- level >= 15;
- status = `published`;
- specific schema;
- specific collection subtree;
- exact external ID;
- boolean flags;
- tenant/application domain IDs where represented as fields.

Then semantic ranking happens over the filtered candidate space.

### 17.1 Strongly typed C# filtering

Conceptual:

```csharp
var results = await npcs.SearchAsync(
    "forbidden-magic wizard",
    q => q
        .Where(x => x.Level >= 10)
        .Where(x => x.Faction == "Red Wizards")
        .Top(10));
```

SemanticKnowledge parses supported expression-tree constructs into a provider-neutral Filter AST. It does not pass arbitrary executable C# into the provider.

### 17.2 Initial supported filter AST

V1 should support a deliberate bounded subset:

- Eq / NotEq;
- Lt / Lte / Gt / Gte;
- IsNull / IsNotNull;
- And / Or / Not;
- In;
- text equality;
- optionally StartsWith/Contains when provider semantics can be made predictable;
- Tags Contains / Any / All;
- system IDs/schema/collection filters.

Unsupported arbitrary methods/functions fail during query construction with an actionable error.

### 17.3 Dynamic/native filtering

The same AST must have a versioned serializable representation for:

- runtime schemas;
- Native C ABI;
- HTTP/external callers;
- exportable query definitions if needed later.

### 17.4 SQL safety

Providers generate trusted SQL from the AST and bind user values as parameters.

Do not make raw interpolated SQL the normal SemanticKnowledge public API.

### 17.5 Filter before candidate truncation

Correctness requirement:

> A structured filter must be applied before the final native Top-K candidate cut whenever the query semantics require it.

Post-filtering an already truncated semantic candidate list can silently lose valid matches and is not acceptable as the default.

Provider integration tests must prove filter+vector behavior. If an upstream adapter/backend cannot preserve prefilter semantics for a particular native ANN path, SemanticKnowledge should choose a correctness-preserving exact path rather than silently changing query meaning.

---

## 18. Database-native candidate retrieval and scoring

The current `OnnxTextEmbeddings.NET` database adapters already define the preferred architecture:

```text
QueryEmbedding
      ↓
relational filters + embedding-space fingerprint
      ↓
database-native cosine / KNN
      ↓
bounded direct-chunk candidates
      ↓
shared core DefaultV1 scoring
      ↓
final item results
```

SemanticKnowledge should wrap and reuse that design rather than reimplementing scoring independently per database.

The upstream database adapters default to candidate over-fetching based on `max(100, Top × 10)`, because retrieving only `Top` chunks can miss supporting fields/chunks needed to rank final documents. SemanticKnowledge may tune this through its own versioned defaults but should retain the same principle.

The database returns plausible direct chunk candidates. The shared scorer combines:

- raw cosine;
- gentle length confidence;
- strongest chunk evidence;
- bounded supporting chunk evidence;
- semantic field weights;
- item-level evidence aggregation.

This deterministic scorer is **not** the future neural reranker described later in this document.

---

## 19. Search results and hydration

Search should not hydrate giant documents by default.

### 19.1 Compact result is the default

A normal hit should contain roughly:

```text
DocumentId
CollectionId
SchemaId
Score
Title
Description
Tags
BestMatch metadata
Top matched semantic-source/chunk references
Scoring/fingerprint diagnostics
```

Do not attach an entire 32k-token body to every search hit unless requested.

### 19.2 Explicit hydration APIs

Provide:

```csharp
GetDocumentAsync(id)
GetDocumentsAsync(ids)
```

and a convenience:

```csharp
SearchDocumentsAsync(...)
```

that searches then hydrates the requested result count.

### 19.3 Include modes

A search request may expose an inclusion policy such as:

```text
MetadataOnly        // default
MatchedChunks
FullDocument
```

Keep response size intentional.

### 19.4 Token-budget retrieval

A very useful higher-level retrieval API should allow:

> Return the best evidence/content up to approximately X tokens.

Because direct chunk metadata preserves source ranges and token counts, SemanticKnowledge can select the strongest matched chunks and hydrate only those sections until a budget is met.

Exact token-budget behavior requires a compatible token-count capability/profile. If exact tokenization is unavailable, the library must not pretend an arbitrary character estimate is exact.

Potential API:

```csharp
var context = await wiki.SearchContentAsync(
    query,
    options => options.MaxContentTokens(8_000));
```

The result should identify source Document/Field/Chunk IDs so applications can trace where returned text came from.

---

## 20. Embedding profile changes and generations

Embedding configuration changes must never leave mixed incompatible vectors active.

### 20.1 Generation model

Persist embedding generations:

```text
Generation A - Active
Generation B - Building
```

When configuration requires a rebuild:

1. keep Generation A active;
2. create Generation B physical vector table/profile;
3. regenerate semantic sources/vectors from canonical text with bounded work;
4. validate completion;
5. atomically set Generation B active;
6. retire/drop Generation A after the switch.

If the process dies during step 3, Generation A remains usable. Startup can resume or discard the incomplete generation.

### 20.2 Changes that require a new embedding generation

Examples:

- embedding-space fingerprint/model changes;
- native/source dimensions change;
- configured output dimensions change;
- reduction profile/version changes;
- tokenizer/chunking profile changes that change chunk boundaries/token metadata;
- semantic source generation algorithm changes;
- provider physical vector representation changes when conversion cannot be performed safely from retained canonical vector data.

### 20.3 Changes that do NOT require inference

Examples:

- semantic field weights change;
- display labels change while stable field identity remains;
- nonsemantic field changes;
- Collection hierarchy move if path text is not embedded into v1 document chunks.

---

## 21. Logical schema versioning and migrations

There are two independent migration domains.

### 21.1 Engine migrations

Owned entirely by SemanticKnowledge.NET/provider packages.

These evolve internal physical tables. They run automatically and transactionally where the backend supports it.

### 21.2 Caller logical database version

Applications may declare a logical version similar in spirit to IndexedDB:

```csharp
options.DatabaseVersion = 3;
```

SemanticKnowledge supplies migration primitives, but does not attempt to infer arbitrary business-data transformations.

Conceptual:

```csharp
services.AddSemanticKnowledge(options => options.DatabaseVersion = 3)
    .Migrate(1, 2, async migration =>
    {
        // caller-controlled transformation
    })
    .Migrate(2, 3, async migration =>
    {
        // caller-controlled transformation
    });
```

If stored version differs from configured version and no valid path exists, fail startup rather than guessing.

### 21.3 Compatible automatic schema evolution

The library can safely handle some changes without custom migration code:

- add optional field;
- change display name while stable field ID/key remains;
- change semantic weight;
- add/remove filter index hint;
- make a semantic field nonsemantic and retire derived sources;
- make an existing text field semantic, then build embeddings from existing source values.

### 21.4 Explicit migration or rebuild required

Examples:

- incompatible field type change;
- add required field without default/backfill;
- split one field into several fields;
- merge several fields;
- change stable field identity/key with existing data;
- caller-specific parsing/transformation;
- collection/document restructuring requiring business knowledge.

### 21.5 Failure safety

A failed logical migration must not:

- increment the stored version;
- leave partial canonical transformations committed;
- activate a partial embedding generation;
- delete the old generation before the new one is valid.

---

## 22. Persistence modes: Authoritative and Rebuildable

The earlier design tension about whether this database is the only source of truth is resolved by making both use cases explicit.

### 22.1 `Authoritative`

SemanticKnowledge owns canonical data the application cares about.

Breaking changes require intentional migration. `ResetAsync` requires explicit destructive intent.

### 22.2 `Rebuildable`

SemanticKnowledge is an indexed projection of another source such as Markdown files, an application database, or a wiki service.

The application may choose to discard/rebuild derived storage when a breaking logical or embedding change occurs.

This is expected to be common for personal wikis/local AI applications.

### 22.3 Reset/rebuild should be first-class

Do not force callers to manually delete a `.db` file.

Provide explicit APIs such as:

```csharp
await store.ResetAsync();
await knowledgeBase.ResetAsync();
```

with clear destructive semantics.

---

## 23. Backup, export, import, and provider portability

SemanticKnowledge should provide a backend-neutral logical archive rather than treating a SQLite file as the universal backup format.

Suggested ZIP archive:

```text
manifest.json
schemas.json
collections.json
documents.jsonl
embedding-profile.json
embeddings/          // optional
```

### 23.1 Always preserve

- GUID identities;
- ExternalIds;
- hierarchy;
- schemas/fields;
- canonical document data;
- tags;
- logical database version;
- embedding-profile metadata sufficient to diagnose compatibility.

### 23.2 Embeddings optional

Vectors are derived and backend representations differ, so portable exports should allow embeddings to be omitted and regenerated after import.

### 23.3 Streaming

Documents should use JSONL or another streaming format rather than a single giant JSON array. ZIP compression is appropriate for the export artifact because this is an offline archive format, not live searchable storage.

This enables:

```text
SQLite -> export -> PostgreSQL
PostgreSQL -> export -> SQL Server
SQL Server -> export -> SQLite
```

without exposing provider internals.

Provider-native physical backup/HA remains a database-administration concern and can be documented separately.

---

## 24. SQLite provider design

SQLite is the default and most frictionless provider.

### 24.1 Dependencies

Use the published `OnnxTextEmbeddings.NET.SqliteVec` NuGet adapter and its tested sqlite-vec dependency/loading helpers wherever possible.

### 24.2 Search

Keep candidate vector search inside sqlite-vec.

Default compact native storage: INT8 cosine vectors.

FP32 optional.

### 24.3 Provider behavior

- create/open configured database file;
- load sqlite-vec extension before vector operations;
- capability/version check at startup;
- apply engine migrations;
- use transactions for canonical mutations;
- generate safe vec0 tables per embedding generation;
- compile filter AST to parameterized SQLite SQL;
- use SQLite indexes for typed EAV/filter tables;
- avoid pulling unbounded vector sets into managed memory.

### 24.4 sqlite-vec pre-v1 policy

sqlite-vec remains upstream pre-v1. Keep all sqlite-vec-specific assumptions isolated in the provider and continuously integration-test Linux, Windows, and macOS. A breaking upstream release should be absorbed by the provider rather than leaking into core contracts.

### 24.5 Database size

Do not invent application-level compression of source text/vectors. The primary size controls are:

- compact native INT8 vectors;
- optional dimension reduction;
- not embedding nonsemantic fields;
- source-hash no-op synchronization;
- cleaning retired generations;
- normal SQLite maintenance/VACUUM guidance when appropriate.

---

## 25. PostgreSQL provider design

### 25.1 Dependencies

Use `OnnxTextEmbeddings.NET.PgVector` and the appropriate Npgsql/pgvector dependencies.

### 25.2 Startup

- validate connection;
- create/use configured dedicated schema;
- verify/install expectations for the `vector` extension; do not silently attempt privileged operations unless explicitly configured;
- apply engine migrations;
- validate configured dimensions/storage.

### 25.3 Search

Exact search is the default.

Approximate HNSW/IVFFlat is an explicit opt-in capability for larger stores, not the product's assumed mode.

### 25.4 Storage

Compact default should map to HalfVector/`halfvec` where supported. Float32 `vector` is available for maximum precision.

A 2048-dimensional native model is valid storage. Approximate index constraints are provider-specific and should be surfaced by capability validation.

### 25.5 Credentials

Examples should use `IConfiguration`, environment variables, .NET user-secrets during development, or production secret managers. README examples should not encourage committed plaintext production credentials.

---

## 26. SQL Server provider design

### 26.1 Target

SQL Server 2025/Azure SQL native vector support.

### 26.2 Dependencies

Use the published `OnnxTextEmbeddings.NET.SqlServer` adapter and `Microsoft.Data.SqlClient` provider as appropriate.

### 26.3 Dimension rule

Current SQL Server VECTOR supports at most 1998 dimensions.

SemanticKnowledge does **not** silently invoke the upstream 2048->1998 convenience reduction. Native dimensions remain the SemanticKnowledge default. If the configured model is 2048 and SQL Server is selected, startup requires the user to explicitly configure a compatible `OutputDimensions` such as 1998 or 1024.

### 26.4 Search

Exact native `VECTOR_DISTANCE` search is default.

Approximate/vector-index search is explicit opt-in and capability-gated. Do not automatically enable preview server features.

### 26.5 Float16

Treat SQL Server float16 vector support as preview-sensitive. `Compact` should remain safe/provider-supported by default; do not silently enable preview flags to get FP16.

### 26.6 NativeAOT

SQL Server managed support is required. SQL Server NativeAOT support is promised only after an actual provider AOT smoke test passes in CI. The library should report provider capabilities accurately rather than claiming AOT from project metadata alone.

---

## 27. Exact versus approximate search

The product's intended working set strongly favors exact search as the default.

Reasons:

- small-to-medium knowledge stores can afford it;
- exact search provides maximum recall;
- structured filters frequently shrink candidate space dramatically;
- the project values predictable fidelity over hyperscale throughput;
- database providers already perform the computation natively.

Approximate search MAY be exposed where providers support it, but it should always be explicit and diagnostics should identify the retrieval mode.

---

## 28. Hybrid lexical search

The architecture must leave room for hybrid lexical + semantic retrieval, but v1 should not become bloated merely to claim the feature.

Canonical text remains normal searchable text specifically so future providers can integrate:

- SQLite FTS5/BM25;
- PostgreSQL full-text search;
- SQL Server full-text search.

Potential future modes:

```text
Semantic
Lexical
Hybrid
```

Hybrid candidate sets could use deterministic fusion such as RRF or an optional future neural reranker. This is an extension point, not a requirement for the first implementation.

---

## 29. Rerankers: current decision and future extension

### 29.1 V1 does NOT include a neural reranker

Do not confuse the existing shared `DefaultV1` deterministic candidate scorer in `OnnxTextEmbeddings.NET` with a cross-encoder/neural reranker.

The initial SemanticKnowledge pipeline does not require an additional model reranker because:

- retrieval is exact by default at the intended scale;
- relational filtering is available;
- Collections provide semantic routing;
- fields are independently embedded and weighted;
- direct chunks are scored independently;
- current DefaultV1 uses strongest evidence + bounded support;
- the product deliberately avoids unnecessary second model inference.

### 29.2 How often should a future reranker fire?

A reranker should operate **once per user search over a bounded final candidate set**, never over the entire stored corpus.

Possible future policy:

```text
Never       // default
Always      // rerank top K every query
Auto        // only when result ambiguity/quality policy warrants it
Evidence    // use reranker when contribution/evidence output is requested
```

`Auto` can eventually use signals such as close top scores, mixed lexical/semantic candidates, or explicit quality mode. Do not invent that heuristic until benchmarks justify it.

### 29.3 Future interface

Reserve a clean extension point:

```csharp
public interface IKnowledgeReranker
{
    KnowledgeRerankerCapabilities Capabilities { get; }

    Task<IReadOnlyList<RerankedCandidate>> RerankAsync(
        KnowledgeRerankRequest request,
        CancellationToken cancellationToken = default);
}
```

The pipeline position is after normal bounded retrieval/scoring:

```text
DB candidate retrieval
      ↓
DefaultV1 scoring
      ↓
Top bounded candidates
      ↓
optional IKnowledgeReranker
      ↓
final result ordering/evidence
```

### 29.4 Prism-Reranker and future evidence

Prism-Reranker is particularly aligned with a planned future SemanticKnowledge result mode because it can return more than a relevance scalar: contribution/evidence outputs can support a future `Evidence` response that gives callers the relevant information without forcing full-document hydration.

This is intentionally future work.

The result model should therefore not assume that ranking can only ever produce a scalar. Future diagnostics/evidence can attach:

- reranker score;
- contribution statement;
- evidence text;
- source Document/Field/Chunk references;
- original semantic match diagnostics.

### 29.5 Local and HTTP rerankers

Future reranker orchestration should be provider-neutral.

A local ONNX reranker package and a remote HTTP reranker can both implement the same interface. SemanticKnowledge hides candidate construction, batching, retries/cancellation, and evidence mapping behind the provider contract.

---

## 30. Future reranker memory safety / “digestion”

A reranker must never cause an application to hydrate the entire corpus or many 32k-token documents into memory.

The future pipeline must:

1. retrieve only a bounded top candidate set;
2. use matched semantic chunks/source ranges to construct candidate windows;
3. optionally include neighboring chunks/context, still under a per-candidate token cap;
4. stream candidates through a bounded channel;
5. batch by **total tokens/bytes**, not only number of documents;
6. cap in-flight batches;
7. release candidate buffers after each batch;
8. honor CancellationToken and request timeouts;
9. never materialize all candidate full-document text by default;
10. expose memory/token diagnostics in benchmark/integration tests.

The stored `TextEmbedding` source/token metadata is deliberately retained now so this future pipeline can extract evidence windows without redesigning persistence.

---

## 31. Shared ONNX runtime/queue concerns

SemanticKnowledge.NET should not own ONNX session scheduling, model health, model copies, or inference queue logic. `OnnxTextEmbeddings.NET` already owns those concerns for embeddings.

When a separate local reranker library is added in the future, it should initially own its own inference runtime abstraction.

If substantial identical scheduling/session-health code appears in both sibling model libraries, extract a deliberately small common lower-level runtime package at that time. Do not prematurely create a generalized runtime framework before real duplication exists.

SemanticKnowledge only owns **workflow-level backpressure** such as bounded document ingestion and bounded candidate/reranker pipelines.

---

## 32. Memory-bounded ingestion

Bulk ingestion must be designed for stable memory use even if callers stream very large sources.

Use:

- `IAsyncEnumerable<T>` input where appropriate;
- bounded `Channel<T>`/pipeline stages;
- configurable maximum in-flight documents;
- configurable maximum in-flight source text bytes/tokens;
- model queue provided by the embedding package rather than a second unbounded queue;
- batched DB writes;
- source hashes to avoid unnecessary re-embedding;
- temporary/staging provider tables for very large Sync seen-ID sets;
- no corpus-wide `List<TextEmbedding>` accumulation.

Unit/integration tests should assert bounded-memory behavior with a fake embedding provider so memory tests do not depend on actual inference speed.

---

## 33. Validation: protect developers from themselves

SemanticKnowledge should fail early and loudly when configuration is invalid.

### 33.1 Startup validation

Validate:

- exactly one storage provider selected;
- embedding provider configured;
- embedding space ID/fingerprint stable/non-empty;
- model/native dimensions > 0;
- OutputDimensions <= native dimensions;
- provider supports OutputDimensions;
- storage preference maps to a supported physical representation;
- all schemas valid;
- field keys unique within schema;
- protected system fields present;
- semantic modes only on compatible field types;
- semantic percentages 0..100 and enabled semantic fields total exactly 100;
- stored engine migration can be applied;
- caller logical version has a valid migration path or allowed rebuild behavior;
- active embedding generation matches configured profile or a rebuild is scheduled;
- sqlite-vec/vector/SQL Server capabilities are present;
- custom HTTP embedding endpoint configuration is complete;
- required token-count capability exists for enabled exact-token features.

### 33.2 Runtime validation

Reject:

- mismatched `EmbeddingSpaceFingerprint`;
- wrong dimensions;
- unsupported filter AST expressions;
- invalid field values/types;
- required fields missing;
- unsafe schema modifications without migration;
- token-budget operations with no valid token metadata/capability;
- approximate-search requests where provider capability is absent;
- naked user SQL in normal APIs.

### 33.3 No silent “helpful” fidelity changes

Never silently:

- reduce vector dimensions;
- switch exact search to approximate;
- normalize invalid semantic weights;
- compare equal-dimension vectors from different spaces;
- enable SQL Server preview features;
- drop failed migration data;
- fall back to in-memory corpus-wide cosine because a native provider failed.

---

## 34. Best-practice guidance to ship with the library

Documentation should be opinionated enough to help users get good results.

### Organizing knowledge

- Create Collections around meaningful semantic domains, not every tiny organizational whim.
- Write descriptive Collection Titles/Descriptions/Tags because Smart Search uses them for routing.
- Reuse Schemas for conceptually identical document types.
- Do not create a different schema merely because two Collections have different names.

### Designing fields

- Make natural-language meaning semantic.
- Keep exact facts such as dates, versions, booleans, IDs, statuses, and levels relational/filterable.
- Use `Whole` for short coherent fields such as Title, Description, Personality summary.
- Use `Chunked` for long prose such as Body, Biography, Notes, Lore.
- Do not embed identifiers/numeric metadata simply because embedding APIs accept strings.

### Searching

- Explicitly scope/filter when the application already knows the domain.
- Use Smart Search when the location is genuinely unknown.
- Use Global Search for broad discovery.
- Filter exact constraints before semantic ranking.
- Do not add a reranker until retrieval benchmarks demonstrate a real quality gain worth its inference cost.

### Persistence

- Use `Rebuildable` when the authoritative content lives elsewhere.
- Use `Authoritative` only when this store genuinely owns canonical content.
- Use ExternalId for synchronization with files/application records.
- Use logical export for provider portability; use native DB backup tools for infrastructure backup/HA.

### Embeddings

- Keep native model dimensions unless storage/provider constraints justify a deliberate reduction.
- Document that 2048->1024 is lossy but legitimate and deterministic when both documents and queries use the same reduction profile.
- Prefer Compact storage for local/wiki workloads unless measured quality requires maximum precision.
- Never mix embedding spaces because dimensions happen to match.

---

## 35. Proposed public API shape

Names below are design targets, not an ABI commitment until implementation/tests settle them.

### 35.1 Registration

```csharp
builder.Services
    .AddSemanticKnowledge(options =>
    {
        options.DatabaseVersion = 1;
        options.PersistenceMode = KnowledgePersistenceMode.Rebuildable;

        options.Embeddings.OutputDimensions = null; // native dimensions
        options.Embeddings.Storage = VectorStoragePreference.Compact;
    })
    .UseSqlite("knowledge.db")
    .UseOnnxEmbeddings();
```

PostgreSQL:

```csharp
builder.Services
    .AddSemanticKnowledge(...)
    .UsePostgreSql(configuration.GetConnectionString("SemanticKnowledge")!)
    .UseOnnxEmbeddings();
```

SQL Server:

```csharp
builder.Services
    .AddSemanticKnowledge(options =>
    {
        options.Embeddings.OutputDimensions = 1024;
    })
    .UseSqlServer(configuration.GetConnectionString("SemanticKnowledge")!)
    .UseOnnxEmbeddings();
```

Remote embeddings:

```csharp
builder.Services
    .AddSemanticKnowledge(...)
    .UseSqlite("knowledge.db")
    .UseHttpEmbeddings(options =>
    {
        options.Endpoint = new Uri(configuration["Embeddings:Endpoint"]!);
        options.Model = "my-embedding-model";
        options.SpaceId = "my-embedding-model-v3";
        options.Dimensions = 2048;
    });
```

### 35.2 Knowledge and Collections

```csharp
ISemanticKnowledgeStore store = ...;

var wiki = await store.GetOrCreateKnowledgeBaseAsync("Personal Wiki");
var npcs = await wiki.Collections.GetOrCreateAsync<NpcDocument>("NPCs");
```

### 35.3 Documents

```csharp
Guid id = await npcs.UpsertAsync(new NpcDocument
{
    Title = "Ezmerelda d'Avenir",
    Description = "A monster hunter encountered in Barovia.",
    Tags = ["npc", "hunter"],
    Biography = "...",
    Level = 12
});
```

External identity:

```csharp
await npcs.UpsertExternalAsync(
    externalId: "npcs/ezmerelda",
    document);
```

### 35.4 Search

```csharp
var hits = await wiki.SearchAsync("monster hunter near Vallaki");
```

Scoped:

```csharp
var hits = await npcs.SearchAsync("monster hunter");
```

Filtered:

```csharp
var hits = await npcs.SearchAsync(
    "dangerous wizard",
    q => q
        .Where(x => x.Level >= 10)
        .Where(x => x.Tags.Contains("npc"))
        .Top(10));
```

Smart:

```csharp
var hits = await wiki.SearchAsync(
    "who was the wizard at the ruined tower?",
    q => q.Mode(KnowledgeSearchMode.Smart));
```

Precomputed query:

```csharp
var queryEmbedding = await embeddingProvider.EmbedQueryAsync(text);
var hits = await wiki.SearchAsync(queryEmbedding);
```

---

## 36. Native interoperability design

SemanticKnowledge.Native should mirror the successful principles already established in `OnnxTextEmbeddings.Native`.

### 36.1 ABI principles

- explicit `SK_ABI_VERSION`;
- versioned option structs with `struct_size` and `abi_version`;
- opaque handles;
- UTF-8 as pointer + explicit length;
- blocking v1 calls; foreign runtimes schedule them themselves;
- managed exceptions never cross ABI boundary;
- stable numeric status codes;
- thread-local last-error message;
- library-owned buffers freed with `sk_buffer_free`;
- no exported CLR object layouts;
- no first-party promise for every language wrapper.

### 36.2 Complex operations via versioned JSON

Avoid a giant C function forest for every schema/filter variant.

Keep lifecycle/common hot operations as normal C functions and pass complex declarative payloads as versioned UTF-8 JSON where appropriate.

Conceptual:

```c
uint32_t sk_abi_version(void);

sk_status sk_store_open_json(
    const uint8_t *options_json,
    size_t options_length,
    intptr_t *store_handle);

sk_status sk_schema_create_json(...);
sk_status sk_document_upsert_json(...);
sk_status sk_search_json(...);
void sk_buffer_free(sk_buffer *buffer);
void sk_store_destroy(intptr_t store_handle);
```

The native API should support SQLite first because it is the easiest self-contained interoperability deployment. PostgreSQL/SQL Server native provider bundles can be added only when their AOT/runtime dependencies are proven by CI.

### 36.3 Non-.NET promise

The project promises a stable, tested C ABI and public header. Rust/Go/Python/C++/Zig/etc. may build bindings over it, but those wrappers are not automatically first-party supported SDKs.

---

## 37. Documentation plan

The root README should be excellent but intentionally concise:

1. one-paragraph scope;
2. install packages;
3. five-minute SQLite + ONNX example;
4. define a schema;
5. insert a document;
6. semantic + relational-filter search;
7. provider choices;
8. project scope/non-goals;
9. documentation links.

Detailed material belongs in `docs/`.

Recommended docs:

- `getting-started.md`
- `concepts.md`
- `schemas.md`
- `collections.md`
- `search.md`
- `filtering.md`
- `embedding-profiles.md`
- `migrations.md`
- `sync-and-rebuild.md`
- `sqlite.md`
- `postgres.md`
- `sql-server.md`
- `backup-restore.md`
- `native-aot.md`
- `native-interop.md`
- `best-practices.md`
- `troubleshooting.md`

Interop docs must explain how native callers specify:

- provider type;
- SQLite path or server connection details;
- embedding provider/model/fingerprint/dimensions;
- schema JSON;
- filter JSON;
- text query versus precomputed embedding;
- buffer ownership/error handling.

---

## 38. NuGet packaging

All managed packages target .NET 10.

Recommended shared build properties mirror the sibling embedding project:

- nullable enabled;
- implicit usings;
- warnings as errors;
- latest analysis;
- deterministic builds;
- XML docs;
- repository metadata/SourceLink;
- `IsAotCompatible` when continuously proven.

### 38.1 Icon and README

The repository already contains:

```text
128x128_compressed.png
README.md
```

Every published NuGet package should use:

```xml
<PackageReadmeFile>README.md</PackageReadmeFile>
<PackageIcon>128x128_compressed.png</PackageIcon>
```

and pack those files from the repository root into the package root.

### 38.2 Package IDs

Initial managed package IDs:

```text
SemanticKnowledge.NET
SemanticKnowledge.NET.Sqlite
SemanticKnowledge.NET.PostgreSql
SemanticKnowledge.NET.SqlServer
SemanticKnowledge.NET.Http
```

Only create/publish `SemanticKnowledge.NET.Http` when its implementation is part of the first release; otherwise reserve the namespace conceptually and avoid empty packages.

`SemanticKnowledge.Native` is a native distribution project, not necessarily a NuGet package.

---

## 39. Release workflow

The required workflow filename is exactly:

```text
.github/workflows/publish-nuget.yml
```

Trigger:

```yaml
on:
  push:
    branches:
      - release
  workflow_dispatch:
```

The repository already has a `release` branch and the NuGet trusted-publisher relationship is configured for that repository/branch.

The workflow should adapt the established `OnnxTextEmbeddings.NET` release pattern:

1. checkout full history/tags;
2. setup .NET 10;
3. detect package-affecting changes;
4. resolve the next release version;
5. validate build/tests on Linux, Windows, macOS;
6. run required provider/AOT gates;
7. build and pack exact package version;
8. validate `.nupkg` metadata/icon/readme/dependencies;
9. authenticate with `NuGet/login@v1` using trusted publishing and `NUGET_USER`;
10. push all managed packages;
11. tag `v<version>`;
12. create GitHub Release.

The sibling project currently starts at `0.1.0` and resolves subsequent releases by taking the highest published/tagged stable version and incrementing patch. SemanticKnowledge can reuse that simple release engine unless a different semantic-versioning policy is intentionally adopted before the first release.

Publish job permissions must include `id-token: write` for trusted publishing.

Do not require a long-lived NuGet API key secret.

---

## 40. CI and test strategy

Testing must verify semantics, persistence, provider equivalence, AOT, and native interoperability — not merely unit-test helper classes.

### 40.1 Core unit tests

Test:

- GUID/external identity rules;
- system-field protection;
- schema validation;
- semantic weight total = 100;
- weight percentage -> upstream scorer mapping;
- field type/semantic mode validation;
- typed expression member extraction;
- expression -> Filter AST;
- unsupported filter rejection;
- embedding profile identity/equality;
- dimension reduction profile compatibility;
- query fingerprint mismatch failure;
- schema diff classification;
- dirty/reembedding decisions;
- logical migration path/rollback semantics;
- compact hit/hydration behavior;
- export/import round trip;
- source range/token metadata round trip;
- HTTP provider retries/timeouts/dimension validation with fake HTTP handlers.

### 40.2 Provider conformance suite

A shared test fixture should be executed against all providers to prove equivalent logical behavior:

- create schema/hierarchy;
- insert documents;
- exact semantic search;
- structured filtering + semantic ranking;
- tags;
- descendant scope;
- Smart Search routing;
- update one semantic field;
- update one nonsemantic field;
- delete/move document;
- restart and recover;
- embedding-generation switch;
- export/import;
- fingerprint safety.

Scores may have small provider numeric tolerances, but result semantics/ranking should remain predictable.

### 40.3 SQLite integration

Real sqlite-vec, Linux/Windows/macOS.

Test FP32 and INT8 paths, extension loading, dimensions, filters, database reopen, generation table retirement.

### 40.4 PostgreSQL integration

Run a real PostgreSQL+pgvector service/container.

Test exact search, HalfVector/Vector mapping, extension capability, filters/indexes, migration transactions.

### 40.5 SQL Server integration

Run SQL Server 2025 where CI licensing/runner support permits.

Test exact native vector search, 1998 boundary, deliberate 1024 reduction, failure at incompatible 2048, relational filters, capability detection.

### 40.6 Memory/backpressure tests

Using a fake deterministic embedding provider:

- stream a large synthetic corpus;
- verify bounded number/bytes of in-flight work;
- verify Sync does not materialize full corpus;
- verify cancellation releases buffers/tasks;
- verify large fields are chunked/processed incrementally where upstream permits;
- future reranker candidate builder test should enforce token/byte caps before Prism or another model is integrated.

### 40.7 Failure/recovery tests

Inject failures:

- embedding failure mid-batch;
- DB write failure;
- migration callback failure;
- process/restart around incomplete embedding generation;
- HTTP transient failures;
- mismatched configured profile after restart.

Old active generation must remain valid until replacement is complete.

---

## 41. NativeAOT CI

Create `.github/workflows/aot-integration.yml` modeled after the sibling project.

### Managed AOT smoke

On Linux, Windows, macOS:

1. publish a SemanticKnowledge AOT smoke executable;
2. use SQLite provider where supported;
3. create temporary store;
4. define schema;
5. insert documents using a deterministic fake provider for fast portability;
6. search/filter;
7. verify result;
8. optionally run a real ONNX Jasper Linux smoke as a deeper scheduled/release gate.

### Native ABI smoke

On Linux, Windows, macOS:

1. publish `SemanticKnowledge.Native` shared library;
2. compile standalone C program against `native/include/semantic_knowledge.h`;
3. dynamically load `.so`/`.dll`/`.dylib`;
4. check ABI/version;
5. create SQLite store;
6. create schema;
7. upsert/search/get result;
8. release returned buffers;
9. destroy handles.

Provider-specific server NativeAOT gates can be added after the managed provider is stable.

---

## 42. Workflow set

Recommended initial workflows:

```text
ci.yml
    build + core tests

aot-integration.yml
    managed NativeAOT + C ABI smoke

sqlite-integration.yml
    real sqlite-vec matrix

postgres-integration.yml
    real PostgreSQL/pgvector

sqlserver-integration.yml
    real SQL Server native vector tests

publish-nuget.yml
    release branch -> validated trusted publish
```

Release publication should depend on the important correctness gates rather than publishing after only a compile.

Scheduled deeper integration tests may be used for expensive model/server combinations, but pull requests must still cover the provider logic with deterministic/fake layers plus at least the practical real-provider gates.

---

## 43. Observability and diagnostics

Expose useful diagnostics without forcing a monitoring framework:

- configured/active embedding profile;
- active generation ID/status;
- provider capabilities;
- storage physical vector kind;
- native/output dimensions;
- document/semantic-source/vector counts;
- pending generation/rebuild status;
- last migration version;
- search mode (exact/approximate/smart);
- candidate count;
- shared scoring profile/version;
- optional provider-native similarity diagnostics;
- embedding provider diagnostics forwarded from `OnnxTextEmbeddings.NET` where useful.

Do not expose secrets or connection strings through diagnostics.

---

## 44. Security and credentials

- All generated SQL is parameterized.
- Dynamic schema/field names are never interpolated directly into physical identifiers; stable internal IDs drive tables/columns.
- Provider schema/table generation uses internal sanitized identifiers only.
- Server credentials should come from IConfiguration/environment/user secrets/secret managers.
- HTTP API keys should support configuration delegates/headers without persisting them into SemanticKnowledge metadata.
- Export archives must not include DB/API credentials.
- Native last-error strings must not include connection secrets.

SemanticKnowledge is not an authorization framework. Applications still own user authorization and must apply appropriate scope/security filters before returning data.

---

## 45. Performance philosophy

Optimize for predictable local/small-server performance, not benchmark theater.

Primary wins are architectural:

- native database vector comparison;
- relational prefiltering;
- compact INT8/FP16 provider storage;
- source-hash no-op updates;
- only embed semantic fields;
- direct chunk arrays instead of giant combined vectors;
- bounded candidate over-fetch;
- no default neural reranker;
- bounded ingestion;
- optional explicit dimensional reduction.

Do not add caching/parallelism layers without measuring whether they duplicate database page cache, upstream ONNX queueing, or provider pooling.

---

## 46. Specific design answers to the pre-implementation checklist

### How often do rerankers need to fire?

Not in v1. A future neural reranker should run once per search over a bounded top-K candidate set, not over the corpus. Default policy should remain off until benchmarks prove value. Prism/evidence mode may become a strong opt-in reason to invoke one.

### Why is INT8 semantic storage not more commonplace?

Because vector database ecosystems and hardware paths historically standardize around FP32/FP16, provider-native vector types differ, and many ANN systems perform separate internal quantization. sqlite-vec's native signed-INT8 cosine support makes INT8 unusually natural for our SQLite-first workload. SemanticKnowledge should use the best native compact representation per backend rather than force one universal format.

### Is the existing 2048 -> 1024 system bad?

No. It is a legitimate deterministic, lossy projection when both document chunks and queries use the same reduction profile. Native dimensions remain the default for fidelity; 1024 is an intentional storage/provider-compatibility option. Persist and validate the derived embedding-space fingerprint/profile.

### Microsoft SQL and SQLite-vec support?

Yes. SQLite/sqlite-vec, PostgreSQL/pgvector, and SQL Server 2025/Azure SQL are first-class providers. Broad filtering/vector candidate work stays in the DB; only bounded candidates reach the shared scorer.

### Add the new reranker?

Not in the initial implementation. Reserve a provider-neutral extension point now. A separate local reranker library can be integrated later without rewriting search orchestration.

### Shared queue/runtime abstractions?

Do not move ONNX queue/session logic into SemanticKnowledge. Let the embedding package own embedding inference. If the future reranker library duplicates substantial generic runtime scheduling, extract a separate shared low-level library only then.

### Can Prism work through normal API calls?

Future design: yes. `IKnowledgeReranker` hides whether the implementation is local ONNX or remote HTTP. SemanticKnowledge owns candidate/evidence orchestration; the provider owns model/API execution.

### Prevent Prism digestion memory blowout?

Future design explicitly uses bounded candidates, matched chunk/source windows, token/byte-capped batches, streaming channels, and no full-corpus/full-document hydration by default.

---

## 47. Implementation phases

### Phase 0 — repository/build foundation

- solution/build props/package props;
- package projects;
- central dependency versions;
- root README/icon packaging;
- CI;
- release scripts/workflow skeleton;
- AOT smoke skeleton.

### Phase 1 — core domain/schema/filter model

- IDs/entities;
- SchemaDefinition;
- system fields;
- typed/runtime schema builders;
- weight validation/mapping;
- Filter AST;
- expression parser;
- provider contracts;
- diagnostics/error model.

### Phase 2 — SQLite first vertical slice

- SQLite physical schema/migrations;
- sqlite-vec loading;
- KnowledgeBase/Collection/Schema/Document CRUD;
- ONNX embedding provider integration;
- direct semantic source persistence;
- exact/native sqlite-vec search;
- relational filters;
- compact results/hydration;
- Smart Search basic routing;
- integration tests.

At the end of this phase the library should already be genuinely useful for a personal wiki.

### Phase 3 — embedding profiles/generation/rebuild/sync

- generation state machine;
- profile mismatch detection;
- explicit dimensional reduction;
- Rebuildable/Authoritative modes;
- `SyncAsync`;
- bounded ingestion;
- Reset;
- failure/restart tests.

### Phase 4 — PostgreSQL provider

- server schema/migrations;
- pgvector integration;
- exact search;
- HalfVector/Vector storage mapping;
- provider conformance tests;
- AOT smoke where applicable.

### Phase 5 — SQL Server provider

- SQL Server schema/migrations;
- VECTOR exact search;
- explicit dimension compatibility errors;
- provider conformance tests;
- AOT compatibility determined by actual smoke results.

### Phase 6 — export/import and native ABI

- streaming logical archive;
- provider migration tests;
- NativeAOT C facade;
- SQLite native bundle first;
- cross-platform C integration tests.

### Phase 7 — documentation/release hardening

- root README;
- docs set;
- best practices;
- samples;
- package validation;
- release branch trusted publishing.

### Future phases — intentionally not v1

- lexical/FTS hybrid search;
- optional neural reranker package integration;
- Prism contribution/evidence;
- evidence/excerpt response mode;
- auto-rerank policy;
- collection-content aggregate semantics;
- approximate-search tuning helpers;
- additional providers only when they preserve the project's simplicity.

---

## 48. Upstream dependency boundary

`OnnxTextEmbeddings.NET` is deliberately responsible for the semantic primitives already implemented there.

SemanticKnowledge should consume those abstractions from NuGet and benefit automatically from compatible upstream improvements.

SemanticKnowledge owns:

- organization;
- schemas;
- canonical knowledge persistence;
- relational filtering abstraction;
- provider storage schema;
- lifecycle/migrations;
- search orchestration;
- hydration/sync/export;
- future reranker/evidence orchestration.

`OnnxTextEmbeddings.NET` owns:

- local embedding inference;
- tokenizer/chunking;
- embedding serialization/vector math;
- direct chunk metadata;
- fingerprints;
- deterministic reduction;
- DefaultV1 scoring;
- database native candidate adapters;
- its own ONNX concurrency/model health.

If a feature naturally belongs in the semantic primitive/runtime layer and is generally useful beyond SemanticKnowledge, prefer improving the sibling package rather than implementing a private fork here.

---

## 49. Current upstream/provider constraints to keep documented

These facts are implementation-sensitive and should be verified again whenever dependency versions change:

- sqlite-vec currently supports native float32 and signed INT8 vector search; upstream remains pre-v1.
- pgvector exact search is the default behavior; approximate HNSW/IVFFlat is optional. Current indexed-dimension limits differ between `vector` and `halfvec`.
- SQL Server 2025 native VECTOR currently has a 1998-dimension maximum; float16 is preview-sensitive.
- NativeAOT shared-library exports require an explicit facade/ABI; referenced library methods do not magically become C exports.
- EF Core NativeAOT/precompiled-query support remains experimental, which is one reason SemanticKnowledge uses a small provider SQL layer instead.

Provider startup capability checks and CI tests are the ultimate source of truth for a shipped package version.

---

## 50. Definition of success

SemanticKnowledge.NET succeeds if a developer can install it and think primarily about **their knowledge**, not vector infrastructure.

A successful v1 should make all of the following feel ordinary:

- “Make a personal wiki store.”
- “Put these documents under NPCs.”
- “This field is semantic; this date is just filterable.”
- “Title matters 30%, Description 20%, Tags 20%, Biography 30%.”
- “Only search campaign entries after this date.”
- “I don't know which folder it is in; use Smart Search.”
- “Here is already-computed query embedding; don't embed it again.”
- “Use my remote embedding API instead.”
- “Use SQLite locally.”
- “Use PostgreSQL in my server deployment.”
- “Use SQL Server because that's what our .NET shop runs.”
- “The source wiki is authoritative; synchronize it.”
- “This store is disposable; rebuild it.”
- “Give me IDs and scores, not 30 giant documents.”
- “Give me only the most relevant content up to a token budget.”
- “Move this SQLite store to PostgreSQL via a logical export.”
- “Publish my application NativeAOT.”
- “Bind to the stable C ABI from another language if I want to.”

All while maintaining one central design principle:

> **Structured knowledge + relational filtering + semantic retrieval, with the complexity hidden behind an intentionally small, strongly validated API.**

---

## 51. Reference implementation sources

Implementation should re-check these sources rather than relying on stale copied assumptions:

- `magiccodingman/OnnxTextEmbeddings.NET` README and `docs/` — semantic primitives, database adapters, scoring, dimension reduction, NativeAOT/interop precedent.
- Microsoft SQL Server 2025 VECTOR / VECTOR_DISTANCE / vector-index documentation.
- pgvector official repository/documentation.
- sqlite-vec official repository/documentation.
- Microsoft .NET NativeAOT library/interop documentation.
- Microsoft EF Core NativeAOT/precompiled-query documentation.
- Prism-Reranker paper/model documentation when reranker/evidence work begins.
