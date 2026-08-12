# Collections

Collections are recursive folders *and* semantic routing nodes. They can have a parent Collection, a default schema, external identity, title, description, and tags.

```csharp
var root = await store.GetOrCreateCollectionAsync(kb.Id, "World");
var npcs = await store.GetOrCreateCollectionAsync(
    kb.Id,
    "NPCs",
    parentCollectionId: root.Id,
    defaultSchemaId: npcSchema.Id);
```

For a path:

```csharp
var vallaki = await store.GetOrCreateCollectionPathAsync(
    kb.Id,
    ["World", "Locations", "Vallaki"],
    defaultSchemaId: locationSchema.Id);
```

The title-only methods are intentionally convenient. To set richer canonical KnowledgeBase or Collection metadata, use `IKnowledgeCatalog`:

```csharp
var catalog = services.GetRequiredService<IKnowledgeCatalog>();

await catalog.UpsertKnowledgeBaseAsync(kb with
{
    Description = "Campaign knowledge",
    Tags = ["campaign", "ravenloft"]
});

await catalog.UpsertCollectionAsync(npcs with
{
    Description = "Player and non-player characters encountered in the campaign.",
    Tags = ["characters", "npc"]
});
```

Updating Collection metadata automatically regenerates its routing embeddings. Smart Search can therefore route a query through Collection Title/Description/Tags before retrieving document candidates. KnowledgeBase tags are canonical metadata and are preserved by portable archives; they do not participate in Smart routing.
