namespace SemanticKnowledge;

public static class KnowledgeSystemFields
{
    public const string Title = "title";
    public const string Description = "description";
    public const string Tags = "tags";
    public const string Body = "body";
}

public sealed record KnowledgeSchemaField
{
    public required Guid Id { get; init; }
    public required string Key { get; init; }
    public required string DisplayName { get; init; }
    public required KnowledgeFieldType Type { get; init; }
    public bool Required { get; init; }
    public bool System { get; init; }
    public bool Filterable { get; init; }
    public SemanticMode SemanticMode { get; init; }
    public int SemanticWeightPercent { get; init; }
}

public sealed record KnowledgeSchemaDefinition
{
    public required Guid Id { get; init; }
    public required string Key { get; init; }
    public required string DisplayName { get; init; }
    public int Revision { get; init; } = 1;
    public required IList<KnowledgeSchemaField> Fields { get; init; }

    public KnowledgeSchemaField GetField(string key) => Fields.FirstOrDefault(field => string.Equals(field.Key, key, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"Schema '{Key}' has no field '{key}'.");

    public void Validate()
    {
        if (Id == System.Guid.Empty) throw new InvalidOperationException("Schema Id cannot be empty.");
        if (string.IsNullOrWhiteSpace(Key)) throw new InvalidOperationException("Schema Key is required.");
        if (Revision <= 0 || Fields.Count == 0) throw new InvalidOperationException("Schema revision and fields are required.");
        var duplicate = Fields.GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase).FirstOrDefault(x => x.Count() > 1);
        if (duplicate is not null) throw new InvalidOperationException($"Schema field key '{duplicate.Key}' is duplicated.");
        foreach (var requiredSystem in new[] { KnowledgeSystemFields.Title, KnowledgeSystemFields.Description, KnowledgeSystemFields.Tags })
        {
            var field = Fields.FirstOrDefault(x => string.Equals(x.Key, requiredSystem, StringComparison.OrdinalIgnoreCase));
            if (field is null || !field.System) throw new InvalidOperationException($"Schema must contain protected system field '{requiredSystem}'.");
        }
        foreach (var field in Fields)
        {
            if (field.Id == System.Guid.Empty) throw new InvalidOperationException($"Field '{field.Key}' has an empty Id.");
            if (field.SemanticWeightPercent is < 0 or > 100) throw new InvalidOperationException($"Semantic weight for '{field.Key}' must be between 0 and 100.");
            if (field.SemanticMode != SemanticMode.None && field.Type != KnowledgeFieldType.Text) throw new InvalidOperationException($"Only Text fields may be semantic in v1. Field '{field.Key}' is {field.Type}.");
            if (field.SemanticMode == SemanticMode.None && field.SemanticWeightPercent != 0) throw new InvalidOperationException($"Nonsemantic field '{field.Key}' must have weight 0.");
        }
        var semantic = Fields.Where(x => x.SemanticMode != SemanticMode.None).ToArray();
        if (semantic.Length == 0) throw new InvalidOperationException("A schema must contain at least one semantic field.");
        var total = semantic.Sum(x => x.SemanticWeightPercent);
        if (total != 100) throw new InvalidOperationException($"Semantic field weights total {total}. Expected exactly 100.");
    }
}

public sealed class KnowledgeSchemaBuilder
{
    private readonly Guid _id;
    private readonly string _key;
    private readonly string _displayName;
    private readonly List<KnowledgeSchemaField> _fields = [];

    public KnowledgeSchemaBuilder(string key, string? displayName = null, Guid? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _id = id ?? System.Guid.NewGuid(); _key = NormalizeKey(key); _displayName = displayName ?? key;
        _fields.Add(Field(KnowledgeSystemFields.Title, "Title", KnowledgeFieldType.Text, true, true, true, SemanticMode.Whole, 35));
        _fields.Add(Field(KnowledgeSystemFields.Description, "Description", KnowledgeFieldType.Text, false, true, true, SemanticMode.Whole, 25));
        _fields.Add(Field(KnowledgeSystemFields.Tags, "Tags", KnowledgeFieldType.Text, false, true, true, SemanticMode.Whole, 20));
    }

    public KnowledgeSchemaBuilder SetSemanticWeight(string key, int percent)
    {
        var index = Find(key); var field = _fields[index];
        _fields[index] = field with { SemanticWeightPercent = percent, SemanticMode = percent == 0 ? SemanticMode.None : field.SemanticMode == SemanticMode.None ? SemanticMode.Whole : field.SemanticMode };
        return this;
    }
    public KnowledgeSchemaBuilder Text(string key, int semanticWeightPercent = 0, SemanticMode semanticMode = SemanticMode.None, bool required = false, bool filterable = false, string? displayName = null) { AddCustom(key, KnowledgeFieldType.Text, required, filterable, semanticMode, semanticWeightPercent, displayName); return this; }
    public KnowledgeSchemaBuilder Int64(string key, bool required = false, bool filterable = true, string? displayName = null) { AddCustom(key, KnowledgeFieldType.Int64, required, filterable, SemanticMode.None, 0, displayName); return this; }
    public KnowledgeSchemaBuilder Decimal(string key, bool required = false, bool filterable = true, string? displayName = null) { AddCustom(key, KnowledgeFieldType.Decimal, required, filterable, SemanticMode.None, 0, displayName); return this; }
    public KnowledgeSchemaBuilder Boolean(string key, bool required = false, bool filterable = true, string? displayName = null) { AddCustom(key, KnowledgeFieldType.Boolean, required, filterable, SemanticMode.None, 0, displayName); return this; }
    public KnowledgeSchemaBuilder DateTimeOffset(string key, bool required = false, bool filterable = true, string? displayName = null) { AddCustom(key, KnowledgeFieldType.DateTimeOffset, required, filterable, SemanticMode.None, 0, displayName); return this; }
    public KnowledgeSchemaBuilder GuidField(string key, bool required = false, bool filterable = true, string? displayName = null) { AddCustom(key, KnowledgeFieldType.Guid, required, filterable, SemanticMode.None, 0, displayName); return this; }

    public KnowledgeSchemaDefinition Build(int revision = 1)
    {
        var result = new KnowledgeSchemaDefinition { Id = _id, Key = _key, DisplayName = _displayName, Revision = revision, Fields = _fields.ToArray() };
        result.Validate(); return result;
    }

    public static KnowledgeSchemaDefinition CreateDefaultDocument(string key = "document", Guid? id = null) => new KnowledgeSchemaBuilder(key, "Document", id)
        .SetSemanticWeight(KnowledgeSystemFields.Title, 30).SetSemanticWeight(KnowledgeSystemFields.Description, 20).SetSemanticWeight(KnowledgeSystemFields.Tags, 20)
        .Text(KnowledgeSystemFields.Body, 30, SemanticMode.Chunked, displayName: "Body").Build();

    private void AddCustom(string key, KnowledgeFieldType type, bool required, bool filterable, SemanticMode mode, int weight, string? displayName)
    {
        var normalized = NormalizeKey(key); if (_fields.Any(x => string.Equals(x.Key, normalized, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException($"Schema field '{normalized}' already exists.");
        _fields.Add(Field(normalized, displayName ?? key, type, required, false, filterable, mode, weight));
    }
    private int Find(string key) { var index = _fields.FindIndex(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase)); return index >= 0 ? index : throw new KeyNotFoundException($"Schema field '{key}' was not found."); }
    private static KnowledgeSchemaField Field(string key, string displayName, KnowledgeFieldType type, bool required, bool system, bool filterable, SemanticMode mode, int weight) => new() { Id = System.Guid.NewGuid(), Key = NormalizeKey(key), DisplayName = displayName, Type = type, Required = required, System = system, Filterable = filterable, SemanticMode = mode, SemanticWeightPercent = weight };
    internal static string NormalizeKey(string key) => string.IsNullOrWhiteSpace(key) ? throw new ArgumentException("Field key is required.", nameof(key)) : key.Trim().ToLowerInvariant();
}

public static class SemanticWeightProfileV1
{
    public static float ToScorerWeight(int percentage, int enabledSemanticFieldCount)
    {
        if (percentage is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(percentage));
        if (enabledSemanticFieldCount <= 0) throw new ArgumentOutOfRangeException(nameof(enabledSemanticFieldCount));
        return percentage / 100f * enabledSemanticFieldCount;
    }
}
