using Microsoft.Extensions.DependencyInjection;
using SemanticKnowledge.Sqlite;

namespace SemanticKnowledge.Sqlite.Tests;

public sealed class WorkflowTests
{
    [Fact]
    public async Task Synchronization_skips_unchanged_updates_changed_and_deletes_missing_documents()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var path = TempPath();
        try
        {
            await using var provider = Build(path);
            var store = provider.GetRequiredService<ISemanticKnowledgeStore>();
            var sync = provider.GetRequiredService<IKnowledgeSynchronizationService>();
            await store.InitializeAsync(cancellationToken);
            var kb = await store.GetOrCreateKnowledgeBaseAsync("Sync Wiki", cancellationToken: cancellationToken);
            var collection = await store.GetOrCreateCollectionAsync(kb.Id, "Pages", cancellationToken: cancellationToken);
            var schema = Schema();
            await store.EnsureSchemaAsync(schema, cancellationToken);

            var first = await sync.SyncCollectionAsync(
                kb.Id,
                collection.Id,
                schema.Id,
                Stream(
                    Page("page-a", "Alpha page", "alpha original content", 1),
                    Page("page-b", "Beta page", "beta removable content", 1)),
                cancellationToken: cancellationToken);
            Assert.Equal(2, first.Inserted);
            Assert.Equal(0, first.Updated);
            Assert.Equal(0, first.Unchanged);
            Assert.Equal(0, first.Deleted);

            var second = await sync.SyncCollectionAsync(
                kb.Id,
                collection.Id,
                schema.Id,
                Stream(
                    Page("page-a", "Alpha page", "alpha original content", 1),
                    Page("page-b", "Beta page", "beta removable content", 1)),
                cancellationToken: cancellationToken);
            Assert.Equal(0, second.Inserted);
            Assert.Equal(0, second.Updated);
            Assert.Equal(2, second.Unchanged);
            Assert.Equal(0, second.Deleted);

            var third = await sync.SyncCollectionAsync(
                kb.Id,
                collection.Id,
                schema.Id,
                Stream(Page("page-a", "Alpha page updated", "alpha gamma changed content", 2)),
                cancellationToken: cancellationToken);
            Assert.Equal(0, third.Inserted);
            Assert.Equal(1, third.Updated);
            Assert.Equal(0, third.Unchanged);
            Assert.Equal(1, third.Deleted);

            var alpha = await store.SearchAsync("alpha", new KnowledgeSearchRequest
            {
                KnowledgeBaseId = kb.Id,
                Top = 5,
                Include = KnowledgeResultInclude.MatchedChunks
            }, cancellationToken);
            Assert.Contains(alpha, hit => hit.Title == "Alpha page updated");
            Assert.DoesNotContain(alpha, hit => hit.Title == "Beta page");
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task Synchronization_rejects_duplicate_external_ids_in_one_stream()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var path = TempPath();
        try
        {
            await using var provider = Build(path);
            var store = provider.GetRequiredService<ISemanticKnowledgeStore>();
            var sync = provider.GetRequiredService<IKnowledgeSynchronizationService>();
            await store.InitializeAsync(cancellationToken);
            var kb = await store.GetOrCreateKnowledgeBaseAsync("Sync Wiki", cancellationToken: cancellationToken);
            var collection = await store.GetOrCreateCollectionAsync(kb.Id, "Pages", cancellationToken: cancellationToken);
            var schema = Schema();
            await store.EnsureSchemaAsync(schema, cancellationToken);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => sync.SyncCollectionAsync(
                kb.Id,
                collection.Id,
                schema.Id,
                Stream(
                    Page("same", "First", "alpha first", 1),
                    Page("same", "Second", "alpha second", 2)),
                cancellationToken: cancellationToken));
            Assert.Contains("duplicate ExternalId", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task Content_search_returns_deduplicated_evidence_within_token_budget()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var path = TempPath();
        try
        {
            await using var provider = Build(path);
            var store = provider.GetRequiredService<ISemanticKnowledgeStore>();
            var content = provider.GetRequiredService<IKnowledgeContentSearch>();
            await store.InitializeAsync(cancellationToken);
            var kb = await store.GetOrCreateKnowledgeBaseAsync("Content Wiki", cancellationToken: cancellationToken);
            var collection = await store.GetOrCreateCollectionAsync(kb.Id, "Notes", cancellationToken: cancellationToken);
            var schema = Schema();
            await store.EnsureSchemaAsync(schema, cancellationToken);

            await store.UpsertDocumentAsync(new KnowledgeDocumentInput
            {
                KnowledgeBaseId = kb.Id,
                CollectionId = collection.Id,
                SchemaId = schema.Id,
                Title = "Alpha backup",
                Description = "alpha restore notes",
                Tags = ["alpha", "backup"],
                Values = new Dictionary<string, KnowledgeValue>
                {
                    [KnowledgeSystemFields.Body] = KnowledgeValue.From("alpha one two three four five"),
                    ["revision"] = KnowledgeValue.From(1L)
                }
            }, cancellationToken);

            const int tokenBudget = 64;
            var result = await content.SearchContentAsync(
                "alpha backup",
                new KnowledgeSearchRequest { KnowledgeBaseId = kb.Id, Top = 5 },
                maxContentTokens: tokenBudget,
                cancellationToken);

            Assert.NotEmpty(result.Hits);
            Assert.NotEmpty(result.Evidence);
            Assert.InRange(result.ApproximateTokenCount, 1, tokenBudget);
            Assert.Equal(result.ApproximateTokenCount, result.Evidence.Sum(item => item.TokenCount));
            Assert.Equal(
                result.Evidence.Count,
                result.Evidence.Select(item => (item.DocumentId, item.FieldKey, item.CharacterRange.Start, item.CharacterRange.Length)).Distinct().Count());
        }
        finally { Delete(path); }
    }

    private static ServiceProvider Build(string path)
    {
        var services = new ServiceCollection();
        services.AddSemanticKnowledge(options => options.Embeddings.OutputDimensions = 4)
            .UseSqlite(path);
        services.AddSingleton<IKnowledgeEmbeddingProvider>(new TestEmbeddingProvider());
        return services.BuildServiceProvider();
    }

    private static KnowledgeSchemaDefinition Schema() => new KnowledgeSchemaBuilder("workflow-article")
        .SetSemanticWeight(KnowledgeSystemFields.Title, 30)
        .SetSemanticWeight(KnowledgeSystemFields.Description, 20)
        .SetSemanticWeight(KnowledgeSystemFields.Tags, 20)
        .Text(KnowledgeSystemFields.Body, 30, SemanticMode.Chunked)
        .Int64("revision")
        .Build();

    private static ExternalKnowledgeDocumentInput Page(string externalId, string title, string body, long revision) => new()
    {
        ExternalId = externalId,
        Title = title,
        Values = new Dictionary<string, KnowledgeValue>
        {
            [KnowledgeSystemFields.Body] = KnowledgeValue.From(body),
            ["revision"] = KnowledgeValue.From(revision)
        }
    };

    private static async IAsyncEnumerable<ExternalKnowledgeDocumentInput> Stream(params ExternalKnowledgeDocumentInput[] documents)
    {
        foreach (var document in documents)
        {
            yield return document;
            await Task.Yield();
        }
    }

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"semantic-knowledge-workflow-{Guid.NewGuid():N}.db");
    private static void Delete(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            try { File.Delete(path + suffix); } catch { }
    }
}
