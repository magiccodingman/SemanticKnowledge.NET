using Microsoft.Extensions.DependencyInjection;
using OnnxTextEmbeddings;
using SemanticKnowledge.Sqlite;

namespace SemanticKnowledge.Sqlite.Tests;

public sealed class EndToEndTests
{
    [Fact]
    public async Task Sqlite_vec_search_filters_and_smart_routes_without_loading_the_corpus()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), $"semantic-knowledge-{Guid.NewGuid():N}.db");
        try
        {
            var services = new ServiceCollection();
            services.AddSemanticKnowledge(options =>
                {
                    options.Embeddings.OutputDimensions = 4;
                    options.Embeddings.Storage = VectorStoragePreference.Compact;
                    options.Embeddings.PersistedRecordFormat = EmbeddingVectorFormat.Int8;
                })
                .UseSqlite(path);
            services.AddSingleton<IKnowledgeEmbeddingProvider, DeterministicEmbeddingProvider>();

            await using var provider = services.BuildServiceProvider();
            var store = provider.GetRequiredService<ISemanticKnowledgeStore>();
            var capabilities = await store.InitializeAsync(cancellationToken);
            Assert.Equal("INT8", capabilities.PhysicalVectorStorage);

            var kb = await store.GetOrCreateKnowledgeBaseAsync("Test Wiki", "test-wiki", cancellationToken);
            var postgres = await store.GetOrCreateCollectionAsync(kb.Id, "PostgreSQL", externalId: "postgres", cancellationToken: cancellationToken);
            var dragons = await store.GetOrCreateCollectionAsync(kb.Id, "Dragons", externalId: "dragons", cancellationToken: cancellationToken);

            var schema = new KnowledgeSchemaBuilder("article")
                .SetSemanticWeight(KnowledgeSystemFields.Title, 30)
                .SetSemanticWeight(KnowledgeSystemFields.Description, 20)
                .SetSemanticWeight(KnowledgeSystemFields.Tags, 20)
                .Text(KnowledgeSystemFields.Body, 30, SemanticMode.Chunked)
                .Int64("version")
                .Build();
            await store.EnsureSchemaAsync(schema, cancellationToken);

            var restoreId = await store.UpsertDocumentAsync(new KnowledgeDocumentInput
            {
                KnowledgeBaseId = kb.Id,
                CollectionId = postgres.Id,
                SchemaId = schema.Id,
                ExternalId = "postgres-restore",
                Title = "Restore PostgreSQL",
                Description = "Restore a PostgreSQL backup after a server failure.",
                Tags = ["postgres", "backup"],
                Values = new Dictionary<string, KnowledgeValue>
                {
                    [KnowledgeSystemFields.Body] = KnowledgeValue.From("Use pg_restore to restore the PostgreSQL backup."),
                    ["version"] = KnowledgeValue.From(2L)
                }
            }, cancellationToken);

            await store.UpsertDocumentAsync(new KnowledgeDocumentInput
            {
                KnowledgeBaseId = kb.Id,
                CollectionId = postgres.Id,
                SchemaId = schema.Id,
                ExternalId = "old-postgres-note",
                Title = "Old PostgreSQL note",
                Description = "An obsolete database note.",
                Tags = ["postgres"],
                Values = new Dictionary<string, KnowledgeValue>
                {
                    [KnowledgeSystemFields.Body] = KnowledgeValue.From("PostgreSQL database maintenance."),
                    ["version"] = KnowledgeValue.From(1L)
                }
            }, cancellationToken);

            await store.UpsertDocumentAsync(new KnowledgeDocumentInput
            {
                KnowledgeBaseId = kb.Id,
                CollectionId = dragons.Id,
                SchemaId = schema.Id,
                ExternalId = "red-dragon",
                Title = "Ancient Red Dragon",
                Description = "Campaign lore about a dragon.",
                Tags = ["dragon"],
                Values = new Dictionary<string, KnowledgeValue>
                {
                    [KnowledgeSystemFields.Body] = KnowledgeValue.From("The ancient dragon sleeps beneath the mountain."),
                    ["version"] = KnowledgeValue.From(9L)
                }
            }, cancellationToken);

            var filtered = await store.SearchAsync("restore postgres backup", new KnowledgeSearchRequest
            {
                KnowledgeBaseId = kb.Id,
                Filter = KnowledgeFilters.Gte("version", KnowledgeValue.From(2L)),
                Top = 5
            }, cancellationToken);

            Assert.NotEmpty(filtered);
            Assert.Equal(restoreId, filtered[0].DocumentId);
            Assert.DoesNotContain(filtered, hit => hit.Title.Contains("Old", StringComparison.Ordinal));

            var smart = await store.SearchAsync("postgres backup", new KnowledgeSearchRequest
            {
                KnowledgeBaseId = kb.Id,
                Mode = KnowledgeSearchMode.Smart,
                Top = 5,
                Include = KnowledgeResultInclude.MatchedChunks
            }, cancellationToken);

            Assert.NotEmpty(smart);
            Assert.Equal(restoreId, smart[0].DocumentId);
            Assert.DoesNotContain(smart, hit => hit.Title.Contains("Dragon", StringComparison.Ordinal));
            Assert.Contains(smart[0].Matches, match => !string.IsNullOrWhiteSpace(match.Text));

            var hydrated = await store.GetDocumentAsync(restoreId, cancellationToken);
            Assert.NotNull(hydrated);
            Assert.Equal(2L, hydrated.Values["version"].Int64);
        }
        finally
        {
            try { File.Delete(path); } catch { }
            try { File.Delete(path + "-wal"); } catch { }
            try { File.Delete(path + "-shm"); } catch { }
        }
    }

    private sealed class DeterministicEmbeddingProvider : IKnowledgeEmbeddingProvider
    {
        private static readonly EmbeddingIdentity Identity = new()
        {
            ModelId = "deterministic-test",
            SourceRevision = "v1",
            EmbeddingSpaceFingerprint = "deterministic-test-space-v1",
            IsNormalized = true
        };

        public Task<KnowledgeEmbeddingProviderInfo> GetInfoAsync(CancellationToken cancellationToken = default) => Task.FromResult(new KnowledgeEmbeddingProviderInfo
        {
            Provider = "test",
            ModelId = Identity.ModelId,
            SourceRevision = Identity.SourceRevision,
            EmbeddingSpaceFingerprint = Identity.EmbeddingSpaceFingerprint,
            NativeDimensions = 4,
            OutputDimensions = 4,
            SupportsTokenCounting = true,
            SupportsChunkedDocuments = true
        });

        public Task<QueryEmbedding> EmbedQueryAsync(string text, CancellationToken cancellationToken = default)
        {
            var tokens = TokenCount(text);
            return Task.FromResult(new QueryEmbedding
            {
                Vector = EmbeddingVector.FromFloat32(VectorFor(text), EmbeddingVectorFormat.Float32),
                Identity = Identity,
                SourceTokenCount = tokens,
                InputTokenCount = tokens
            });
        }

        public Task<IReadOnlyList<TextEmbedding>> EmbedDocumentAsync(string text, CancellationToken cancellationToken = default)
        {
            var tokens = TokenCount(text);
            IReadOnlyList<TextEmbedding> result =
            [
                new TextEmbedding
                {
                    Vector = EmbeddingVector.FromFloat32(VectorFor(text), EmbeddingVectorFormat.Int8),
                    Identity = Identity,
                    Source = new EmbeddingSource
                    {
                        DocumentTokenCount = tokens,
                        CharacterRange = new Utf16TextRange(0, text.Length),
                        TokenRange = new TokenRange(0, tokens),
                        TokenCount = tokens,
                        TokenCapacity = Math.Max(tokens, 1)
                    },
                    Chunk = new EmbeddingChunkInfo
                    {
                        Index = 0,
                        Count = 1,
                        BoundaryKind = ChunkBoundaryKind.WholeDocument,
                        InputTokenCount = tokens
                    },
                    Text = text
                }
            ];
            return Task.FromResult(result);
        }

        public Task<int?> TryCountTokensAsync(string text, CancellationToken cancellationToken = default) =>
            Task.FromResult<int?>(TokenCount(text));

        private static int TokenCount(string text) => Math.Max(1, text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);

        private static float[] VectorFor(string text)
        {
            if (text.Contains("postgres", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("backup", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("pg_restore", StringComparison.OrdinalIgnoreCase))
                return [1f, 0f, 0f, 0f];

            if (text.Contains("dragon", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("mountain", StringComparison.OrdinalIgnoreCase))
                return [0f, 1f, 0f, 0f];

            return [0f, 0f, 1f, 0f];
        }
    }
}
