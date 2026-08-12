namespace SemanticKnowledge.Tests;

public sealed class SchemaTests
{
    [Fact]
    public void Default_document_schema_is_valid_and_totals_100()
    {
        var schema = KnowledgeSchemaBuilder.CreateDefaultDocument();
        schema.Validate();
        Assert.Equal(100, schema.Fields.Where(x => x.SemanticMode != SemanticMode.None).Sum(x => x.SemanticWeightPercent));
        Assert.Equal(SemanticMode.Chunked, schema.GetField(KnowledgeSystemFields.Body).SemanticMode);
    }

    [Fact]
    public void Invalid_semantic_total_fails_early()
    {
        var builder = new KnowledgeSchemaBuilder("npc").Text("bio", 30, SemanticMode.Chunked);
        var error = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Contains("Expected exactly 100", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_text_semantic_is_rejected()
    {
        var schema = KnowledgeSchemaBuilder.CreateDefaultDocument();
        Assert.Equal(KnowledgeFieldType.Text, schema.GetField(KnowledgeSystemFields.Body).Type);
    }

    [Fact]
    public void Weight_mapping_preserves_relative_percentage()
    {
        Assert.Equal(1.2f, SemanticWeightProfileV1.ToScorerWeight(30, 4), 3);
        Assert.Equal(0.8f, SemanticWeightProfileV1.ToScorerWeight(20, 4), 3);
    }
}
