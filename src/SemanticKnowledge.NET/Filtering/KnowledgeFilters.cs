namespace SemanticKnowledge;

public enum KnowledgeFilterOperator { Equal = 1, NotEqual = 2, LessThan = 3, LessThanOrEqual = 4, GreaterThan = 5, GreaterThanOrEqual = 6, In = 7, IsNull = 8, IsNotNull = 9, TagContains = 10 }

public abstract record KnowledgeFilter;
public sealed record KnowledgeComparisonFilter(string FieldKey, KnowledgeFilterOperator Operator, KnowledgeValue? Value = null, IReadOnlyList<KnowledgeValue>? Values = null) : KnowledgeFilter;
public sealed record KnowledgeAndFilter(IReadOnlyList<KnowledgeFilter> Filters) : KnowledgeFilter;
public sealed record KnowledgeOrFilter(IReadOnlyList<KnowledgeFilter> Filters) : KnowledgeFilter;
public sealed record KnowledgeNotFilter(KnowledgeFilter Filter) : KnowledgeFilter;

public static class KnowledgeFilters
{
    public static KnowledgeFilter Eq(string field, KnowledgeValue value) => new KnowledgeComparisonFilter(Normalize(field), KnowledgeFilterOperator.Equal, value);
    public static KnowledgeFilter Ne(string field, KnowledgeValue value) => new KnowledgeComparisonFilter(Normalize(field), KnowledgeFilterOperator.NotEqual, value);
    public static KnowledgeFilter Gt(string field, KnowledgeValue value) => new KnowledgeComparisonFilter(Normalize(field), KnowledgeFilterOperator.GreaterThan, value);
    public static KnowledgeFilter Gte(string field, KnowledgeValue value) => new KnowledgeComparisonFilter(Normalize(field), KnowledgeFilterOperator.GreaterThanOrEqual, value);
    public static KnowledgeFilter Lt(string field, KnowledgeValue value) => new KnowledgeComparisonFilter(Normalize(field), KnowledgeFilterOperator.LessThan, value);
    public static KnowledgeFilter Lte(string field, KnowledgeValue value) => new KnowledgeComparisonFilter(Normalize(field), KnowledgeFilterOperator.LessThanOrEqual, value);
    public static KnowledgeFilter IsNull(string field) => new KnowledgeComparisonFilter(Normalize(field), KnowledgeFilterOperator.IsNull);
    public static KnowledgeFilter IsNotNull(string field) => new KnowledgeComparisonFilter(Normalize(field), KnowledgeFilterOperator.IsNotNull);
    public static KnowledgeFilter In(string field, params KnowledgeValue[] values) => new KnowledgeComparisonFilter(Normalize(field), KnowledgeFilterOperator.In, Values: values);
    public static KnowledgeFilter HasTag(string tag) => new KnowledgeComparisonFilter(KnowledgeSystemFields.Tags, KnowledgeFilterOperator.TagContains, KnowledgeValue.From(tag));
    public static KnowledgeFilter And(params KnowledgeFilter[] filters) => new KnowledgeAndFilter(filters);
    public static KnowledgeFilter Or(params KnowledgeFilter[] filters) => new KnowledgeOrFilter(filters);
    public static KnowledgeFilter Not(KnowledgeFilter filter) => new KnowledgeNotFilter(filter);
    private static string Normalize(string field) => KnowledgeSchemaBuilder.NormalizeKey(field);
}
