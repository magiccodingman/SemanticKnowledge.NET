using System.Text.Json.Serialization;

namespace SemanticKnowledge;

public sealed record KnowledgeArchiveExportResult(Guid KnowledgeBaseId, int SchemaCount, int CollectionCount, int DocumentCount);
public sealed record KnowledgeArchiveImportResult(Guid KnowledgeBaseId, int SchemaCount, int CollectionCount, int DocumentCount);
public sealed record KnowledgeArchiveManifest(int FormatVersion, DateTimeOffset ExportedAtUtc, int LogicalDatabaseVersion, string SourceProvider, Guid KnowledgeBaseId, int SchemaCount, int CollectionCount, int DocumentCount, bool EmbeddingsIncluded);
public sealed record ArchiveEmbeddingProfile(string Provider, string ModelId, string SourceRevision, string EmbeddingSpaceFingerprint, int NativeDimensions, int OutputDimensions);
public sealed record ArchiveKnowledgeBase(Guid Id, string? ExternalId, string Title, string Description, string[] Tags)
{
    internal static ArchiveKnowledgeBase From(KnowledgeBaseRecord value) => new(value.Id, value.ExternalId, value.Title, value.Description, value.Tags.ToArray());
    internal KnowledgeBaseRecord ToRecord() => new() { Id = Id, ExternalId = ExternalId, Title = Title, Description = Description, Tags = Tags };
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
