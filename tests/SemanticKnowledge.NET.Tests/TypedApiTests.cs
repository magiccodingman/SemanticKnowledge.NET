namespace SemanticKnowledge.Tests;

public sealed class TypedApiTests
{
    [Fact]
    public void Typed_schema_uses_member_names_without_compiling_expressions()
    {
        var schema = new TypedKnowledgeSchemaBuilder<Npc>("npc")
            .Semantic(x => x.Title, 30)
            .Semantic(x => x.Description, 20)
            .Semantic(x => x.Tags, 20)
            .Semantic(x => x.Biography, 30, SemanticMode.Chunked)
            .Filterable(x => x.Level)
            .Filterable(x => x.IntroducedAt)
            .Build();

        Assert.Equal(100, schema.Fields.Where(field => field.SemanticMode != SemanticMode.None).Sum(field => field.SemanticWeightPercent));
        Assert.Equal(SemanticMode.Chunked, schema.GetField("biography").SemanticMode);
        Assert.True(schema.GetField("level").Filterable);
        Assert.Equal(KnowledgeFieldType.Int64, schema.GetField("level").Type);
    }

    [Fact]
    public void Typed_filter_converts_supported_expression_tree_to_filter_ast()
    {
        var minimumLevel = 10;
        var filter = KnowledgeFilterExpression.Parse<Npc>(npc => npc.Level >= minimumLevel && npc.Faction == "Red Wizards");

        var and = Assert.IsType<KnowledgeAndFilter>(filter);
        Assert.Equal(2, and.Filters.Count);
        Assert.Contains(and.Filters, node => node is KnowledgeComparisonFilter { FieldKey: "level", Operator: KnowledgeFilterOperator.GreaterThanOrEqual });
        Assert.Contains(and.Filters, node => node is KnowledgeComparisonFilter { FieldKey: "faction", Operator: KnowledgeFilterOperator.Equal });
    }

    [Fact]
    public void Typed_filter_rejects_method_execution()
    {
        Assert.Throws<NotSupportedException>(() => KnowledgeFilterExpression.Parse<Npc>(npc => npc.Level > GetMinimumLevel()));
    }

    private static int GetMinimumLevel() => 10;

    private sealed class Npc : KnowledgeDocument
    {
        public string Biography { get; init; } = string.Empty;
        public string Faction { get; init; } = string.Empty;
        public int Level { get; init; }
        public DateTimeOffset IntroducedAt { get; init; }
    }
}
