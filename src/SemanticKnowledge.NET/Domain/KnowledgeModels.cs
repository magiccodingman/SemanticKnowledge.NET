namespace SemanticKnowledge;

public enum KnowledgeFieldType { Text = 1, Int64 = 2, Decimal = 3, Boolean = 4, DateTimeOffset = 5, Guid = 6 }
public enum SemanticMode { None = 0, Whole = 1, Chunked = 2 }

public sealed record KnowledgeBaseRecord
{
    public required Guid Id { get; init; }
    public string? ExternalId { get; init; }
    public required string Title { get; init; }
    public string Description { get; init; } = string.Empty;
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
}

public sealed record KnowledgeCollectionRecord
{
    public required Guid Id { get; init; }
    public required Guid KnowledgeBaseId { get; init; }
    public Guid? ParentCollectionId { get; init; }
    public Guid? DefaultSchemaId { get; init; }
    public string? ExternalId { get; init; }
    public required string Title { get; init; }
    public string Description { get; init; } = string.Empty;
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
}

public readonly record struct KnowledgeValue
{
    public required KnowledgeFieldType Type { get; init; }
    public string? Text { get; init; }
    public long? Int64 { get; init; }
    public decimal? Decimal { get; init; }
    public bool? Boolean { get; init; }
    public DateTimeOffset? DateTimeOffset { get; init; }
    public Guid? Guid { get; init; }

    public static KnowledgeValue From(string? value) => new() { Type = KnowledgeFieldType.Text, Text = value };
    public static KnowledgeValue From(long value) => new() { Type = KnowledgeFieldType.Int64, Int64 = value };
    public static KnowledgeValue From(int value) => From((long)value);
    public static KnowledgeValue From(decimal value) => new() { Type = KnowledgeFieldType.Decimal, Decimal = value };
    public static KnowledgeValue From(bool value) => new() { Type = KnowledgeFieldType.Boolean, Boolean = value };
    public static KnowledgeValue From(DateTimeOffset value) => new() { Type = KnowledgeFieldType.DateTimeOffset, DateTimeOffset = value };
    public static KnowledgeValue From(Guid value) => new() { Type = KnowledgeFieldType.Guid, Guid = value };

    public object? ToObject() => Type switch
    {
        KnowledgeFieldType.Text => Text, KnowledgeFieldType.Int64 => Int64, KnowledgeFieldType.Decimal => Decimal,
        KnowledgeFieldType.Boolean => Boolean, KnowledgeFieldType.DateTimeOffset => DateTimeOffset, KnowledgeFieldType.Guid => Guid,
        _ => throw new InvalidOperationException($"Unsupported knowledge value type {Type}.")
    };
    public string? ToSemanticText() => Type == KnowledgeFieldType.Text ? Text : null;
}

public sealed record KnowledgeDocumentInput
{
    public Guid? Id { get; init; }
    public string? ExternalId { get; init; }
    public required Guid KnowledgeBaseId { get; init; }
    public required Guid CollectionId { get; init; }
    public required Guid SchemaId { get; init; }
    public required string Title { get; init; }
    public string Description { get; init; } = string.Empty;
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, KnowledgeValue> Values { get; init; } = new Dictionary<string, KnowledgeValue>();
}

public sealed record KnowledgeDocumentRecord
{
    public required Guid Id { get; init; }
    public string? ExternalId { get; init; }
    public required Guid KnowledgeBaseId { get; init; }
    public required Guid CollectionId { get; init; }
    public required Guid SchemaId { get; init; }
    public required string Title { get; init; }
    public string Description { get; init; } = string.Empty;
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, KnowledgeValue> Values { get; init; } = new Dictionary<string, KnowledgeValue>();
    public string? SourceHash { get; init; }
}
