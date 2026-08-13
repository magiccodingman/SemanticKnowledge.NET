namespace SemanticKnowledge.Tests;

public sealed class AdvancedSearchContractTests
{
    [Fact]
    public void Search_fields_normalize_keys_and_require_positive_weights()
    {
        var field = new KnowledgeSearchField(" Body ", 2f);
        Assert.Equal(KnowledgeSystemFields.Body, field.FieldKey);
        Assert.Equal(2f, field.Weight);

        Assert.Throws<ArgumentOutOfRangeException>(() => new KnowledgeSearchField("body", 0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new KnowledgeSearchField("body", -1f));
    }

    [Fact]
    public void Query_rejects_all_disabled_stages_and_zero_weight_fields_created_with_with_expression()
    {
        var query = KnowledgeSearchQuery.Create(Guid.NewGuid(), "backup")
            .Add(KnowledgeRetrievalStage.Semantic("semantic").WithWeight(0f))
            .Add(KnowledgeRetrievalStage.Lexical("lexical").WithWeight(0f));

        Assert.Throws<InvalidOperationException>(query.Validate);

        var invalidField = KnowledgeSearchField.Body() with { Weight = 0f };
        var invalidQuery = KnowledgeSearchQuery.Create(Guid.NewGuid(), "backup")
            .Lexical(invalidField);

        Assert.Throws<InvalidOperationException>(invalidQuery.Validate);
    }
}
