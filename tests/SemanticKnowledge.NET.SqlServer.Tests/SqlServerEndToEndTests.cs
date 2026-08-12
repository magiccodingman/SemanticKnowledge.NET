using Microsoft.Extensions.DependencyInjection;
using OnnxTextEmbeddings;
using SemanticKnowledge.SqlServer;

namespace SemanticKnowledge.SqlServer.Tests;

public sealed class SqlServerEndToEndTests
{
    [Fact]
    public async Task Native_vector_filters_smart_routing_and_full_text_search_work_end_to_end()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = Environment.GetEnvironmentVariable("SEMANTIC_KNOWLEDGE_SQLSERVER");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        var schemaName = $"SKTest_{Guid.NewGuid():N}";

        var services = new ServiceCollection();
        services.AddSemanticKnowledge(options =>
            {
                options.Embeddings.OutputDimensions = 4;
                options.Embeddings.Storage = VectorStoragePreference.MaximumPrecision;
            })
            .UseSqlServer(options =>
            {
                options.ConnectionString = connectionString!;
                options.Schema = schemaName;
            });
        services.AddSingleton<IKnowledgeEmbeddingProvider, DeterministicEmbeddingProvider>();

        await using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<ISemanticKnowledgeStore>();
        var capabilities = await store.InitializeAsync(cancellationToken);
        Assert.Equal("SQL Server 2025/Azure SQL", capabilities.Provider);
        Assert.Equal(1998, capabilities.MaxDimensions);
        Assert.Equal("VECTOR(float32)", capabilities.PhysicalVectorStorage);
        Assert.True(capabilities.ExactVectorSearch);
        Assert.True(capabilities.LexicalSearchSupported);
        Assert.Contains("Full-Text", capabilities.LexicalSearchProvider, StringComparison.OrdinalIgnoreCase);

        var kb = await store.GetOrCreateKnowledgeBaseAsync("Provider Test", "provider-test", cancellationToken);
        var databases = await store.GetOrCreateCollectionAsync(kb.Id, "Databases", externalId: "databases", cancellationToken: cancellationToken);
        var creatures = await store.GetOrCreateCollectionAsync(kb.Id, "Creatures", externalId: "creatures", cancellationToken: cancellationToken);
        var schema = new KnowledgeSchemaBuilder("article")
            .SetSemanticWeight(KnowledgeSystemFields.Title, 30)
            .SetSemanticWeight(KnowledgeSystemFields.Description, 20)
            .SetSemanticWeight(KnowledgeSystemFields.Tags, 20)
            .Text(KnowledgeSystemFields.Body, 30, SemanticMode.Chunked)
            .Int64("version")
            .Build();
        await store.EnsureSchemaAsync(schema, cancellationToken);

        var expectedId = await store.UpsertDocumentAsync(new KnowledgeDocumentInput
        {
            KnowledgeBaseId = kb.Id,
            CollectionId = databases.Id,
            SchemaId = schema.Id,
            ExternalId = "restore-sql",
            Title = "Restore SQL Database",
            Description = "Restoring a database backup.",
            Tags = ["sql", "backup"],
            Values = new Dictionary<string, KnowledgeValue>
            {
                [KnowledgeSystemFields.Body] = KnowledgeValue.From("Restore the SQL database from its backup."),
                ["version"] = KnowledgeValue.From(2L)
            }
        }, cancellationToken);

        await store.UpsertDocumentAsync(new KnowledgeDocumentInput
        {
            KnowledgeBaseId = kb.Id,
            CollectionId = databases.Id,
            SchemaId = schema.Id,
            ExternalId = "old-sql",
            Title = "Old SQL Note",
            Description = "Old database note.",
            Tags = ["sql"],
            Values = new Dictionary<string, KnowledgeValue>
            {
                [KnowledgeSystemFields.Body] = KnowledgeValue.From("SQL database maintenance."),
                ["version"] = KnowledgeValue.From(1L)
            }
        }, cancellationToken);

        await store.UpsertDocumentAsync(new KnowledgeDocumentInput
        {
            KnowledgeBaseId = kb.Id,
            CollectionId = creatures.Id,
            SchemaId = schema.Id,
            ExternalId = "dragon",
            Title = "Ancient Dragon",
            Description = "Campaign lore.",
            Tags = ["dragon"],
            Values = new Dictionary<string, KnowledgeValue>
            {
                [KnowledgeSystemFields.Body] = KnowledgeValue.From("The dragon sleeps below the mountain."),
                ["version"] = KnowledgeValue.From(9L)
            }
        }, cancellationToken);

        var filtered = await store.SearchAsync("restore sql backup", new KnowledgeSearchRequest
        {
            KnowledgeBaseId = kb.Id,
            Filter = KnowledgeFilters.Gte("version", KnowledgeValue.From(2L)),
            Top = 5
        }, cancellationToken);
        Assert.NotEmpty(filtered);
        Assert.Equal(expectedId, filtered[0].DocumentId);
        Assert.DoesNotContain(filtered, hit => hit.Title.Contains("Old", StringComparison.Ordinal));

        var smart = await store.SearchAsync("sql backup", new KnowledgeSearchRequest
        {
            KnowledgeBaseId = kb.Id,
            Mode = KnowledgeSearchMode.Smart,
            Top = 5,
            Include = KnowledgeResultInclude.MatchedChunks
        }, cancellationToken);
        Assert.NotEmpty(smart);
        Assert.Equal(expectedId, smart[0].DocumentId);
        Assert.DoesNotContain(smart, hit => hit.Title.Contains("Dragon", StringComparison.Ordinal));
        Assert.Contains(smart[0].Matches, match => !string.IsNullOrWhiteSpace(match.Text));

        var lexicalQuery = KnowledgeSearchQuery.Create(kb.Id, "SQL database backup")
            .Add(KnowledgeRetrievalStage.Lexical("lexical-body", KnowledgeSearchField.Body(3))
                .Where(KnowledgeFilters.Gte("version", KnowledgeValue.From(2L))))
            .Take(5);
        var lexical = await SearchEventuallyAsync(store, lexicalQuery, hits => hits.Any(hit => hit.DocumentId == expectedId), cancellationToken);
        var lexicalHit = Assert.Single(lexical, hit => hit.DocumentId == expectedId);
        Assert.DoesNotContain(lexical, hit => hit.Title.Contains("Old", StringComparison.Ordinal));
        var lexicalContribution = Assert.Single(lexicalHit.Contributions);
        Assert.Equal(KnowledgeRetrievalKind.Lexical, lexicalContribution.Kind);
        Assert.Contains(lexicalContribution.LexicalFields, field => field.FieldKey == KnowledgeSystemFields.Body);

        var hybridQuery = KnowledgeSearchQuery.Create(kb.Id, "sql database backup")
            .Smart()
            .Hybrid()
            .Where(KnowledgeFilters.Gte("version", KnowledgeValue.From(2L)))
            .Take(5);
        var hybrid = await SearchEventuallyAsync(store, hybridQuery, hits => hits.Any(hit => hit.DocumentId == expectedId && hit.Contributions.Any(c => c.Kind == KnowledgeRetrievalKind.Lexical)), cancellationToken);
        var hybridHit = Assert.Single(hybrid, hit => hit.DocumentId == expectedId);
        Assert.DoesNotContain(hybrid, hit => hit.Title.Contains("Old", StringComparison.Ordinal));
        Assert.DoesNotContain(hybrid, hit => hit.Title.Contains("Dragon", StringComparison.Ordinal));
        Assert.Contains(hybridHit.Contributions, contribution => contribution.Kind == KnowledgeRetrievalKind.Semantic);
        Assert.Contains(hybridHit.Contributions, contribution => contribution.Kind == KnowledgeRetrievalKind.Lexical);
    }

    [Fact]
    public async Task Dimensions_above_1998_are_rejected_explicitly()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = Environment.GetEnvironmentVariable("SEMANTIC_KNOWLEDGE_SQLSERVER");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        var services = new ServiceCollection();
        services.AddSemanticKnowledge(options => options.Embeddings.OutputDimensions = 1999)
            .UseSqlServer(connectionString!);
        services.AddSingleton<IKnowledgeEmbeddingProvider>(_ => new OversizedEmbeddingProvider());
        await using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<ISemanticKnowledgeStore>();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => store.InitializeAsync(cancellationToken));
        Assert.Contains("at most 1998", exception.Message, StringComparison.Ordinal);
        Assert.Contains("never silently performs lossy reduction", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<IReadOnlyList<KnowledgeSearchHit>> SearchEventuallyAsync(
        ISemanticKnowledgeStore store,
        KnowledgeSearchQuery query,
        Func<IReadOnlyList<KnowledgeSearchHit>, bool> predicate,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<KnowledgeSearchHit> last = Array.Empty<KnowledgeSearchHit>();
        for (var attempt = 0; attempt < 20; attempt++)
        {
            last = await store.SearchAsync(query, cancellationToken);
            if (predicate(last)) return last;
            await Task.Delay(250, cancellationToken);
        }
        return last;
    }

    private sealed class DeterministicEmbeddingProvider : IKnowledgeEmbeddingProvider
    {
        private static readonly EmbeddingIdentity Identity = new()
        {
            ModelId = "provider-test",
            SourceRevision = "v1",
            EmbeddingSpaceFingerprint = "provider-test-space-v1",
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
            var tokens = Count(text);
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
            var tokens = Count(text);
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

        public Task<int?> TryCountTokensAsync(string text, CancellationToken cancellationToken = default) => Task.FromResult<int?>(Count(text));
        private static int Count(string text) => Math.Max(1, text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        private static float[] VectorFor(string text)
        {
            if (text.Contains("sql", StringComparison.OrdinalIgnoreCase) || text.Contains("backup", StringComparison.OrdinalIgnoreCase) || text.Contains("database", StringComparison.OrdinalIgnoreCase)) return [1f, 0f, 0f, 0f];
            if (text.Contains("dragon", StringComparison.OrdinalIgnoreCase) || text.Contains("mountain", StringComparison.OrdinalIgnoreCase)) return [0f, 1f, 0f, 0f];
            return [0f, 0f, 1f, 0f];
        }
    }

    private sealed class OversizedEmbeddingProvider : IKnowledgeEmbeddingProvider
    {
        public Task<KnowledgeEmbeddingProviderInfo> GetInfoAsync(CancellationToken cancellationToken = default) => Task.FromResult(new KnowledgeEmbeddingProviderInfo
        {
            Provider = "oversized-test",
            ModelId = "oversized",
            SourceRevision = "v1",
            EmbeddingSpaceFingerprint = "oversized-space-v1",
            NativeDimensions = 1999,
            OutputDimensions = 1999,
            SupportsTokenCounting = false,
            SupportsChunkedDocuments = false
        });
        public Task<QueryEmbedding> EmbedQueryAsync(string text, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TextEmbedding>> EmbedDocumentAsync(string text, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int?> TryCountTokensAsync(string text, CancellationToken cancellationToken = default) => Task.FromResult<int?>(null);
    }
}
