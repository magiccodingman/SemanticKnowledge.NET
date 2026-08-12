using Microsoft.Extensions.DependencyInjection;
using SemanticKnowledge.Sqlite;

namespace SemanticKnowledge.Sqlite.Tests;

public sealed class SnapshotResetTests
{
    [Fact]
    public async Task Reset_removes_snapshot_state_before_recreating_store()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), $"semantic-knowledge-snapshot-reset-{Guid.NewGuid():N}.db");
        try
        {
            var services = new ServiceCollection();
            services.AddSemanticKnowledge(options => options.Embeddings.OutputDimensions = 4).UseSqlite(path);
            services.AddSingleton<IKnowledgeEmbeddingProvider>(new TestEmbeddingProvider());
            await using var provider = services.BuildServiceProvider();
            var store = provider.GetRequiredService<ISemanticKnowledgeStore>();
            var sync = provider.GetRequiredService<IKnowledgeSynchronizationService>();
            await store.InitializeAsync(cancellationToken);
            var kb = await store.GetOrCreateKnowledgeBaseAsync("Reset Snapshot", cancellationToken: cancellationToken);
            var collection = await store.GetOrCreateCollectionAsync(kb.Id, "Pages", cancellationToken: cancellationToken);
            var schema = new KnowledgeSchemaBuilder("reset-snapshot")
                .SetSemanticWeight(KnowledgeSystemFields.Title, 40)
                .SetSemanticWeight(KnowledgeSystemFields.Description, 20)
                .SetSemanticWeight(KnowledgeSystemFields.Tags, 10)
                .Text(KnowledgeSystemFields.Body, 30, SemanticMode.Chunked)
                .Build();
            await store.EnsureSchemaAsync(schema, cancellationToken);

            await sync.SyncCollectionSnapshotAsync(kb.Id, collection.Id, schema.Id, "before-reset", Stream(), cancellationToken: cancellationToken);
            Assert.NotNull(await sync.GetCollectionSnapshotStateAsync(collection.Id, cancellationToken));

            await store.ResetAsync(cancellationToken);
            await store.InitializeAsync(cancellationToken);
            Assert.Null(await sync.GetCollectionSnapshotStateAsync(collection.Id, cancellationToken));
            _ = await store.GetOrCreateKnowledgeBaseAsync("After Reset", cancellationToken: cancellationToken);
        }
        finally
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" }) try { File.Delete(path + suffix); } catch { }
        }
    }

    private static async IAsyncEnumerable<ExternalKnowledgeDocumentInput> Stream()
    {
        yield return new ExternalKnowledgeDocumentInput
        {
            ExternalId = "one",
            Title = "Alpha",
            Values = new Dictionary<string, KnowledgeValue> { [KnowledgeSystemFields.Body] = KnowledgeValue.From("alpha body") }
        };
        await Task.Yield();
    }
}
