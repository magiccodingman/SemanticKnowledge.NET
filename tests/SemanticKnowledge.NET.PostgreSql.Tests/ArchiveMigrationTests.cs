using Microsoft.Extensions.DependencyInjection;
using OnnxTextEmbeddings;
using SemanticKnowledge.PostgreSql;
using SemanticKnowledge.Sqlite;

namespace SemanticKnowledge.PostgreSql.Tests;

public sealed class ArchiveMigrationTests
{
    [Fact]
    public async Task Sqlite_archive_imports_into_postgresql_with_stable_document_identity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = Environment.GetEnvironmentVariable("SEMANTIC_KNOWLEDGE_POSTGRES");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        var sqlitePath = Path.Combine(Path.GetTempPath(), $"sk-pg-migration-{Guid.NewGuid():N}.db");
        try
        {
            await using var source = BuildSqlite(sqlitePath, "sqlite-source-space");
            var sourceStore = source.GetRequiredService<ISemanticKnowledgeStore>();
            var sourceArchive = source.GetRequiredService<IKnowledgeArchiveService>();
            await sourceStore.InitializeAsync(cancellationToken);
            var kb = await sourceStore.GetOrCreateKnowledgeBaseAsync("Portable Provider Wiki", cancellationToken: cancellationToken);
            var schema = KnowledgeSchemaBuilder.CreateDefaultDocument("provider-portable");
            await sourceStore.EnsureSchemaAsync(schema, cancellationToken);
            var collection = await sourceStore.GetOrCreateCollectionAsync(kb.Id, "Recovery", defaultSchemaId: schema.Id, cancellationToken: cancellationToken);
            var documentId = await sourceStore.UpsertDocumentAsync(new KnowledgeDocumentInput
            {
                ExternalId = "postgres/restore",
                KnowledgeBaseId = kb.Id,
                CollectionId = collection.Id,
                SchemaId = schema.Id,
                Title = "Alpha PostgreSQL backup",
                Description = "Portable recovery procedure.",
                Tags = ["alpha", "backup"],
                Values = new Dictionary<string, KnowledgeValue>
                {
                    [KnowledgeSystemFields.Body] = KnowledgeValue.From("alpha postgres backup recovery")
                }
            }, cancellationToken);

            await using var archive = new MemoryStream();
            await sourceArchive.ExportKnowledgeBaseAsync(kb.Id, archive, cancellationToken);
            archive.Position = 0;

            var services = new ServiceCollection();
            services.AddSemanticKnowledge(options => options.Embeddings.OutputDimensions = 4)
                .UsePostgreSql(options =>
                {
                    options.ConnectionString = connectionString!;
                    options.Schema = $"sk_archive_{Guid.NewGuid():N}";
                });
            services.AddSingleton<IKnowledgeEmbeddingProvider>(new PortableEmbeddingProvider("postgres-target-space"));
            await using var target = services.BuildServiceProvider();
            var targetStore = target.GetRequiredService<ISemanticKnowledgeStore>();
            var targetArchive = target.GetRequiredService<IKnowledgeArchiveService>();
            var imported = await targetArchive.ImportAsync(archive, cancellationToken);

            Assert.Equal(kb.Id, imported.KnowledgeBaseId);
            Assert.Equal(1, imported.DocumentCount);
            var document = await targetStore.GetDocumentAsync(documentId, cancellationToken);
            Assert.NotNull(document);
            Assert.Equal("postgres/restore", document.ExternalId);
            var hits = await targetStore.SearchAsync("alpha backup", new KnowledgeSearchRequest { KnowledgeBaseId = kb.Id, Top = 5 }, cancellationToken);
            Assert.Contains(hits, hit => hit.DocumentId == documentId);
        }
        finally
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
                try { File.Delete(sqlitePath + suffix); } catch { }
        }
    }

    private static ServiceProvider BuildSqlite(string path, string fingerprint)
    {
        var services = new ServiceCollection();
        services.AddSemanticKnowledge(options => options.Embeddings.OutputDimensions = 4).UseSqlite(path);
        services.AddSingleton<IKnowledgeEmbeddingProvider>(new PortableEmbeddingProvider(fingerprint));
        return services.BuildServiceProvider();
    }

    private sealed class PortableEmbeddingProvider(string fingerprint) : IKnowledgeEmbeddingProvider
    {
        private readonly EmbeddingIdentity _identity = new() { ModelId = "archive-provider-test", SourceRevision = fingerprint, EmbeddingSpaceFingerprint = fingerprint, IsNormalized = true };
        public Task<KnowledgeEmbeddingProviderInfo> GetInfoAsync(CancellationToken cancellationToken = default) => Task.FromResult(new KnowledgeEmbeddingProviderInfo { Provider = "test", ModelId = _identity.ModelId, SourceRevision = _identity.SourceRevision, EmbeddingSpaceFingerprint = _identity.EmbeddingSpaceFingerprint, NativeDimensions = 4, OutputDimensions = 4, SupportsTokenCounting = true, SupportsChunkedDocuments = true });
        public Task<QueryEmbedding> EmbedQueryAsync(string text, CancellationToken cancellationToken = default) { var count = Count(text); return Task.FromResult(new QueryEmbedding { Vector = EmbeddingVector.FromFloat32(Vector(text), EmbeddingVectorFormat.Float32), Identity = _identity, SourceTokenCount = count, InputTokenCount = count }); }
        public Task<IReadOnlyList<TextEmbedding>> EmbedDocumentAsync(string text, CancellationToken cancellationToken = default)
        {
            var count = Count(text);
            IReadOnlyList<TextEmbedding> result = [new TextEmbedding { Vector = EmbeddingVector.FromFloat32(Vector(text), EmbeddingVectorFormat.Int8), Identity = _identity, Source = new EmbeddingSource { DocumentTokenCount = count, CharacterRange = new Utf16TextRange(0, text.Length), TokenRange = new TokenRange(0, count), TokenCount = count, TokenCapacity = Math.Max(1, count) }, Chunk = new EmbeddingChunkInfo { Index = 0, Count = 1, BoundaryKind = ChunkBoundaryKind.WholeDocument, InputTokenCount = count }, Text = text }];
            return Task.FromResult(result);
        }
        public Task<int?> TryCountTokensAsync(string text, CancellationToken cancellationToken = default) => Task.FromResult<int?>(Count(text));
        private static int Count(string text) => Math.Max(1, text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        private static float[] Vector(string text) => text.Contains("alpha", StringComparison.OrdinalIgnoreCase) || text.Contains("backup", StringComparison.OrdinalIgnoreCase) ? [1f, 0f, 0f, 0f] : [0f, 1f, 0f, 0f];
    }
}
