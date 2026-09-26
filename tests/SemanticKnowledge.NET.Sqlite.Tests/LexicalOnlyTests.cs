using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using OnnxTextEmbeddings;
using SemanticKnowledge.Sqlite;

namespace SemanticKnowledge.Sqlite.Tests;

public sealed class LexicalOnlyTests
{
    [Fact]
    public async Task Canonical_snapshot_search_archive_and_reset_never_call_the_model()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), $"lexical-only-{Guid.NewGuid():N}.db");
        var bomb = new ForbiddenEmbeddings();
        try
        {
            await using var provider = Build(path, bomb);
            var store = provider.GetRequiredService<ISemanticKnowledgeStore>();
            var capabilities = await store.InitializeAsync(ct);
            Assert.True(capabilities.LexicalSearchSupported);
            Assert.False(capabilities.ExactVectorSearch);
            Assert.Equal("none", capabilities.PhysicalVectorStorage);
            Assert.Empty(await provider.GetRequiredService<IKnowledgeEmbeddingSpaceCatalog>().GetEmbeddingSpacesAsync(ct));
            var kb = await store.GetOrCreateKnowledgeBaseAsync("Wiki", cancellationToken: ct);
            var collection = await store.GetOrCreateCollectionAsync(kb.Id, "Guide", cancellationToken: ct);
            var schema = new KnowledgeSchemaBuilder("page")
                .SetSemanticWeight(KnowledgeSystemFields.Title, 30)
                .SetSemanticWeight(KnowledgeSystemFields.Description, 20)
                .SetSemanticWeight(KnowledgeSystemFields.Tags, 20)
                .Text(KnowledgeSystemFields.Body, 30, SemanticMode.Chunked).Build();
            await store.EnsureSchemaAsync(schema, ct);
            var sync = provider.GetRequiredService<IKnowledgeSynchronizationService>();
            await sync.SyncCollectionSnapshotAsync(kb.Id, collection.Id, schema.Id, "one",
                Pages(("a", "Original page"), ("b", "Removed page")), cancellationToken: ct);
            await sync.SyncCollectionSnapshotAsync(kb.Id, collection.Id, schema.Id, "two",
                Pages(("a", "Replacement page")), cancellationToken: ct);
            var hits = await store.SearchAsync(KnowledgeSearchQuery.Create(kb.Id, "replacement").Lexical(KnowledgeSearchField.Title()), ct);
            Assert.Equal("Replacement page", Assert.Single(hits).Title);
            Assert.Empty(await store.SearchAsync(KnowledgeSearchQuery.Create(kb.Id, "removed").Lexical(KnowledgeSearchField.Title()), ct));
            await Assert.ThrowsAsync<NotSupportedException>(() => store.SearchAsync(KnowledgeSearchQuery.Create(kb.Id, "replacement").Hybrid(), ct));
            using var archive = new MemoryStream();
            var result = await provider.GetRequiredService<IKnowledgeArchiveService>().ExportKnowledgeBaseAsync(kb.Id, archive, ct);
            Assert.Equal(1, result.DocumentCount);
            archive.Position = 0;
            await provider.GetRequiredService<IKnowledgeArchiveService>().ImportAsync(archive, ct);
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync(ct);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT count(*) FROM sqlite_master WHERE name LIKE 'sk_vectors_%'";
                Assert.Equal(0L, await command.ExecuteScalarAsync(ct));
            }
            await using (var reopened = Build(path, bomb))
                Assert.Single(await reopened.GetRequiredService<ISemanticKnowledgeStore>().SearchAsync(
                    KnowledgeSearchQuery.Create(kb.Id, "replacement").Lexical(KnowledgeSearchField.Title()), ct));
            await store.DeleteDocumentAsync(hits[0].DocumentId, ct);
            await store.ResetAsync(ct);
            Assert.Equal(0, bomb.Calls);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }

    [Fact]
    public async Task Model_free_registration_does_not_require_an_embedding_provider()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lexical-registration-{Guid.NewGuid():N}.db");
        try
        {
            await using var provider = Build(path);
            Assert.True((await provider.GetRequiredService<ISemanticKnowledgeStore>()
                .InitializeAsync(TestContext.Current.CancellationToken)).LexicalSearchSupported);
            var services = new ServiceCollection();
            Assert.Throws<InvalidOperationException>(() => services.AddSemanticKnowledge(o => o.LexicalOnly = true).UseOnnxEmbeddings());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }

    private static ServiceProvider Build(string path, ForbiddenEmbeddings? bomb = null)
    {
        var services = new ServiceCollection();
        services.AddSemanticKnowledge(o => o.LexicalOnly = true).UseSqlite(path);
        if (bomb is not null) services.AddSingleton<IKnowledgeEmbeddingProvider>(bomb);
        return services.BuildServiceProvider();
    }

    private static async IAsyncEnumerable<ExternalKnowledgeDocumentInput> Pages(params (string Id, string Title)[] pages)
    {
        foreach (var page in pages)
        {
            yield return new ExternalKnowledgeDocumentInput { ExternalId = page.Id, Title = page.Title,
                Values = new Dictionary<string, KnowledgeValue> { [KnowledgeSystemFields.Body] = KnowledgeValue.From(page.Title) } };
            await Task.Yield();
        }
    }

    private sealed class ForbiddenEmbeddings : IKnowledgeEmbeddingProvider
    {
        public int Calls { get; private set; }
        private Exception Fail() { Calls++; return new InvalidOperationException("Model API must not be invoked."); }
        public Task<KnowledgeEmbeddingProviderInfo> GetInfoAsync(CancellationToken cancellationToken = default) => throw Fail();
        public Task<QueryEmbedding> EmbedQueryAsync(string text, CancellationToken cancellationToken = default) => throw Fail();
        public Task<IReadOnlyList<TextEmbedding>> EmbedDocumentAsync(string text, CancellationToken cancellationToken = default) => throw Fail();
        public Task<int?> TryCountTokensAsync(string text, CancellationToken cancellationToken = default) => throw Fail();
    }
}
