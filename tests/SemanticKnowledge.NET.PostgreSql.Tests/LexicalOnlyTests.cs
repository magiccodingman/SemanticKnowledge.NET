using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SemanticKnowledge.PostgreSql;

namespace SemanticKnowledge.PostgreSql.Tests;

public sealed class LexicalOnlyTests
{
    [Fact]
    public async Task Model_free_snapshot_and_search_work_without_vector_tables()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = Environment.GetEnvironmentVariable("SEMANTIC_KNOWLEDGE_POSTGRES");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        var schemaName = $"sk_lexical_{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddSemanticKnowledge(o => o.LexicalOnly = true)
            .UsePostgreSql(o => { o.ConnectionString = connectionString!; o.Schema = schemaName; });
        await using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<ISemanticKnowledgeStore>();
        try
        {
            var capabilities = await store.InitializeAsync(ct);
            Assert.True(capabilities.LexicalSearchSupported);
            Assert.False(capabilities.ExactVectorSearch);
            Assert.Empty(await provider.GetRequiredService<IKnowledgeEmbeddingSpaceCatalog>().GetEmbeddingSpacesAsync(ct));
            var kb = await store.GetOrCreateKnowledgeBaseAsync("Model free", cancellationToken: ct);
            var collection = await store.GetOrCreateCollectionAsync(kb.Id, "Pages", cancellationToken: ct);
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
            Assert.Single(await store.SearchAsync(KnowledgeSearchQuery.Create(kb.Id, "replacement").Lexical(KnowledgeSearchField.Title()).Smart(), ct));
            Assert.Empty(await store.SearchAsync(KnowledgeSearchQuery.Create(kb.Id, "removed").Lexical(KnowledgeSearchField.Title()), ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() => sync.SyncCollectionSnapshotAsync(
                kb.Id, collection.Id, schema.Id, "three", FailingPages(), cancellationToken: ct));
            Assert.Equal("two", (await sync.GetCollectionSnapshotStateAsync(collection.Id, ct))!.SourceRevision);
            Assert.Single(await store.SearchAsync(KnowledgeSearchQuery.Create(kb.Id, "replacement").Lexical(KnowledgeSearchField.Title()), ct));
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(ct);
            await using var command = new NpgsqlCommand("SELECT count(*) FROM information_schema.tables WHERE table_schema=@schema AND table_name LIKE 'sk_vectors_%'", connection);
            command.Parameters.AddWithValue("schema", schemaName);
            Assert.Equal(0L, await command.ExecuteScalarAsync(ct));
            using var archive = new MemoryStream();
            var archives = provider.GetRequiredService<IKnowledgeArchiveService>();
            Assert.Equal(1, (await archives.ExportKnowledgeBaseAsync(kb.Id, archive, ct)).DocumentCount);
            archive.Position = 0;
            await archives.ImportAsync(archive, ct);
        }
        finally { await store.ResetAsync(ct); }
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

    private static async IAsyncEnumerable<ExternalKnowledgeDocumentInput> FailingPages()
    {
        yield return new ExternalKnowledgeDocumentInput { ExternalId = "a", Title = "Unpublished",
            Values = new Dictionary<string, KnowledgeValue> { [KnowledgeSystemFields.Body] = KnowledgeValue.From("Unpublished") } };
        await Task.Yield();
        throw new InvalidOperationException("Injected staging failure.");
    }
}
