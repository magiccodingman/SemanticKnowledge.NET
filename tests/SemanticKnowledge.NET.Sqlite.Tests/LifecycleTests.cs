using Microsoft.Extensions.DependencyInjection;
using OnnxTextEmbeddings;
using SemanticKnowledge.Sqlite;

namespace SemanticKnowledge.Sqlite.Tests;

public sealed class LifecycleTests
{
    [Fact]
    public async Task Authoritative_store_runs_registered_logical_migration_and_rebuilds_changed_semantics()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var path = TempPath();
        try
        {
            Guid documentId;
            await using (var provider = Build(path, databaseVersion: 1, KnowledgePersistenceMode.Authoritative, "test-space-v1"))
            {
                var store = provider.GetRequiredService<ISemanticKnowledgeStore>();
                await store.InitializeAsync(cancellationToken);
                var kb = await store.GetOrCreateKnowledgeBaseAsync("Migration Wiki", cancellationToken: cancellationToken);
                var collection = await store.GetOrCreateCollectionAsync(kb.Id, "Notes", cancellationToken: cancellationToken);
                var schema = Schema();
                await store.EnsureSchemaAsync(schema, cancellationToken);
                documentId = await store.UpsertDocumentAsync(new KnowledgeDocumentInput
                {
                    KnowledgeBaseId = kb.Id,
                    CollectionId = collection.Id,
                    SchemaId = schema.Id,
                    Title = "Migrating note",
                    Values = new Dictionary<string, KnowledgeValue>
                    {
                        [KnowledgeSystemFields.Body] = KnowledgeValue.From("alpha old content"),
                        ["revision"] = KnowledgeValue.From(1L)
                    }
                }, cancellationToken);
            }

            var services = new ServiceCollection();
            services.AddSemanticKnowledge(options =>
                {
                    options.DatabaseVersion = 2;
                    options.PersistenceMode = KnowledgePersistenceMode.Authoritative;
                    options.Embeddings.OutputDimensions = 4;
                })
                .Migrate(1, 2, async (context, ct) =>
                {
                    var document = Assert.Single(await context.GetDocumentsAsync(cancellationToken: ct));
                    var values = document.Values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
                    values[KnowledgeSystemFields.Body] = KnowledgeValue.From("beta migrated content");
                    values["revision"] = KnowledgeValue.From(2L);
                    await context.UpsertDocumentAsync(document with { Values = values }, ct);
                })
                .UseSqlite(path);
            services.AddSingleton<IKnowledgeEmbeddingProvider>(new TestEmbeddingProvider());

            await using (var provider = services.BuildServiceProvider())
            {
                var store = provider.GetRequiredService<ISemanticKnowledgeStore>();
                await store.InitializeAsync(cancellationToken);
                var migrated = await store.GetDocumentAsync(documentId, cancellationToken);
                Assert.NotNull(migrated);
                Assert.Equal(2L, migrated.Values["revision"].Int64);
                Assert.Equal("beta migrated content", migrated.Values[KnowledgeSystemFields.Body].Text);

                var results = await store.SearchAsync("beta", new KnowledgeSearchRequest
                {
                    KnowledgeBaseId = migrated.KnowledgeBaseId,
                    Top = 3
                }, cancellationToken);
                Assert.NotEmpty(results);
                Assert.Equal(documentId, results[0].DocumentId);
            }

            await using (var provider = Build(path, databaseVersion: 2, KnowledgePersistenceMode.Authoritative, "test-space-v1"))
                _ = await provider.GetRequiredService<ISemanticKnowledgeStore>().InitializeAsync(cancellationToken);
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task Authoritative_store_fails_closed_when_logical_migration_is_missing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var path = TempPath();
        try
        {
            await using (var provider = Build(path, 1, KnowledgePersistenceMode.Authoritative, "test-space-v1"))
                _ = await provider.GetRequiredService<ISemanticKnowledgeStore>().InitializeAsync(cancellationToken);

            await using var upgraded = Build(path, 2, KnowledgePersistenceMode.Authoritative, "test-space-v1");
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => upgraded.GetRequiredService<ISemanticKnowledgeStore>().InitializeAsync(cancellationToken));
            Assert.Contains("no migration", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task Rebuildable_store_resets_canonical_data_on_logical_version_change()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var path = TempPath();
        try
        {
            Guid documentId;
            await using (var provider = Build(path, 1, KnowledgePersistenceMode.Rebuildable, "test-space-v1"))
            {
                var store = provider.GetRequiredService<ISemanticKnowledgeStore>();
                await store.InitializeAsync(cancellationToken);
                var kb = await store.GetOrCreateKnowledgeBaseAsync("Rebuildable", cancellationToken: cancellationToken);
                var collection = await store.GetOrCreateCollectionAsync(kb.Id, "Notes", cancellationToken: cancellationToken);
                var schema = Schema();
                await store.EnsureSchemaAsync(schema, cancellationToken);
                documentId = await store.UpsertDocumentAsync(new KnowledgeDocumentInput
                {
                    KnowledgeBaseId = kb.Id,
                    CollectionId = collection.Id,
                    SchemaId = schema.Id,
                    Title = "Disposable",
                    Values = new Dictionary<string, KnowledgeValue>
                    {
                        [KnowledgeSystemFields.Body] = KnowledgeValue.From("alpha disposable"),
                        ["revision"] = KnowledgeValue.From(1L)
                    }
                }, cancellationToken);
            }

            await using var upgraded = Build(path, 2, KnowledgePersistenceMode.Rebuildable, "test-space-v1");
            var store2 = upgraded.GetRequiredService<ISemanticKnowledgeStore>();
            await store2.InitializeAsync(cancellationToken);
            Assert.Null(await store2.GetDocumentAsync(documentId, cancellationToken));
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task Embedding_profile_change_rebuilds_generation_and_keeps_canonical_data_searchable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var path = TempPath();
        try
        {
            Guid kbId;
            Guid documentId;
            await using (var provider = Build(path, 1, KnowledgePersistenceMode.Authoritative, "test-space-v1"))
            {
                var store = provider.GetRequiredService<ISemanticKnowledgeStore>();
                await store.InitializeAsync(cancellationToken);
                var kb = await store.GetOrCreateKnowledgeBaseAsync("Embeddings", cancellationToken: cancellationToken);
                kbId = kb.Id;
                var collection = await store.GetOrCreateCollectionAsync(kb.Id, "Notes", cancellationToken: cancellationToken);
                var schema = Schema();
                await store.EnsureSchemaAsync(schema, cancellationToken);
                documentId = await store.UpsertDocumentAsync(new KnowledgeDocumentInput
                {
                    KnowledgeBaseId = kb.Id,
                    CollectionId = collection.Id,
                    SchemaId = schema.Id,
                    Title = "Alpha backup",
                    Values = new Dictionary<string, KnowledgeValue>
                    {
                        [KnowledgeSystemFields.Body] = KnowledgeValue.From("alpha backup content"),
                        ["revision"] = KnowledgeValue.From(1L)
                    }
                }, cancellationToken);
            }

            var services = new ServiceCollection();
            services.AddSemanticKnowledge(options =>
                {
                    options.DatabaseVersion = 1;
                    options.PersistenceMode = KnowledgePersistenceMode.Authoritative;
                    options.Embeddings.OutputDimensions = 4;
                    options.Embeddings.Storage = VectorStoragePreference.MaximumPrecision;
                })
                .UseSqlite(path);
            services.AddSingleton<IKnowledgeEmbeddingProvider>(new TestEmbeddingProvider("test-space-v2"));

            await using var rebuilt = services.BuildServiceProvider();
            var store2 = rebuilt.GetRequiredService<ISemanticKnowledgeStore>();
            var capabilities = await store2.InitializeAsync(cancellationToken);
            Assert.Equal("FP32", capabilities.PhysicalVectorStorage);
            Assert.False(capabilities.RequiresEmbeddingRebuild);
            Assert.NotNull(await store2.GetDocumentAsync(documentId, cancellationToken));
            var results = await store2.SearchAsync("alpha", new KnowledgeSearchRequest { KnowledgeBaseId = kbId, Top = 3 }, cancellationToken);
            Assert.NotEmpty(results);
            Assert.Equal(documentId, results[0].DocumentId);
        }
        finally { Delete(path); }
    }

    private static ServiceProvider Build(string path, int databaseVersion, KnowledgePersistenceMode mode, string fingerprint)
    {
        var services = new ServiceCollection();
        services.AddSemanticKnowledge(options =>
            {
                options.DatabaseVersion = databaseVersion;
                options.PersistenceMode = mode;
                options.Embeddings.OutputDimensions = 4;
            })
            .UseSqlite(path);
        services.AddSingleton<IKnowledgeEmbeddingProvider>(new TestEmbeddingProvider(fingerprint));
        return services.BuildServiceProvider();
    }

    private static KnowledgeSchemaDefinition Schema() => new KnowledgeSchemaBuilder("lifecycle-article")
        .SetSemanticWeight(KnowledgeSystemFields.Title, 30)
        .SetSemanticWeight(KnowledgeSystemFields.Description, 20)
        .SetSemanticWeight(KnowledgeSystemFields.Tags, 20)
        .Text(KnowledgeSystemFields.Body, 30, SemanticMode.Chunked)
        .Int64("revision")
        .Build();

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"semantic-knowledge-lifecycle-{Guid.NewGuid():N}.db");
    private static void Delete(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            try { File.Delete(path + suffix); } catch { }
    }
}
