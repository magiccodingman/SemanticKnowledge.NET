using Microsoft.Extensions.DependencyInjection;
using SemanticKnowledge.Sqlite;

namespace SemanticKnowledge.Sqlite.Tests;

public sealed class ArchiveTests
{
    [Fact]
    public async Task Archive_round_trip_preserves_canonical_ids_metadata_and_regenerates_searchable_embeddings()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var sourcePath = TempPath("source");
        var targetPath = TempPath("target");
        try
        {
            Guid knowledgeBaseId;
            Guid parentCollectionId;
            Guid childCollectionId;
            Guid documentId;
            Guid schemaId;
            await using var sourceProvider = Build(sourcePath, "archive-space-v1");
            var sourceStore = sourceProvider.GetRequiredService<ISemanticKnowledgeStore>();
            var sourceCatalog = sourceProvider.GetRequiredService<IKnowledgeCatalog>();
            var sourceArchive = sourceProvider.GetRequiredService<IKnowledgeArchiveService>();
            await sourceStore.InitializeAsync(cancellationToken);

            var kb = await sourceStore.GetOrCreateKnowledgeBaseAsync("Portable Wiki", "portable-wiki", cancellationToken);
            knowledgeBaseId = kb.Id;
            var schema = new KnowledgeSchemaBuilder("portable-article")
                .SetSemanticWeight(KnowledgeSystemFields.Title, 30)
                .SetSemanticWeight(KnowledgeSystemFields.Description, 20)
                .SetSemanticWeight(KnowledgeSystemFields.Tags, 20)
                .Text(KnowledgeSystemFields.Body, 30, SemanticMode.Chunked)
                .Int64("revision")
                .Boolean("verified")
                .Build();
            schemaId = schema.Id;
            await sourceStore.EnsureSchemaAsync(schema, cancellationToken);

            var parent = await sourceStore.GetOrCreateCollectionAsync(kb.Id, "Infrastructure", defaultSchemaId: schema.Id, externalId: "infra", cancellationToken: cancellationToken);
            parentCollectionId = parent.Id;
            await sourceCatalog.UpsertCollectionAsync(parent with
            {
                Description = "Server, database, and infrastructure operations.",
                Tags = ["infrastructure", "servers"]
            }, cancellationToken);

            var child = await sourceStore.GetOrCreateCollectionAsync(kb.Id, "Database Recovery", parent.Id, schema.Id, "database-recovery", cancellationToken);
            childCollectionId = child.Id;
            await sourceCatalog.UpsertCollectionAsync(child with
            {
                Description = "PostgreSQL backup and restore procedures.",
                Tags = ["postgres", "backup", "restore"]
            }, cancellationToken);

            documentId = await sourceStore.UpsertDocumentAsync(new KnowledgeDocumentInput
            {
                ExternalId = "docs/postgres-restore.md",
                KnowledgeBaseId = kb.Id,
                CollectionId = child.Id,
                SchemaId = schema.Id,
                Title = "Restoring PostgreSQL backups",
                Description = "Recovery notes for PostgreSQL.",
                Tags = ["postgres", "backup"],
                Values = new Dictionary<string, KnowledgeValue>
                {
                    [KnowledgeSystemFields.Body] = KnowledgeValue.From("Use pg_restore to recover the PostgreSQL database from the selected backup."),
                    ["revision"] = KnowledgeValue.From(7L),
                    ["verified"] = KnowledgeValue.From(true)
                }
            }, cancellationToken);

            await using var archiveStream = new MemoryStream();
            var exported = await sourceArchive.ExportKnowledgeBaseAsync(kb.Id, archiveStream, cancellationToken);
            Assert.Equal(kb.Id, exported.KnowledgeBaseId);
            Assert.Equal(1, exported.DocumentCount);
            Assert.Equal(2, exported.CollectionCount);
            Assert.Equal(1, exported.SchemaCount);
            Assert.True(archiveStream.Length > 0);

            archiveStream.Position = 0;
            await using var targetProvider = Build(targetPath, "archive-space-v2");
            var targetStore = targetProvider.GetRequiredService<ISemanticKnowledgeStore>();
            var targetArchive = targetProvider.GetRequiredService<IKnowledgeArchiveService>();
            var targetArchiveStorage = targetProvider.GetRequiredService<IKnowledgeArchiveStorage>();
            var targetStorage = targetProvider.GetRequiredService<IKnowledgeStorageProvider>();

            var imported = await targetArchive.ImportAsync(archiveStream, cancellationToken);
            Assert.Equal(knowledgeBaseId, imported.KnowledgeBaseId);
            Assert.Equal(1, imported.DocumentCount);
            Assert.Equal(2, imported.CollectionCount);
            Assert.Equal(1, imported.SchemaCount);

            var importedKb = await targetArchiveStorage.GetKnowledgeBaseAsync(knowledgeBaseId, cancellationToken);
            Assert.NotNull(importedKb);
            Assert.Equal("Portable Wiki", importedKb.Title);
            Assert.Equal("portable-wiki", importedKb.ExternalId);

            var collections = await targetStorage.GetCollectionsAsync(knowledgeBaseId, cancellationToken);
            var importedParent = Assert.Single(collections, collection => collection.Id == parentCollectionId);
            Assert.Contains("infrastructure", importedParent.Tags, StringComparer.OrdinalIgnoreCase);
            var importedChild = Assert.Single(collections, collection => collection.Id == childCollectionId);
            Assert.Equal(parentCollectionId, importedChild.ParentCollectionId);
            Assert.Equal(schemaId, importedChild.DefaultSchemaId);
            Assert.Equal("PostgreSQL backup and restore procedures.", importedChild.Description);
            Assert.Contains("postgres", importedChild.Tags, StringComparer.OrdinalIgnoreCase);

            var importedDocument = await targetStore.GetDocumentAsync(documentId, cancellationToken);
            Assert.NotNull(importedDocument);
            Assert.Equal("docs/postgres-restore.md", importedDocument.ExternalId);
            Assert.Equal(schemaId, importedDocument.SchemaId);
            Assert.Equal(7L, importedDocument.Values["revision"].Int64);
            Assert.True(importedDocument.Values["verified"].Boolean);

            var smart = await targetStore.SearchAsync("postgres backup recovery", new KnowledgeSearchRequest
            {
                KnowledgeBaseId = knowledgeBaseId,
                Mode = KnowledgeSearchMode.Smart,
                Top = 5,
                Include = KnowledgeResultInclude.MatchedChunks
            }, cancellationToken);
            var hit = Assert.Single(smart, result => result.DocumentId == documentId);
            Assert.Contains(hit.Matches, match => !string.IsNullOrWhiteSpace(match.Text));
        }
        finally
        {
            Delete(sourcePath);
            Delete(targetPath);
        }
    }

    private static ServiceProvider Build(string path, string fingerprint)
    {
        var services = new ServiceCollection();
        services.AddSemanticKnowledge(options => options.Embeddings.OutputDimensions = 4)
            .UseSqlite(path);
        services.AddSingleton<IKnowledgeEmbeddingProvider>(new TestEmbeddingProvider(fingerprint));
        return services.BuildServiceProvider();
    }

    private static string TempPath(string suffix) => Path.Combine(Path.GetTempPath(), $"semantic-knowledge-archive-{suffix}-{Guid.NewGuid():N}.db");
    private static void Delete(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            try { File.Delete(path + suffix); } catch { }
    }
}
