# Best practices

Prefer SQLite unless you already need a server database, multi-process concurrency, centralized operations, or much larger shared datasets. The provider abstraction is for deployment fit, not a reason to start with infrastructure you do not need.

Keep one embedding space per store. Do not use different models/dimensions per Collection if you expect Global or Smart Search to work coherently.

Use `ExternalId` for synchronization identity and GUIDs for stable internal identity. Let source hashes skip unchanged re-embedding.

Use Chunked semantic fields for long prose. Keep structured numeric/date/boolean facts as typed nonsemantic fields and filter them relationally.

Put meaningful Description/Tags on Collections when using Smart Search; hierarchy is part of the retrieval system, not merely UI organization.

Do not silently reduce dimensions. If a backend limit forces reduction, configure it explicitly and treat the reduced output as a distinct embedding space.

Use `MatchedChunks` or `IKnowledgeContentSearch` for RAG evidence instead of loading entire documents. Use database-native backups for operations and logical archives for portability.

Choose `Authoritative` when the database is the source of truth. Choose `Rebuildable` only when a complete external source can recreate it.
