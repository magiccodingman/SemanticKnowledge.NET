using Microsoft.Extensions.DependencyInjection;
using SemanticKnowledge.Sqlite;

namespace SemanticKnowledge.Sqlite.Tests;

public sealed class SnapshotRecoveryTests
{
    [Fact]
    public async Task Stale_staging_can_be_guardedly_discarded_without_touching_active_state()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), $"semantic-knowledge-snapshot-recovery-{Guid.NewGuid():N}.db");
        try
        {
            var services = new ServiceCollection();
            services.AddSemanticKnowledge(options => options.Embeddings.OutputDimensions = 4).UseSqlite(path);
            services.AddSingleton<IKnowledgeEmbeddingProvider>(new TestEmbeddingProvider());
            await using var root = services.BuildServiceProvider();
            var store = root.GetRequiredService<ISemanticKnowledgeStore>();
            await store.InitializeAsync(cancellationToken);
            var kb = await store.GetOrCreateKnowledgeBaseAsync("Recovery", cancellationToken: cancellationToken);
            var collection = await store.GetOrCreateCollectionAsync(kb.Id, "Pages", cancellationToken: cancellationToken);
            var schema = new KnowledgeSchemaBuilder("recovery")
                .SetSemanticWeight(KnowledgeSystemFields.Title, 40)
                .SetSemanticWeight(KnowledgeSystemFields.Description, 20)
                .SetSemanticWeight(KnowledgeSystemFields.Tags, 10)
                .Text(KnowledgeSystemFields.Body, 30, SemanticMode.Chunked)
                .Build();
            await store.EnsureSchemaAsync(schema, cancellationToken);

            var provider = root.GetRequiredService<IKnowledgeCollectionSnapshotProvider>();
            var manager = root.GetRequiredService<IKnowledgeCollectionSnapshotManager>();
            var handle = await provider.BeginCollectionSnapshotAsync(kb.Id, collection.Id, schema.Id, "abandoned-revision", cancellationToken);

            var staging = await manager.GetStateAsync(collection.Id, cancellationToken);
            Assert.NotNull(staging);
            Assert.Equal(handle.SnapshotId, staging.StagingSnapshotId);
            Assert.Equal("abandoned-revision", staging.StagingSourceRevision);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                manager.DiscardStagedAsync(collection.Id, Guid.NewGuid(), cancellationToken));
            Assert.Equal(handle.SnapshotId, (await manager.GetStateAsync(collection.Id, cancellationToken))?.StagingSnapshotId);

            await manager.DiscardStagedAsync(collection.Id, handle.SnapshotId, cancellationToken);
            var recovered = await manager.GetStateAsync(collection.Id, cancellationToken);
            Assert.NotNull(recovered);
            Assert.Null(recovered.StagingSnapshotId);
            Assert.Null(recovered.StagingSourceRevision);
            Assert.Null(recovered.ActiveSnapshotId);

            var next = await provider.BeginCollectionSnapshotAsync(kb.Id, collection.Id, schema.Id, "next-revision", cancellationToken);
            Assert.NotEqual(handle.SnapshotId, next.SnapshotId);
            await provider.AbortCollectionSnapshotAsync(next, cancellationToken);
        }
        finally
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" }) try { File.Delete(path + suffix); } catch { }
        }
    }
}
