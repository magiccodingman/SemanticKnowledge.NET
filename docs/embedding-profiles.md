# Embedding profiles and generations

A store currently has one active semantic coordinate space. Compatibility is identified by more than dimensions: the **embedding-space fingerprint is the mathematical authority**. Model identity/source revision, output dimensions, normalization metadata, reduction profile, and physical vector storage are descriptive/capability metadata around that identity.

Two vectors are not considered compatible merely because they both contain 1,024 values.

## Introspect the accepted space

Applications can enumerate the semantic spaces accepted by a configured store:

```csharp
var spaces = await services
    .GetRequiredService<IKnowledgeEmbeddingSpaceCatalog>()
    .GetEmbeddingSpacesAsync();

foreach (var space in spaces)
{
    Console.WriteLine(space.EmbeddingSpaceFingerprint);
    Console.WriteLine(space.ModelId);
    Console.WriteLine(space.Dimensions);
    Console.WriteLine(space.Normalization);
}
```

`KnowledgeEmbeddingSpaceDescriptor` includes:

- `EmbeddingSpaceFingerprint`;
- provider/model/source revision;
- native and effective dimensions;
- coordinate-space label;
- normalization when the embedding provider can state it authoritatively;
- dimensional-reduction profile when known;
- provider physical vector storage;
- queryable/lifecycle status;
- accepted query vector representations.

The API deliberately returns a collection. SemanticKnowledge currently exposes one active queryable space, but callers do not need an API redesign if a future store can safely expose several independent spaces.

Do not infer missing metadata. A custom provider that cannot authoritatively state normalization or reduction details may expose `Unknown`/`null`; the fingerprint still remains the compatibility authority.

## Precomputed query embeddings

`ISemanticKnowledgeStore` accepts a full `QueryEmbedding`, not a naked `float[]`. SemanticKnowledge validates both dimensions and `EmbeddingSpaceFingerprint` before retrieval. This allows callers to cache or obtain query embeddings elsewhere without weakening coordinate-space safety.

## Dimension reduction

`OutputDimensions` is optional. When omitted, the embedding provider's native dimensions are used. Reduction is **never** silently applied to satisfy a backend limit.

```csharp
services.AddSemanticKnowledge(options =>
{
    options.Embeddings.OutputDimensions = 1024; // explicit, potentially lossy
    options.Embeddings.Storage = VectorStoragePreference.Compact;
});
```

The built-in ONNX adapter reports the derived fingerprint and the deterministic reduction profile for the reduced child space.

## Generation rebuilds

When an existing database opens with a changed embedding profile, the provider creates a pending vector generation. SemanticKnowledge re-embeds canonical Collections/Documents into that generation and activates it only after rebuild completion. A failed rebuild leaves the previous active generation intact.

Storage mappings:

- SQLite Compact: native sqlite-vec INT8; MaximumPrecision: Float32.
- PostgreSQL Compact: pgvector `halfvec`; MaximumPrecision: `vector`.
- SQL Server: native float32 VECTOR. SQL Server's native vector limit is 1,998 dimensions; configure reduction explicitly when required.

The portable archive records the source embedding profile for diagnostics but omits vectors in v1. Import regenerates vectors in the destination's active profile.
