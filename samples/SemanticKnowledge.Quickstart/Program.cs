using Microsoft.Extensions.DependencyInjection;
using SemanticKnowledge;
using SemanticKnowledge.Sqlite;

var databasePath = args.Length > 0 ? args[0] : "quickstart.db";

var services = new ServiceCollection();
services
    .AddSemanticKnowledge(options =>
    {
        options.PersistenceMode = KnowledgePersistenceMode.Authoritative;
    })
    .UseSqlite(databasePath)
    .UseOnnxEmbeddings();

await using var provider = services.BuildServiceProvider();
var store = provider.GetRequiredService<ISemanticKnowledgeStore>();
await store.InitializeAsync();

var wiki = await store.GetOrCreateKnowledgeBaseAsync("Quickstart Wiki");
var schema = await store.EnsureBuiltInDocumentSchemaAsync();
var notes = await store.GetOrCreateCollectionAsync(wiki.Id, "Notes", defaultSchemaId: schema.Id);

await store.UpsertDocumentAsync(
    wiki.Id,
    notes.Id,
    title: "SQLite backup notes",
    body: "Use SQLite's online backup API before moving a live database.",
    tags: ["sqlite", "backup"],
    externalId: "quickstart/sqlite-backup");

var hits = await store.SearchAsync("how should I back up sqlite?", new KnowledgeSearchRequest
{
    KnowledgeBaseId = wiki.Id,
    Mode = KnowledgeSearchMode.Smart,
    Top = 5,
    Include = KnowledgeResultInclude.MatchedChunks
});

foreach (var hit in hits)
    Console.WriteLine($"{hit.Score:F3}  {hit.Title}");
