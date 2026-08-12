# Embedding profiles and generations

A store has one active semantic space. Compatibility is identified by more than dimensions: model identity/source revision, embedding-space fingerprint, output dimensions, and physical vector storage all matter.

`OutputDimensions` is optional. When omitted, the embedding provider's native dimensions are used. Reduction is **never** silently applied to satisfy a backend limit.

```csharp
services.AddSemanticKnowledge(options =>
{
    options.Embeddings.OutputDimensions = 1024; // explicit, potentially lossy
    options.Embeddings.Storage = VectorStoragePreference.Compact;
});
```

When an existing database opens with a changed embedding profile, the provider creates a pending vector generation. SemanticKnowledge re-embeds canonical Collections/Documents into that generation and activates it only after rebuild completion. A failed rebuild leaves the previous active generation intact.

Storage mappings:

- SQLite Compact: native sqlite-vec INT8; MaximumPrecision: Float32.
- PostgreSQL Compact: pgvector `halfvec`; MaximumPrecision: `vector`.
- SQL Server: native float32 VECTOR. SQL Server's native vector limit is 1,998 dimensions; configure reduction explicitly when required.

The portable archive records the source embedding profile for diagnostics but omits vectors in v1. Import regenerates vectors in the destination's active profile.
