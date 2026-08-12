namespace SemanticKnowledge;

public static class BuiltInKnowledgeSchemas
{
    public static readonly Guid DocumentSchemaId = Guid.Parse("5dc39d3a-5607-5a31-a627-77cb327fe8a0");

    public static KnowledgeSchemaDefinition Document() =>
        KnowledgeSchemaBuilder.CreateDefaultDocument("document", DocumentSchemaId);
}

public static class SemanticKnowledgeConvenienceExtensions
{
    /// <summary>Ensures the built-in Title/Description/Tags/Body document schema exists and returns its stable definition.</summary>
    public static async Task<KnowledgeSchemaDefinition> EnsureBuiltInDocumentSchemaAsync(
        this ISemanticKnowledgeStore store,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var schema = BuiltInKnowledgeSchemas.Document();
        await store.EnsureSchemaAsync(schema, cancellationToken).ConfigureAwait(false);
        return schema;
    }

    /// <summary>Creates or resolves a recursive Collection path such as ["World", "NPCs", "Vallaki"].</summary>
    public static async Task<KnowledgeCollectionRecord> GetOrCreateCollectionPathAsync(
        this ISemanticKnowledgeStore store,
        Guid knowledgeBaseId,
        IEnumerable<string> path,
        Guid? defaultSchemaId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(path);
        KnowledgeCollectionRecord? current = null;
        foreach (var segment in path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(segment);
            current = await store.GetOrCreateCollectionAsync(
                knowledgeBaseId,
                segment,
                current?.Id,
                defaultSchemaId,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        return current ?? throw new ArgumentException("At least one Collection path segment is required.", nameof(path));
    }

    /// <summary>Stores a simple wiki-style document using the built-in schema.</summary>
    public static async Task<Guid> UpsertDocumentAsync(
        this ISemanticKnowledgeStore store,
        Guid knowledgeBaseId,
        Guid collectionId,
        string title,
        string body,
        string? description = null,
        IReadOnlyList<string>? tags = null,
        string? externalId = null,
        Guid? id = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(body);
        var schema = await store.EnsureBuiltInDocumentSchemaAsync(cancellationToken).ConfigureAwait(false);
        return await store.UpsertDocumentAsync(new KnowledgeDocumentInput
        {
            Id = id,
            ExternalId = externalId,
            KnowledgeBaseId = knowledgeBaseId,
            CollectionId = collectionId,
            SchemaId = schema.Id,
            Title = title,
            Description = description ?? string.Empty,
            Tags = tags ?? Array.Empty<string>(),
            Values = new Dictionary<string, KnowledgeValue>
            {
                [KnowledgeSystemFields.Body] = KnowledgeValue.From(body)
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public static Task<IReadOnlyList<KnowledgeSearchHit>> SearchCollectionAsync(
        this ISemanticKnowledgeStore store,
        string query,
        Guid knowledgeBaseId,
        Guid collectionId,
        int top = 10,
        bool includeDescendants = true,
        KnowledgeFilter? filter = null,
        CancellationToken cancellationToken = default)
        => store.SearchAsync(query, new KnowledgeSearchRequest
        {
            KnowledgeBaseId = knowledgeBaseId,
            Mode = KnowledgeSearchMode.Scoped,
            CollectionIds = [collectionId],
            IncludeDescendants = includeDescendants,
            Filter = filter,
            Top = top
        }, cancellationToken);
}
