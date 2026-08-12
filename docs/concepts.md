# Concepts

The hierarchy is:

```text
Store
└── KnowledgeBase
    └── Collection
        ├── Collection
        └── Document
```

A **KnowledgeBase** is an independent knowledge space. A **Collection** is a recursive organizational and semantic-routing node. A **Document** is canonical application data governed by a logical **Schema**.

SemanticKnowledge keeps canonical data and derived semantic data separate. Titles, descriptions, tags, typed field values, external IDs, and source text are canonical. Embeddings and vector indexes are derived and may be rebuilt.

Schemas are data, not physical tables. Adding a logical field does not run `ALTER TABLE`; the engine uses a stable relational schema plus typed EAV values. This keeps runtime schemas portable across SQLite, PostgreSQL, and SQL Server.

Every semantic field can be `None`, `Whole`, or `Chunked`. Chunking and embedding behavior are delegated to the configured embedding provider. Query text always becomes one query embedding.

The store has one active embedding space at a time. Model/fingerprint/dimension/storage changes create a new vector generation. Canonical data remains the source of truth while the new generation is built and atomically activated.

`ExternalId` is intended for synchronization with authoritative outside sources. Internal GUIDs remain stable store identities and are preserved by logical archives.

Relational filtering and vector retrieval happen in the database provider. SemanticKnowledge does not load the corpus into application memory to perform filtering or cosine search.
