# Schemas

Schemas describe logical document fields while the physical database schema stays stable.

Every schema contains protected system fields `title`, `description`, and `tags`. Custom fields can be Text, Int64, Decimal, Boolean, DateTimeOffset, or Guid. In v1 only Text fields may participate in semantic embedding.

```csharp
var schema = new KnowledgeSchemaBuilder("npc")
    .SetSemanticWeight(KnowledgeSystemFields.Title, 25)
    .SetSemanticWeight(KnowledgeSystemFields.Description, 20)
    .SetSemanticWeight(KnowledgeSystemFields.Tags, 15)
    .Text("biography", 30, SemanticMode.Chunked)
    .Text("personality", 10, SemanticMode.Whole)
    .Int64("level")
    .Boolean("alive")
    .Build();

await store.EnsureSchemaAsync(schema);
```

Semantic weights are percentages and must total exactly 100 across enabled semantic fields. SemanticKnowledge maps those percentages onto the underlying scorer so increasing one field does not require backend-specific scoring code.

`SemanticMode.Whole` requires the embedding provider to return one embedding. If the text is too large and the provider chunks it, SemanticKnowledge fails explicitly; use `Chunked` for long material.

A built-in wiki/document schema is available:

```csharp
var schema = await store.EnsureBuiltInDocumentSchemaAsync();
```

It contains Title, Description, Tags, and Body with a stable schema ID, making it appropriate for ordinary notes and wiki pages.

Strongly typed schema/filter helpers inspect expression trees instead of compiling delegates, preserving NativeAOT compatibility. Runtime schema builders remain available for dynamic applications.
