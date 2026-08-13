using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SemanticKnowledge.Sqlite;

namespace SemanticKnowledge.Sqlite.Tests;

public sealed class CatalogRoutingIndexTests
{
    [Fact]
    public async Task Catalog_collection_metadata_refreshes_semantic_and_lexical_routing_sources()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), $"semantic-knowledge-catalog-{Guid.NewGuid():N}.db");
        try
        {
            var services = new ServiceCollection();
            services.AddSemanticKnowledge(options => options.Embeddings.OutputDimensions = 4)
                .UseSqlite(path);
            services.AddSingleton<IKnowledgeEmbeddingProvider, TestEmbeddingProvider>();

            await using var provider = services.BuildServiceProvider();
            var store = provider.GetRequiredService<ISemanticKnowledgeStore>();
            var catalog = provider.GetRequiredService<IKnowledgeCatalog>();
            await store.InitializeAsync(cancellationToken);

            var kb = await store.GetOrCreateKnowledgeBaseAsync("Catalog Test", "catalog-test", cancellationToken);
            var collection = await store.GetOrCreateCollectionAsync(kb.Id, "Miscellaneous", externalId: "misc", cancellationToken: cancellationToken);

            var updated = await catalog.UpsertCollectionAsync(collection with
            {
                Description = "Operational recovery material.",
                Tags = ["routing-secret", "operations"]
            }, cancellationToken);

            Assert.Contains("routing-secret", updated.Tags);

            await using var connection = new SqliteConnection($"Data Source={path}");
            await connection.OpenAsync(cancellationToken);

            await using (var lexical = connection.CreateCommand())
            {
                lexical.CommandText = "SELECT text_value FROM sk_lexical_sources_fts WHERE item_id=$id AND entity_kind=$kind AND field_key=$field";
                lexical.Parameters.AddWithValue("$id", collection.Id.ToString("D"));
                lexical.Parameters.AddWithValue("$kind", (int)SemanticEntityKind.Collection);
                lexical.Parameters.AddWithValue("$field", KnowledgeSystemFields.Tags);
                var text = Assert.IsType<string>(await lexical.ExecuteScalarAsync(cancellationToken));
                Assert.Contains("routing-secret", text, StringComparison.Ordinal);
            }

            await using (var semantic = connection.CreateCommand())
            {
                semantic.CommandText = "SELECT COUNT(*) FROM sk_semantic_sources WHERE item_id=$id AND entity_kind=$kind AND field_key=$field";
                semantic.Parameters.AddWithValue("$id", collection.Id.ToString("D"));
                semantic.Parameters.AddWithValue("$kind", (int)SemanticEntityKind.Collection);
                semantic.Parameters.AddWithValue("$field", KnowledgeSystemFields.Tags);
                Assert.True(Convert.ToInt32(await semantic.ExecuteScalarAsync(cancellationToken)) > 0);
            }
        }
        finally
        {
            try { File.Delete(path); } catch { }
            try { File.Delete(path + "-wal"); } catch { }
            try { File.Delete(path + "-shm"); } catch { }
        }
    }
}
