using System.Text.Json;
using System.Text.Json.Serialization;
using OnnxTextEmbeddings;

namespace SemanticKnowledge;

public enum KnowledgeCollectionSnapshotStatus
{
    None = 0,
    Staging = 1,
    Active = 2
}

/// <summary>Durable publication state for one logical Collection.</summary>
public sealed record KnowledgeCollectionSnapshotState
{
    public required Guid CollectionId { get; init; }
    public Guid? ActiveSnapshotId { get; init; }
    public string? SourceRevision { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public Guid? StagingSnapshotId { get; init; }
    public string? StagingSourceRevision { get; init; }
    public DateTimeOffset? StagingStartedAt { get; init; }
    public KnowledgeCollectionSnapshotStatus Status => StagingSnapshotId is not null
        ? KnowledgeCollectionSnapshotStatus.Staging
        : ActiveSnapshotId is not null
            ? KnowledgeCollectionSnapshotStatus.Active
            : KnowledgeCollectionSnapshotStatus.None;
}

/// <summary>Opaque durable handle for a Collection snapshot being prepared.</summary>
public sealed record KnowledgeCollectionSnapshotHandle
{
    public required Guid SnapshotId { get; init; }
    public required Guid KnowledgeBaseId { get; init; }
    public required Guid CollectionId { get; init; }
    public required Guid SchemaId { get; init; }
    public required string SourceRevision { get; init; }
}

/// <summary>Provider-ready canonical document plus all semantic/lexical derived records needed at publication.</summary>
public sealed record KnowledgePreparedSnapshotDocument
{
    public required KnowledgeDocumentRecord Document { get; init; }
    public IReadOnlyList<SemanticSourceRecord> SemanticSources { get; init; } = Array.Empty<SemanticSourceRecord>();
    public IReadOnlyList<LexicalSourceRecord> LexicalSources { get; init; } = Array.Empty<LexicalSourceRecord>();
}

/// <summary>
/// Storage-provider SPI for preparing an entire Collection off to the side and atomically publishing it.
/// Staging must not affect normal queries. Publish must make canonical data and derived search data visible
/// as one coherent Collection revision.
/// </summary>
public interface IKnowledgeCollectionSnapshotProvider
{
    Task<KnowledgeCollectionSnapshotState?> GetCollectionSnapshotStateAsync(Guid collectionId, CancellationToken cancellationToken = default);
    Task<KnowledgeCollectionSnapshotHandle> BeginCollectionSnapshotAsync(Guid knowledgeBaseId, Guid collectionId, Guid schemaId, string sourceRevision, CancellationToken cancellationToken = default);
    Task StageCollectionSnapshotDocumentAsync(KnowledgeCollectionSnapshotHandle snapshot, KnowledgePreparedSnapshotDocument document, CancellationToken cancellationToken = default);
    Task StageExistingCollectionSnapshotDocumentAsync(KnowledgeCollectionSnapshotHandle snapshot, Guid documentId, CancellationToken cancellationToken = default);
    Task<KnowledgeCollectionSnapshotState> PublishCollectionSnapshotAsync(KnowledgeCollectionSnapshotHandle snapshot, CancellationToken cancellationToken = default);
    Task AbortCollectionSnapshotAsync(KnowledgeCollectionSnapshotHandle snapshot, CancellationToken cancellationToken = default);
}

/// <summary>Optional provider hook used to recover durable staging left behind by a terminated synchronization process.</summary>
public interface IKnowledgeCollectionSnapshotRecoveryProvider
{
    Task DiscardStagedCollectionSnapshotAsync(Guid collectionId, Guid? expectedSnapshotId = null, CancellationToken cancellationToken = default);
}

/// <summary>Optional provider hook invoked before the base store is destructively reset.</summary>
public interface IKnowledgeCollectionSnapshotResetter
{
    Task ResetAsync(CancellationToken cancellationToken = default);
}

/// <summary>Shared constants used by provider snapshot implementations.</summary>
public static class KnowledgeCollectionSnapshotStorage
{
    // Normal search only recognizes Document=1 and Collection=2. Providers stage native lexical rows under this
    // deliberately non-public kind so full-text engines can index the text before atomic publication.
    public const int PendingLexicalEntityKind = 1001;
}

/// <summary>AOT-safe transport used by providers to persist changed/new staged documents.</summary>
public static class KnowledgeSnapshotPayloadSerializer
{
    public static string Serialize(KnowledgePreparedSnapshotDocument prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        var dto = new SnapshotDocumentDto
        {
            Id = prepared.Document.Id,
            ExternalId = prepared.Document.ExternalId,
            KnowledgeBaseId = prepared.Document.KnowledgeBaseId,
            CollectionId = prepared.Document.CollectionId,
            SchemaId = prepared.Document.SchemaId,
            Title = prepared.Document.Title,
            Description = prepared.Document.Description,
            Tags = prepared.Document.Tags.ToArray(),
            Values = prepared.Document.Values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
            SourceHash = prepared.Document.SourceHash,
            SemanticSources = prepared.SemanticSources.Select(source => new SnapshotSemanticSourceDto
            {
                Id = source.Id,
                KnowledgeBaseId = source.KnowledgeBaseId,
                CollectionId = source.CollectionId,
                ItemId = source.ItemId,
                EntityKind = source.EntityKind,
                DocumentId = source.DocumentId,
                FieldId = source.FieldId,
                FieldKey = source.FieldKey,
                ScorerWeight = source.ScorerWeight,
                EmbeddingJson = EmbeddingSerializer.SerializeJson(source.Embedding)
            }).ToArray()
        };
        return JsonSerializer.Serialize(dto, KnowledgeSnapshotJsonContext.Default.SnapshotDocumentDto);
    }

    public static KnowledgePreparedSnapshotDocument Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var dto = JsonSerializer.Deserialize(json, KnowledgeSnapshotJsonContext.Default.SnapshotDocumentDto)
            ?? throw new InvalidOperationException("Snapshot payload contained no document.");
        var document = new KnowledgeDocumentRecord
        {
            Id = dto.Id,
            ExternalId = dto.ExternalId,
            KnowledgeBaseId = dto.KnowledgeBaseId,
            CollectionId = dto.CollectionId,
            SchemaId = dto.SchemaId,
            Title = dto.Title,
            Description = dto.Description,
            Tags = dto.Tags,
            Values = dto.Values,
            SourceHash = dto.SourceHash
        };
        var semantic = dto.SemanticSources.Select(source => new SemanticSourceRecord
        {
            Id = source.Id,
            KnowledgeBaseId = source.KnowledgeBaseId,
            CollectionId = source.CollectionId,
            ItemId = source.ItemId,
            EntityKind = source.EntityKind,
            DocumentId = source.DocumentId,
            FieldId = source.FieldId,
            FieldKey = source.FieldKey,
            ScorerWeight = source.ScorerWeight,
            Embedding = EmbeddingSerializer.DeserializeJson(source.EmbeddingJson)
        }).ToArray();
        return new KnowledgePreparedSnapshotDocument { Document = document, SemanticSources = semantic };
    }

    internal sealed record SnapshotDocumentDto
    {
        public required Guid Id { get; init; }
        public string? ExternalId { get; init; }
        public required Guid KnowledgeBaseId { get; init; }
        public required Guid CollectionId { get; init; }
        public required Guid SchemaId { get; init; }
        public required string Title { get; init; }
        public string Description { get; init; } = string.Empty;
        public string[] Tags { get; init; } = Array.Empty<string>();
        public Dictionary<string, KnowledgeValue> Values { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public string? SourceHash { get; init; }
        public SnapshotSemanticSourceDto[] SemanticSources { get; init; } = Array.Empty<SnapshotSemanticSourceDto>();
    }

    internal sealed record SnapshotSemanticSourceDto
    {
        public required Guid Id { get; init; }
        public required Guid KnowledgeBaseId { get; init; }
        public required Guid CollectionId { get; init; }
        public required Guid ItemId { get; init; }
        public required SemanticEntityKind EntityKind { get; init; }
        public Guid? DocumentId { get; init; }
        public required Guid FieldId { get; init; }
        public required string FieldKey { get; init; }
        public required float ScorerWeight { get; init; }
        public required string EmbeddingJson { get; init; }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(KnowledgeSnapshotPayloadSerializer.SnapshotDocumentDto))]
internal partial class KnowledgeSnapshotJsonContext : JsonSerializerContext;
