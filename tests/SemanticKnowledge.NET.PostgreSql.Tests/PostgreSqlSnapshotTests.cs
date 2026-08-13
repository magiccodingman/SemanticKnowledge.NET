using Microsoft.Extensions.DependencyInjection;
using OnnxTextEmbeddings;
using SemanticKnowledge.PostgreSql;

namespace SemanticKnowledge.PostgreSql.Tests;

public sealed class PostgreSqlSnapshotTests
{
    [Fact]
    public async Task Atomic_snapshot_publishes_one_revision_and_failed_replacement_stays_invisible()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = Environment.GetEnvironmentVariable("SEMANTIC_KNOWLEDGE_POSTGRES");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        var schemaName = $"sk_snapshot_{Guid.NewGuid():N}";

        var services = new ServiceCollection();
        services.AddSemanticKnowledge(options => { options.Embeddings.OutputDimensions = 4; options.Embeddings.Storage = VectorStoragePreference.Compact; })
            .UsePostgreSql(options => { options.ConnectionString = connectionString!; options.Schema = schemaName; });
        services.AddSingleton<IKnowledgeEmbeddingProvider, Provider>();
        await using var root = services.BuildServiceProvider();
        var store = root.GetRequiredService<ISemanticKnowledgeStore>();
        var sync = root.GetRequiredService<IKnowledgeSynchronizationService>();
        await store.InitializeAsync(cancellationToken);
        var kb = await store.GetOrCreateKnowledgeBaseAsync("Snapshot Provider Test", cancellationToken: cancellationToken);
        var collection = await store.GetOrCreateCollectionAsync(kb.Id, "Pages", cancellationToken: cancellationToken);
        var schema = Schema(); await store.EnsureSchemaAsync(schema, cancellationToken);

        await sync.SyncCollectionSnapshotAsync(kb.Id, collection.Id, schema.Id, "pg-rev-1", Stream(Page("a", "Alpha old", "alpha postgres old")), cancellationToken: cancellationToken);
        var oldId = (await root.GetRequiredService<IKnowledgeStorageProvider>().GetDocumentsAsync(kb.Id, cancellationToken)).Single().Id;

        var second = await sync.SyncCollectionSnapshotAsync(kb.Id, collection.Id, schema.Id, "pg-rev-2", Stream(Page("a", "Gamma current", "gamma postgres current"), Page("b", "Alpha added", "alpha postgres added")), cancellationToken: cancellationToken);
        Assert.Equal("pg-rev-2", second.SourceRevision);
        var current = await root.GetRequiredService<IKnowledgeStorageProvider>().GetDocumentsAsync(kb.Id, cancellationToken);
        Assert.Equal(oldId, current.Single(document => document.ExternalId == "a").Id);
        Assert.Equal(2, current.Count);

        var hybrid = await store.SearchAsync(KnowledgeSearchQuery.Create(kb.Id, "Gamma current").Hybrid().Take(5), cancellationToken);
        Assert.Contains(hybrid, hit => hit.DocumentId == oldId && hit.Title == "Gamma current");

        await Assert.ThrowsAsync<InvalidOperationException>(() => sync.SyncCollectionSnapshotAsync(kb.Id, collection.Id, schema.Id, "pg-rev-3", Failing(Page("a", "Beta unpublished", "beta unpublished")), cancellationToken: cancellationToken));
        var state = await sync.GetCollectionSnapshotStateAsync(collection.Id, cancellationToken);
        Assert.NotNull(state); Assert.Equal("pg-rev-2", state.SourceRevision); Assert.Null(state.StagingSnapshotId);
        var leaked = await store.SearchAsync(KnowledgeSearchQuery.Create(kb.Id, "Beta unpublished").Lexical(KnowledgeSearchField.Title()).Take(5), cancellationToken);
        Assert.DoesNotContain(leaked, hit => hit.Title == "Beta unpublished");
        var stillCurrent = await store.SearchAsync(KnowledgeSearchQuery.Create(kb.Id, "Gamma current").Hybrid().Take(5), cancellationToken);
        Assert.Contains(stillCurrent, hit => hit.DocumentId == oldId);
    }

    private static KnowledgeSchemaDefinition Schema() => new KnowledgeSchemaBuilder("snapshot")
        .SetSemanticWeight(KnowledgeSystemFields.Title, 40).SetSemanticWeight(KnowledgeSystemFields.Description, 10).SetSemanticWeight(KnowledgeSystemFields.Tags, 10).Text(KnowledgeSystemFields.Body, 40, SemanticMode.Chunked).Build();
    private static ExternalKnowledgeDocumentInput Page(string id, string title, string body) => new() { ExternalId=id,Title=title,Values=new Dictionary<string,KnowledgeValue>{{KnowledgeSystemFields.Body,KnowledgeValue.From(body)}} };
    private static async IAsyncEnumerable<ExternalKnowledgeDocumentInput> Stream(params ExternalKnowledgeDocumentInput[] items){foreach(var item in items){yield return item;await Task.Yield();}}
    private static async IAsyncEnumerable<ExternalKnowledgeDocumentInput> Failing(ExternalKnowledgeDocumentInput item){yield return item;await Task.Yield();throw new InvalidOperationException("synthetic snapshot failure");}

    private sealed class Provider : IKnowledgeEmbeddingProvider
    {
        private static readonly EmbeddingIdentity Identity=new(){ModelId="snapshot-test",SourceRevision="v1",EmbeddingSpaceFingerprint="snapshot-test-space",IsNormalized=true};
        public Task<KnowledgeEmbeddingProviderInfo> GetInfoAsync(CancellationToken cancellationToken=default)=>Task.FromResult(new KnowledgeEmbeddingProviderInfo{Provider="test",ModelId=Identity.ModelId,SourceRevision=Identity.SourceRevision,EmbeddingSpaceFingerprint=Identity.EmbeddingSpaceFingerprint,NativeDimensions=4,OutputDimensions=4,IsNormalized=true,SupportsTokenCounting=true,SupportsChunkedDocuments=true});
        public Task<QueryEmbedding> EmbedQueryAsync(string text,CancellationToken cancellationToken=default){var n=Count(text);return Task.FromResult(new QueryEmbedding{Vector=EmbeddingVector.FromFloat32(Vector(text),EmbeddingVectorFormat.Float32),Identity=Identity,SourceTokenCount=n,InputTokenCount=n});}
        public Task<IReadOnlyList<TextEmbedding>> EmbedDocumentAsync(string text,CancellationToken cancellationToken=default){var n=Count(text);IReadOnlyList<TextEmbedding> result=[new TextEmbedding{Vector=EmbeddingVector.FromFloat32(Vector(text),EmbeddingVectorFormat.Int8),Identity=Identity,Source=new EmbeddingSource{DocumentTokenCount=n,CharacterRange=new Utf16TextRange(0,text.Length),TokenRange=new TokenRange(0,n),TokenCount=n,TokenCapacity=n},Chunk=new EmbeddingChunkInfo{Index=0,Count=1,BoundaryKind=ChunkBoundaryKind.WholeDocument,InputTokenCount=n},Text=text}];return Task.FromResult(result);}
        public Task<int?> TryCountTokensAsync(string text,CancellationToken cancellationToken=default)=>Task.FromResult<int?>(Count(text));
        private static int Count(string text)=>Math.Max(1,text.Split(' ',StringSplitOptions.RemoveEmptyEntries).Length);
        private static float[] Vector(string text)=>text.Contains("gamma",StringComparison.OrdinalIgnoreCase)?[0f,1f,0f,0f]:text.Contains("beta",StringComparison.OrdinalIgnoreCase)?[0f,0f,1f,0f]:[1f,0f,0f,0f];
    }
}
