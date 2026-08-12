using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace SemanticKnowledge;

/// <summary>Provider SPI used by the logical archive service for canonical catalog records and streaming document IDs.</summary>
public interface IKnowledgeArchiveStorage
{
    Task<KnowledgeBaseRecord?> GetKnowledgeBaseAsync(Guid knowledgeBaseId, CancellationToken cancellationToken = default);
    Task UpsertKnowledgeBaseAsync(KnowledgeBaseRecord knowledgeBase, CancellationToken cancellationToken = default);
    Task UpsertCollectionAsync(KnowledgeCollectionRecord collection, CancellationToken cancellationToken = default);
    IAsyncEnumerable<Guid> StreamDocumentIdsAsync(Guid knowledgeBaseId, CancellationToken cancellationToken = default);
}

public interface IKnowledgeArchiveService
{
    Task<KnowledgeArchiveExportResult> ExportKnowledgeBaseAsync(Guid knowledgeBaseId, Stream destination, CancellationToken cancellationToken = default);
    Task<KnowledgeArchiveImportResult> ImportAsync(Stream source, CancellationToken cancellationToken = default);
}

public sealed record KnowledgeArchiveExportResult(Guid KnowledgeBaseId, int SchemaCount, int CollectionCount, int DocumentCount);
public sealed record KnowledgeArchiveImportResult(Guid KnowledgeBaseId, int SchemaCount, int CollectionCount, int DocumentCount);

internal sealed class KnowledgeArchiveService(
    SemanticKnowledgeOptions options,
    ISemanticKnowledgeStore store,
    IKnowledgeStorageProvider storage,
    IKnowledgeArchiveStorage archiveStorage,
    IKnowledgeEmbeddingProvider embeddings) : IKnowledgeArchiveService
{
    private const int FormatVersion = 1;
    private static readonly Guid CollectionTitleFieldId = new("b6a961c4-71a2-41e8-9ab4-8cb51e223201");
    private static readonly Guid CollectionDescriptionFieldId = new("b6a961c4-71a2-41e8-9ab4-8cb51e223202");
    private static readonly Guid CollectionTagsFieldId = new("b6a961c4-71a2-41e8-9ab4-8cb51e223203");

    public async Task<KnowledgeArchiveExportResult> ExportKnowledgeBaseAsync(Guid knowledgeBaseId, Stream destination, CancellationToken cancellationToken = default)
    {
        if (knowledgeBaseId == Guid.Empty) throw new ArgumentException("KnowledgeBase ID cannot be empty.", nameof(knowledgeBaseId));
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new ArgumentException("Destination stream must be writable.", nameof(destination));

        var capabilities = await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var knowledgeBase = await archiveStorage.GetKnowledgeBaseAsync(knowledgeBaseId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"KnowledgeBase {knowledgeBaseId} was not found.");
        var collections = await storage.GetCollectionsAsync(knowledgeBaseId, cancellationToken).ConfigureAwait(false);
        var schemaIds = new HashSet<Guid>(collections.Where(collection => collection.DefaultSchemaId.HasValue).Select(collection => collection.DefaultSchemaId!.Value));

        // The archive is streamed at document granularity. We only keep canonical catalog metadata in memory.
        var documentIds = archiveStorage.StreamDocumentIdsAsync(knowledgeBaseId, cancellationToken);
        var profile = await embeddings.GetInfoAsync(cancellationToken).ConfigureAwait(false);
        var documentCount = 0;

        using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        await WriteJsonEntryAsync(zip, "knowledge-base.json", ArchiveKnowledgeBase.From(knowledgeBase), KnowledgeArchiveJsonContext.Default.ArchiveKnowledgeBase, cancellationToken).ConfigureAwait(false);
        await WriteJsonEntryAsync(zip, "collections.json", collections.Select(ArchiveCollection.From).ToArray(), KnowledgeArchiveJsonContext.Default.ArchiveCollectionArray, cancellationToken).ConfigureAwait(false);
        await WriteJsonEntryAsync(zip, "embedding-profile.json", new ArchiveEmbeddingProfile(profile.Provider, profile.ModelId, profile.SourceRevision, profile.EmbeddingSpaceFingerprint, profile.NativeDimensions, profile.OutputDimensions), KnowledgeArchiveJsonContext.Default.ArchiveEmbeddingProfile, cancellationToken).ConfigureAwait(false);

        var documentsEntry = zip.CreateEntry("documents.jsonl", CompressionLevel.Optimal);
        await using (var documentsStream = documentsEntry.Open())
        {
            await foreach (var documentId in documentIds.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                var document = await storage.GetDocumentAsync(documentId, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Document {documentId} disappeared while the archive was being exported.");
                schemaIds.Add(document.SchemaId);
                var payload = JsonSerializer.SerializeToUtf8Bytes(ArchiveDocument.From(document), KnowledgeArchiveJsonContext.Default.ArchiveDocument);
                await documentsStream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await documentsStream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
                documentCount++;
            }
        }

        var schemas = new List<ArchiveSchema>(schemaIds.Count);
        foreach (var schemaId in schemaIds.Order())
        {
            var schema = await storage.GetSchemaAsync(schemaId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Archive source references missing schema {schemaId}.");
            schemas.Add(ArchiveSchema.From(schema));
        }
        await WriteJsonEntryAsync(zip, "schemas.json", schemas.ToArray(), KnowledgeArchiveJsonContext.Default.ArchiveSchemaArray, cancellationToken).ConfigureAwait(false);

        var manifest = new KnowledgeArchiveManifest(
            FormatVersion,
            DateTimeOffset.UtcNow,
            options.DatabaseVersion,
            capabilities.Provider,
            knowledgeBaseId,
            schemas.Count,
            collections.Count,
            documentCount,
            EmbeddingsIncluded: false);
        await WriteJsonEntryAsync(zip, "manifest.json", manifest, KnowledgeArchiveJsonContext.Default.KnowledgeArchiveManifest, cancellationToken).ConfigureAwait(false);
        return new KnowledgeArchiveExportResult(knowledgeBaseId, schemas.Count, collections.Count, documentCount);
    }

    public async Task<KnowledgeArchiveImportResult> ImportAsync(Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead) throw new ArgumentException("Source stream must be readable.", nameof(source));
        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);

        using var zip = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        var manifest = await ReadJsonEntryAsync(zip, "manifest.json", KnowledgeArchiveJsonContext.Default.KnowledgeArchiveManifest, cancellationToken).ConfigureAwait(false);
        if (manifest.FormatVersion != FormatVersion)
            throw new InvalidDataException($"Unsupported SemanticKnowledge archive format {manifest.FormatVersion}. This version supports format {FormatVersion}.");
        if (manifest.EmbeddingsIncluded)
            throw new InvalidDataException("Archive format v1 imports canonical data only; embedded vector payloads are not accepted.");

        var archivedKnowledgeBase = await ReadJsonEntryAsync(zip, "knowledge-base.json", KnowledgeArchiveJsonContext.Default.ArchiveKnowledgeBase, cancellationToken).ConfigureAwait(false);
        if (archivedKnowledgeBase.Id != manifest.KnowledgeBaseId)
            throw new InvalidDataException("Archive manifest and KnowledgeBase IDs do not match.");
        await archiveStorage.UpsertKnowledgeBaseAsync(archivedKnowledgeBase.ToRecord(), cancellationToken).ConfigureAwait(false);

        var schemas = await ReadJsonEntryAsync(zip, "schemas.json", KnowledgeArchiveJsonContext.Default.ArchiveSchemaArray, cancellationToken).ConfigureAwait(false);
        foreach (var archivedSchema in schemas)
            await store.EnsureSchemaAsync(archivedSchema.ToDefinition(), cancellationToken).ConfigureAwait(false);

        var collections = await ReadJsonEntryAsync(zip, "collections.json", KnowledgeArchiveJsonContext.Default.ArchiveCollectionArray, cancellationToken).ConfigureAwait(false);
        await ImportCollectionsAsync(collections, manifest.KnowledgeBaseId, cancellationToken).ConfigureAwait(false);

        var documentsEntry = zip.GetEntry("documents.jsonl") ?? throw new InvalidDataException("Archive is missing documents.jsonl.");
        var documentCount = 0;
        await using (var documentStream = documentsEntry.Open())
        using (var reader = new StreamReader(documentStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 16 * 1024, leaveOpen: false))
        {
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var archivedDocument = JsonSerializer.Deserialize(line, KnowledgeArchiveJsonContext.Default.ArchiveDocument)
                    ?? throw new InvalidDataException($"documents.jsonl record {documentCount + 1} deserialized to null.");
                if (archivedDocument.KnowledgeBaseId != manifest.KnowledgeBaseId)
                    throw new InvalidDataException($"Document {archivedDocument.Id} belongs to a different KnowledgeBase than the archive manifest.");
                await store.UpsertDocumentAsync(archivedDocument.ToInput(), cancellationToken).ConfigureAwait(false);
                documentCount++;
            }
        }

        if (documentCount != manifest.DocumentCount)
            throw new InvalidDataException($"Archive manifest declares {manifest.DocumentCount} documents but {documentCount} were imported.");
        return new KnowledgeArchiveImportResult(manifest.KnowledgeBaseId, schemas.Length, collections.Length, documentCount);
    }

    private async Task ImportCollectionsAsync(ArchiveCollection[] collections, Guid knowledgeBaseId, CancellationToken cancellationToken)
    {
        var remaining = collections.ToDictionary(collection => collection.Id);
        var imported = new HashSet<Guid>();
        while (remaining.Count > 0)
        {
            var ready = remaining.Values
                .Where(collection => collection.KnowledgeBaseId == knowledgeBaseId && (collection.ParentCollectionId is null || imported.Contains(collection.ParentCollectionId.Value)))
                .OrderBy(collection => collection.ParentCollectionId.HasValue)
                .ThenBy(collection => collection.Title, StringComparer.Ordinal)
                .ToArray();
            if (ready.Length == 0)
                throw new InvalidDataException("Collection hierarchy contains a cycle, a missing parent, or a Collection from another KnowledgeBase.");

            foreach (var archivedCollection in ready)
            {
                var collection = archivedCollection.ToRecord();
                await archiveStorage.UpsertCollectionAsync(collection, cancellationToken).ConfigureAwait(false);
                await storage.UpsertCollectionSemanticSourcesAsync(collection, await BuildCollectionSourcesAsync(collection, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                remaining.Remove(collection.Id);
                imported.Add(collection.Id);
            }
        }
    }

    private async Task<IReadOnlyList<SemanticSourceRecord>> BuildCollectionSourcesAsync(KnowledgeCollectionRecord collection, CancellationToken cancellationToken)
    {
        var sources = new List<SemanticSourceRecord>();
        await AddCollectionSourceAsync(collection, CollectionTitleFieldId, KnowledgeSystemFields.Title, collection.Title, 1.35f, sources, cancellationToken).ConfigureAwait(false);
        await AddCollectionSourceAsync(collection, CollectionDescriptionFieldId, KnowledgeSystemFields.Description, collection.Description, 1f, sources, cancellationToken).ConfigureAwait(false);
        await AddCollectionSourceAsync(collection, CollectionTagsFieldId, KnowledgeSystemFields.Tags, string.Join("\n", collection.Tags), 1.15f, sources, cancellationToken).ConfigureAwait(false);
        return sources;
    }

    private async Task AddCollectionSourceAsync(KnowledgeCollectionRecord collection, Guid fieldId, string fieldKey, string? text, float weight, List<SemanticSourceRecord> destination, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        foreach (var embedding in await embeddings.EmbedDocumentAsync(text, cancellationToken).ConfigureAwait(false))
            destination.Add(new SemanticSourceRecord { Id = Guid.NewGuid(), KnowledgeBaseId = collection.KnowledgeBaseId, CollectionId = collection.Id, ItemId = collection.Id, EntityKind = SemanticEntityKind.Collection, FieldId = fieldId, FieldKey = fieldKey, ScorerWeight = weight, Embedding = embedding });
    }

    private static async Task WriteJsonEntryAsync<T>(ZipArchive zip, string name, T value, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await JsonSerializer.SerializeAsync(stream, value, typeInfo, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ReadJsonEntryAsync<T>(ZipArchive zip, string name, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        var entry = zip.GetEntry(name) ?? throw new InvalidDataException($"Archive is missing {name}.");
        await using var stream = entry.Open();
        return await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException($"Archive entry {name} deserialized to null.");
    }
}

public sealed record KnowledgeArchiveManifest(int FormatVersion, DateTimeOffset ExportedAtUtc, int LogicalDatabaseVersion, string SourceProvider, Guid KnowledgeBaseId, int SchemaCount, int CollectionCount, int DocumentCount, bool EmbeddingsIncluded);
public sealed record ArchiveEmbeddingProfile(string Provider, string ModelId, string SourceRevision, string EmbeddingSpaceFingerprint, int NativeDimensions, int OutputDimensions);
public sealed record ArchiveKnowledgeBase(Guid Id, string? ExternalId, string Title, string Description)
{
    internal static ArchiveKnowledgeBase From(KnowledgeBaseRecord value) => new(value.Id, value.ExternalId, value.Title, value.Description);
    internal KnowledgeBaseRecord ToRecord() => new() { Id = Id, ExternalId = ExternalId, Title = Title, Description = Description };
}
public sealed record ArchiveCollection(Guid Id, Guid KnowledgeBaseId, Guid? ParentCollectionId, Guid? DefaultSchemaId, string? ExternalId, string Title, string Description, string[] Tags)
{
    internal static ArchiveCollection From(KnowledgeCollectionRecord value) => new(value.Id, value.KnowledgeBaseId, value.ParentCollectionId, value.DefaultSchemaId, value.ExternalId, value.Title, value.Description, value.Tags.ToArray());
    internal KnowledgeCollectionRecord ToRecord() => new() { Id = Id, KnowledgeBaseId = KnowledgeBaseId, ParentCollectionId = ParentCollectionId, DefaultSchemaId = DefaultSchemaId, ExternalId = ExternalId, Title = Title, Description = Description, Tags = Tags };
}
public sealed record ArchiveSchema(Guid Id, string Key, string DisplayName, int Revision, ArchiveSchemaField[] Fields)
{
    internal static ArchiveSchema From(KnowledgeSchemaDefinition value) => new(value.Id, value.Key, value.DisplayName, value.Revision, value.Fields.Select(ArchiveSchemaField.From).ToArray());
    internal KnowledgeSchemaDefinition ToDefinition() => new() { Id = Id, Key = Key, DisplayName = DisplayName, Revision = Revision, Fields = Fields.Select(field => field.ToField()).ToArray() };
}
public sealed record ArchiveSchemaField(Guid Id, string Key, string DisplayName, KnowledgeFieldType Type, bool Required, bool System, bool Filterable, SemanticMode SemanticMode, int SemanticWeightPercent)
{
    internal static ArchiveSchemaField From(KnowledgeSchemaField value) => new(value.Id, value.Key, value.DisplayName, value.Type, value.Required, value.System, value.Filterable, value.SemanticMode, value.SemanticWeightPercent);
    internal KnowledgeSchemaField ToField() => new() { Id = Id, Key = Key, DisplayName = DisplayName, Type = Type, Required = Required, System = System, Filterable = Filterable, SemanticMode = SemanticMode, SemanticWeightPercent = SemanticWeightPercent };
}
public sealed record ArchiveDocument(Guid Id, string? ExternalId, Guid KnowledgeBaseId, Guid CollectionId, Guid SchemaId, string Title, string Description, string[] Tags, ArchiveValue[] Values)
{
    internal static ArchiveDocument From(KnowledgeDocumentRecord value) => new(value.Id, value.ExternalId, value.KnowledgeBaseId, value.CollectionId, value.SchemaId, value.Title, value.Description, value.Tags.ToArray(), value.Values.Select(pair => ArchiveValue.From(pair.Key, pair.Value)).ToArray());
    internal KnowledgeDocumentInput ToInput() => new() { Id = Id, ExternalId = ExternalId, KnowledgeBaseId = KnowledgeBaseId, CollectionId = CollectionId, SchemaId = SchemaId, Title = Title, Description = Description, Tags = Tags, Values = Values.ToDictionary(value => value.Key, value => value.ToKnowledgeValue(), StringComparer.OrdinalIgnoreCase) };
}
public sealed record ArchiveValue(string Key, KnowledgeFieldType Type, string? Text = null, long? Int64 = null, decimal? Decimal = null, bool? Boolean = null, DateTimeOffset? DateTimeOffset = null, Guid? Guid = null)
{
    internal static ArchiveValue From(string key, KnowledgeValue value) => new(key, value.Type, value.Text, value.Int64, value.Decimal, value.Boolean, value.DateTimeOffset, value.Guid);
    internal KnowledgeValue ToKnowledgeValue() => Type switch
    {
        KnowledgeFieldType.Text => KnowledgeValue.From(Text),
        KnowledgeFieldType.Int64 => KnowledgeValue.From(Int64 ?? throw new InvalidDataException($"Archive value '{Key}' is missing Int64 data.")),
        KnowledgeFieldType.Decimal => KnowledgeValue.From(Decimal ?? throw new InvalidDataException($"Archive value '{Key}' is missing Decimal data.")),
        KnowledgeFieldType.Boolean => KnowledgeValue.From(Boolean ?? throw new InvalidDataException($"Archive value '{Key}' is missing Boolean data.")),
        KnowledgeFieldType.DateTimeOffset => KnowledgeValue.From(DateTimeOffset ?? throw new InvalidDataException($"Archive value '{Key}' is missing DateTimeOffset data.")),
        KnowledgeFieldType.Guid => KnowledgeValue.From(Guid ?? throw new InvalidDataException($"Archive value '{Key}' is missing Guid data.")),
        _ => throw new InvalidDataException($"Archive value '{Key}' has unsupported type {Type}.")
    };
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(KnowledgeArchiveManifest))]
[JsonSerializable(typeof(ArchiveEmbeddingProfile))]
[JsonSerializable(typeof(ArchiveKnowledgeBase))]
[JsonSerializable(typeof(ArchiveCollection[]))]
[JsonSerializable(typeof(ArchiveSchema[]))]
[JsonSerializable(typeof(ArchiveDocument))]
internal partial class KnowledgeArchiveJsonContext : JsonSerializerContext;
