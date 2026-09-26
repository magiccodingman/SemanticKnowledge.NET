# Model-free lexical stores

Set `SemanticKnowledgeOptions.LexicalOnly = true` before registering SQLite or PostgreSQL:

```csharp
services.AddSemanticKnowledge(options => options.LexicalOnly = true)
    .UsePostgreSql(connectionString);
// Do not call UseOnnxEmbeddings().
```

This is an explicit store mode, not a disabled index. Canonical collections/documents,
atomic snapshots, lexical search, filters, logical archives and reset remain available.
No embedding provider is required. Normal operations do not request model information,
download/load an ONNX model, count model tokens, generate embeddings, or create a
vector generation. PostgreSQL does not require the vector extension. SQLite does not
load sqlite-vec in this mode.

Use `KnowledgeSearchQuery.Create(knowledgeBaseId, text).Lexical(...)`. Collection
routing via `Smart()` can accompany lexical retrieval. Explicit semantic/hybrid
retrieval and the legacy semantic string-query overload throw `NotSupportedException`;
they do not silently change a caller's requested search semantics. Applications may
choose lexical fallback themselves after inspecting capabilities. The embedding-space
catalog is empty, vector capabilities are false, and physical vector storage is
`none`. Lexical availability remains provider-reported.

SQL Server lexical-only storage is not currently supported and fails explicitly.
Existing semantic-mode configuration and behavior remain the default.

## Storage and transitions

Lexical metadata has no active vector generation (empty identifier/table/fingerprint
and zero dimensions). The provider SPI represents this explicitly with a null
`KnowledgeStorageInitialization.Embedding`; custom providers must handle or reject it.
No synthetic embedding identity or zero vector is substituted.

Opening an existing store with the other mode is rejected. Provision a separate store
and explicitly reindex authoritative source content, or export/import a logical archive.
Do not delete an authoritative store to change modes. Logical archives contain canonical
data, not vectors; model-free exports omit the optional informational
`embedding-profile.json`. Imports continue to rebuild according to the destination mode.

Regression tests cover model-API rejection, snapshot replacement/deletion, failed
publication preserving the previous revision, lexical search, archive round trips,
reopening and reset. PostgreSQL tests run without a vector extension as well as in
the normal provider integration suite.
