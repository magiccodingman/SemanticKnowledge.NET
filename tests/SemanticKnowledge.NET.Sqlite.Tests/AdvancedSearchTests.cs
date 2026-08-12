using Microsoft.Extensions.DependencyInjection;
using OnnxTextEmbeddings;
using SemanticKnowledge.Sqlite;

namespace SemanticKnowledge.Sqlite.Tests;

public sealed class AdvancedSearchTests
{
    [Fact]
    public async Task Lexical_and_hybrid_search_compose_fields_filters_and_semantic_evidence()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), $"semantic-knowledge-advanced-{Guid.NewGuid():N}.db");
        try
        {
            var embeddings = new CountingEmbeddingProvider();
            var services = new ServiceCollection();
            services.AddSemanticKnowledge(options =>
                {
                    options.Embeddings.OutputDimensions = 4;
                    options.Embeddings.Storage = VectorStoragePreference.Compact;
                    options.Embeddings.PersistedRecordFormat = EmbeddingVectorFormat.Int8;
                })
                .UseSqlite(path);
            services.AddSingleton<IKnowledgeEmbeddingProvider>(embeddings);

            await using var provider = services.BuildServiceProvider();
            var store = provider.GetRequiredService<ISemanticKnowledgeStore>();
            var capabilities = await store.InitializeAsync(cancellationToken);
            Assert.True(capabilities.LexicalSearchSupported);
            Assert.Contains("FTS5", capabilities.LexicalSearchProvider, StringComparison.OrdinalIgnoreCase);

            var kb = await store.GetOrCreateKnowledgeBaseAsync("Operations", "ops", cancellationToken);
            var databases = await store.GetOrCreateCollectionAsync(kb.Id, "Database Recovery", externalId: "db-recovery", cancellationToken: cancellationToken);
            var lore = await store.GetOrCreateCollectionAsync(kb.Id, "Fantasy Lore", externalId: "lore", cancellationToken: cancellationToken);

            var schema = new KnowledgeSchemaBuilder("article")
                .SetSemanticWeight(KnowledgeSystemFields.Title, 30)
                .SetSemanticWeight(KnowledgeSystemFields.Description, 20)
                .SetSemanticWeight(KnowledgeSystemFields.Tags, 20)
                .Text(KnowledgeSystemFields.Body, 30, SemanticMode.Chunked)
                .Text("private_notes", semanticWeightPercent: 0, semanticMode: SemanticMode.None)
                .Int64("version")
                .Build();
            await store.EnsureSchemaAsync(schema, cancellationToken);

            var restoreId = await store.UpsertDocumentAsync(new KnowledgeDocumentInput
            {
                KnowledgeBaseId = kb.Id,
                CollectionId = databases.Id,
                SchemaId = schema.Id,
                Title = "Restore PostgreSQL",
                Description = "Recovery procedure after a database server failure.",
                Tags = ["postgres", "backup", "operations"],
                Values = new Dictionary<string, KnowledgeValue>
                {
                    [KnowledgeSystemFields.Body] = KnowledgeValue.From("Use pg_restore to recover the production PostgreSQL backup."),
                    ["private_notes"] = KnowledgeValue.From("wal-gamma-restore-token"),
                    ["version"] = KnowledgeValue.From(2L)
                }
            }, cancellationToken);

            var oldId = await store.UpsertDocumentAsync(new KnowledgeDocumentInput
            {
                KnowledgeBaseId = kb.Id,
                CollectionId = databases.Id,
                SchemaId = schema.Id,
                Title = "Legacy PostgreSQL maintenance",
                Description = "Old procedure that should be filtered out.",
                Tags = ["postgres", "obsolete"],
                Values = new Dictionary<string, KnowledgeValue>
                {
                    [KnowledgeSystemFields.Body] = KnowledgeValue.From("Use pg_restore for an obsolete backup workflow."),
                    ["private_notes"] = KnowledgeValue.From("historical-only"),
                    ["version"] = KnowledgeValue.From(1L)
                }
            }, cancellationToken);

            var dragonId = await store.UpsertDocumentAsync(new KnowledgeDocumentInput
            {
                KnowledgeBaseId = kb.Id,
                CollectionId = lore.Id,
                SchemaId = schema.Id,
                Title = "Ancient Red Dragon",
                Description = "Campaign lore about a mountain dragon.",
                Tags = ["dragon"],
                Values = new Dictionary<string, KnowledgeValue>
                {
                    [KnowledgeSystemFields.Body] = KnowledgeValue.From("The dragon sleeps beneath the mountain."),
                    ["private_notes"] = KnowledgeValue.From("red-wyrm"),
                    ["version"] = KnowledgeValue.From(9L)
                }
            }, cancellationToken);

            var beforeLexical = embeddings.QueryCalls;
            var lexicalOnly = await store.SearchAsync(
                KnowledgeSearchQuery.Create(kb.Id, "wal gamma restore token")
                    .Lexical(KnowledgeSearchField.Create("private_notes"))
                    .Take(5),
                cancellationToken);

            Assert.Equal(beforeLexical, embeddings.QueryCalls);
            Assert.Single(lexicalOnly);
            Assert.Equal(restoreId, lexicalOnly[0].DocumentId);
            var lexicalContribution = Assert.Single(lexicalOnly[0].Contributions);
            Assert.Equal(KnowledgeRetrievalKind.Lexical, lexicalContribution.Kind);
            Assert.Contains(lexicalContribution.LexicalFields, field => field.FieldKey == "private_notes");

            var titleOnly = await store.SearchAsync(
                KnowledgeSearchQuery.Create(kb.Id, "restore")
                    .Lexical(KnowledgeSearchField.Title(8))
                    .Take(5),
                cancellationToken);
            Assert.Equal(restoreId, Assert.Single(titleOnly).DocumentId);

            var filteredLexical = await store.SearchAsync(
                KnowledgeSearchQuery.Create(kb.Id, "pg restore")
                    .Add(KnowledgeRetrievalStage.Lexical("lexical-body", KnowledgeSearchField.Body())
                        .Where(KnowledgeFilters.Gte("version", KnowledgeValue.From(2L))))
                    .Take(10),
                cancellationToken);
            Assert.Contains(filteredLexical, hit => hit.DocumentId == restoreId);
            Assert.DoesNotContain(filteredLexical, hit => hit.DocumentId == oldId);

            var hybrid = await store.SearchAsync(
                KnowledgeSearchQuery.Create(kb.Id, "postgres backup")
                    .Smart()
                    .Hybrid()
                    .Where(KnowledgeFilters.Gte("version", KnowledgeValue.From(2L)))
                    .Take(10),
                cancellationToken);

            Assert.Contains(hybrid, hit => hit.DocumentId == restoreId);
            Assert.DoesNotContain(hybrid, hit => hit.DocumentId == oldId);
            Assert.DoesNotContain(hybrid, hit => hit.DocumentId == dragonId);
            var restore = Assert.Single(hybrid, hit => hit.DocumentId == restoreId);
            Assert.Contains(restore.Contributions, contribution => contribution.Kind == KnowledgeRetrievalKind.Semantic);
            Assert.Contains(restore.Contributions, contribution => contribution.Kind == KnowledgeRetrievalKind.Lexical);

            var postFiltered = await store.SearchAsync(
                KnowledgeSearchQuery.Create(kb.Id, "postgres backup")
                    .Hybrid()
                    .PostWhere(KnowledgeFilters.Gte("version", KnowledgeValue.From(2L)))
                    .Take(10),
                cancellationToken);
            Assert.Contains(postFiltered, hit => hit.DocumentId == restoreId);
            Assert.DoesNotContain(postFiltered, hit => hit.DocumentId == oldId);
        }
        finally
        {
            try { File.Delete(path); } catch { }
            try { File.Delete(path + "-wal"); } catch { }
            try { File.Delete(path + "-shm"); } catch { }
        }
    }

    private sealed class CountingEmbeddingProvider : IKnowledgeEmbeddingProvider
    {
        private static readonly EmbeddingIdentity Identity = new()
        {
            ModelId = "advanced-search-test",
            SourceRevision = "v1",
            EmbeddingSpaceFingerprint = "advanced-search-test-v1",
            IsNormalized = true
        };

        private int _queryCalls;
        public int QueryCalls => Volatile.Read(ref _queryCalls);

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
            Interlocked.Increment(ref _queryCalls);
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
            IReadOnlyList<TextEmbedding> result = [new TextEmbedding
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
            }];
            return Task.FromResult(result);
        }

        public Task<int?> TryCountTokensAsync(string text, CancellationToken cancellationToken = default) => Task.FromResult<int?>(TokenCount(text));
        private static int TokenCount(string text) => Math.Max(1, text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);

        private static float[] VectorFor(string text)
        {
            if (text.Contains("postgres", StringComparison.OrdinalIgnoreCase) || text.Contains("backup", StringComparison.OrdinalIgnoreCase) || text.Contains("pg_restore", StringComparison.OrdinalIgnoreCase)) return [1f, 0f, 0f, 0f];
            if (text.Contains("dragon", StringComparison.OrdinalIgnoreCase) || text.Contains("mountain", StringComparison.OrdinalIgnoreCase)) return [0f, 1f, 0f, 0f];
            return [0f, 0f, 1f, 0f];
        }
    }
}
