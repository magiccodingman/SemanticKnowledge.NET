using Microsoft.Extensions.DependencyInjection;
using SemanticKnowledge.Sqlite;

namespace SemanticKnowledge.Sqlite.Tests;

public sealed class SnapshotAndEmbeddingSpaceTests
{
    [Fact]
    public async Task Embedding_space_catalog_reports_queryable_coordinate_identity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var path = TempPath();
        try
        {
            await using var provider = Build(path);
            var catalog = provider.GetRequiredService<IKnowledgeEmbeddingSpaceCatalog>();
            var spaces = await catalog.GetEmbeddingSpacesAsync(cancellationToken);

            var space = Assert.Single(spaces);
            Assert.Equal("test-space-v1", space.EmbeddingSpaceFingerprint);
            Assert.Equal("semantic-knowledge-tests", space.ModelId);
            Assert.Equal("test-space-v1", space.SourceRevision);
            Assert.Equal(4, space.NativeDimensions);
            Assert.Equal(4, space.Dimensions);
            Assert.Equal("dense-test", space.CoordinateSpace);
            Assert.Equal(KnowledgeEmbeddingNormalization.Normalized, space.Normalization);
            Assert.Null(space.DimensionReductionProfile);
            Assert.Equal(KnowledgeEmbeddingSpaceStatus.ActiveQueryable, space.Status);
            Assert.True(space.Queryable);
            Assert.NotEmpty(space.SupportedQueryVectorFormats);
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task Atomic_snapshot_failure_keeps_previous_corpus_and_revision_visible()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var path = TempPath();
        try
        {
            await using var provider = Build(path);
            var store = provider.GetRequiredService<ISemanticKnowledgeStore>();
            var sync = provider.GetRequiredService<IKnowledgeSynchronizationService>();
            await store.InitializeAsync(cancellationToken);
            var kb = await store.GetOrCreateKnowledgeBaseAsync("Atomic Wiki", cancellationToken: cancellationToken);
            var collection = await store.GetOrCreateCollectionAsync(kb.Id, "Pages", cancellationToken: cancellationToken);
            var schema = Schema();
            await store.EnsureSchemaAsync(schema, cancellationToken);

            var first = await sync.SyncCollectionSnapshotAsync(
                kb.Id, collection.Id, schema.Id, "revision-1",
                Stream(Page("page-a", "Alpha old", "alpha old body", 1), Page("page-b", "Beta old", "beta old body", 1)),
                cancellationToken: cancellationToken);
            Assert.Equal(KnowledgeSyncPublication.AtomicSnapshot, first.Publication);
            Assert.NotNull(first.SnapshotId);

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => sync.SyncCollectionSnapshotAsync(
                kb.Id, collection.Id, schema.Id, "revision-2",
                FailingStream(Page("page-a", "Gamma replacement", "gamma staged but unpublished", 2)),
                cancellationToken: cancellationToken));
            Assert.Contains("synthetic staging failure", failure.Message, StringComparison.Ordinal);

            var state = await sync.GetCollectionSnapshotStateAsync(collection.Id, cancellationToken);
            Assert.NotNull(state);
            Assert.Equal("revision-1", state.SourceRevision);
            Assert.Null(state.StagingSnapshotId);

            var oldLexical = await store.SearchAsync(
                KnowledgeSearchQuery.Create(kb.Id, "Alpha old").Lexical(KnowledgeSearchField.Title()).Take(10),
                cancellationToken);
            Assert.Contains(oldLexical, hit => hit.Title == "Alpha old");

            var unpublishedLexical = await store.SearchAsync(
                KnowledgeSearchQuery.Create(kb.Id, "Gamma replacement").Lexical(KnowledgeSearchField.Title()).Take(10),
                cancellationToken);
            Assert.DoesNotContain(unpublishedLexical, hit => hit.Title == "Gamma replacement");

            var documents = await provider.GetRequiredService<IKnowledgeStorageProvider>().GetDocumentsAsync(kb.Id, cancellationToken);
            Assert.Contains(documents, document => document.Title == "Alpha old");
            Assert.Contains(documents, document => document.Title == "Beta old");
            Assert.DoesNotContain(documents, document => document.Title == "Gamma replacement");
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task Atomic_snapshot_publishes_reconciled_corpus_with_stable_ids_and_revision()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var path = TempPath();
        try
        {
            await using var provider = Build(path);
            var store = provider.GetRequiredService<ISemanticKnowledgeStore>();
            var sync = provider.GetRequiredService<IKnowledgeSynchronizationService>();
            await store.InitializeAsync(cancellationToken);
            var kb = await store.GetOrCreateKnowledgeBaseAsync("Atomic Wiki", cancellationToken: cancellationToken);
            var collection = await store.GetOrCreateCollectionAsync(kb.Id, "Pages", cancellationToken: cancellationToken);
            var schema = Schema();
            await store.EnsureSchemaAsync(schema, cancellationToken);

            await sync.SyncCollectionSnapshotAsync(
                kb.Id, collection.Id, schema.Id, "revision-1",
                Stream(Page("page-a", "Alpha old", "alpha old body", 1), Page("page-b", "Beta removed", "beta old body", 1)),
                cancellationToken: cancellationToken);

            var before = await provider.GetRequiredService<IKnowledgeStorageProvider>().GetDocumentsAsync(kb.Id, cancellationToken);
            var alphaId = before.Single(document => document.ExternalId == "page-a").Id;

            var result = await sync.SyncCollectionSnapshotAsync(
                kb.Id, collection.Id, schema.Id, "revision-2",
                Stream(Page("page-a", "Gamma updated", "gamma replacement body", 2), Page("page-c", "Alpha new", "alpha new body", 1)),
                cancellationToken: cancellationToken);

            Assert.Equal(1, result.Inserted);
            Assert.Equal(1, result.Updated);
            Assert.Equal(0, result.Unchanged);
            Assert.Equal(1, result.Deleted);
            Assert.Equal("revision-2", result.SourceRevision);
            Assert.NotNull(result.PublishedAt);

            var after = await provider.GetRequiredService<IKnowledgeStorageProvider>().GetDocumentsAsync(kb.Id, cancellationToken);
            Assert.Equal(alphaId, after.Single(document => document.ExternalId == "page-a").Id);
            Assert.DoesNotContain(after, document => document.ExternalId == "page-b");
            Assert.Contains(after, document => document.ExternalId == "page-c");

            var gamma = await store.SearchAsync(
                KnowledgeSearchQuery.Create(kb.Id, "Gamma updated").Hybrid().Take(10),
                cancellationToken);
            Assert.Contains(gamma, hit => hit.DocumentId == alphaId && hit.Title == "Gamma updated");

            var removed = await store.SearchAsync(
                KnowledgeSearchQuery.Create(kb.Id, "Beta removed").Lexical(KnowledgeSearchField.Title()).Take(10),
                cancellationToken);
            Assert.DoesNotContain(removed, hit => hit.Title == "Beta removed");

            var state = await sync.GetCollectionSnapshotStateAsync(collection.Id, cancellationToken);
            Assert.NotNull(state);
            Assert.Equal(result.SnapshotId, state.ActiveSnapshotId);
            Assert.Equal("revision-2", state.SourceRevision);
            Assert.Null(state.StagingSnapshotId);
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

    private static KnowledgeSchemaDefinition Schema() => new KnowledgeSchemaBuilder("snapshot-article")
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

    private static async IAsyncEnumerable<ExternalKnowledgeDocumentInput> FailingStream(ExternalKnowledgeDocumentInput first)
    {
        yield return first;
        await Task.Yield();
        throw new InvalidOperationException("synthetic staging failure");
    }

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"semantic-knowledge-snapshot-{Guid.NewGuid():N}.db");
    private static void Delete(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            try { File.Delete(path + suffix); } catch { }
    }
}
