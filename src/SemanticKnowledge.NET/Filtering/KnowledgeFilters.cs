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
    public static KnowledgeFilter? CombineAnd(KnowledgeFilter? left, KnowledgeFilter? right)
    {
        if (left is null) return right;
        if (right is null) return left;
        return left is KnowledgeAndFilter existing ? new KnowledgeAndFilter(existing.Filters.Append(right).ToArray()) : And(left, right);
    }
    private static string Normalize(string field) => KnowledgeSchemaBuilder.NormalizeKey(field);
}

internal static class KnowledgeFilterEvaluator
{
    public static bool Matches(KnowledgeFilter? filter, KnowledgeDocumentRecord document) => filter is null || Evaluate(filter, document);

    private static bool Evaluate(KnowledgeFilter filter, KnowledgeDocumentRecord document) => filter switch
    {
        KnowledgeAndFilter and => and.Filters.All(item => Evaluate(item, document)),
        KnowledgeOrFilter or => or.Filters.Any(item => Evaluate(item, document)),
        KnowledgeNotFilter not => !Evaluate(not.Filter, document),
        KnowledgeComparisonFilter comparison => Compare(comparison, document),
        _ => throw new NotSupportedException($"Unsupported filter type {filter.GetType().Name}.")
    };

    private static bool Compare(KnowledgeComparisonFilter filter, KnowledgeDocumentRecord document)
    {
        if (filter.Operator == KnowledgeFilterOperator.TagContains)
        {
            var expected = filter.Value?.Text ?? throw new InvalidOperationException("TagContains requires a text value.");
            return document.Tags.Contains(expected, StringComparer.OrdinalIgnoreCase);
        }

        var actual = Resolve(filter.FieldKey, document);
        if (filter.Operator == KnowledgeFilterOperator.IsNull) return actual is null;
        if (filter.Operator == KnowledgeFilterOperator.IsNotNull) return actual is not null;
        if (filter.Operator == KnowledgeFilterOperator.In)
        {
            var values = filter.Values ?? throw new InvalidOperationException("IN requires values.");
            return values.Any(value => Equal(actual, value));
        }
        var expectedValue = filter.Value ?? throw new InvalidOperationException($"Filter {filter.Operator} requires a value.");
        if (filter.Operator == KnowledgeFilterOperator.Equal) return Equal(actual, expectedValue);
        if (filter.Operator == KnowledgeFilterOperator.NotEqual) return !Equal(actual, expectedValue);
        if (actual is null || actual.Value.Type != expectedValue.Type) return false;
        var comparison = CompareValues(actual.Value, expectedValue);
        return filter.Operator switch
        {
            KnowledgeFilterOperator.LessThan => comparison < 0,
            KnowledgeFilterOperator.LessThanOrEqual => comparison <= 0,
            KnowledgeFilterOperator.GreaterThan => comparison > 0,
            KnowledgeFilterOperator.GreaterThanOrEqual => comparison >= 0,
            _ => throw new NotSupportedException($"Unsupported comparison operator {filter.Operator}.")
        };
    }

    private static KnowledgeValue? Resolve(string field, KnowledgeDocumentRecord document) => field switch
    {
        KnowledgeSystemFields.Title => KnowledgeValue.From(document.Title),
        KnowledgeSystemFields.Description => KnowledgeValue.From(document.Description),
        KnowledgeSystemFields.Tags => null,
        _ => document.Values.TryGetValue(field, out var value) ? value : null
    };

    private static bool Equal(KnowledgeValue? actual, KnowledgeValue expected)
    {
        if (actual is null) return false;
        if (actual.Value.Type != expected.Type) return false;
        return actual.Value.Type == KnowledgeFieldType.Text
            ? string.Equals(actual.Value.Text, expected.Text, StringComparison.OrdinalIgnoreCase)
            : Equals(actual.Value.ToObject(), expected.ToObject());
    }

    private static int CompareValues(KnowledgeValue left, KnowledgeValue right) => left.Type switch
    {
        KnowledgeFieldType.Text => string.Compare(left.Text, right.Text, StringComparison.OrdinalIgnoreCase),
        KnowledgeFieldType.Int64 => Nullable.Compare(left.Int64, right.Int64),
        KnowledgeFieldType.Decimal => Nullable.Compare(left.Decimal, right.Decimal),
        KnowledgeFieldType.Boolean => Nullable.Compare(left.Boolean, right.Boolean),
        KnowledgeFieldType.DateTimeOffset => Nullable.Compare(left.DateTimeOffset, right.DateTimeOffset),
        KnowledgeFieldType.Guid => Nullable.Compare(left.Guid, right.Guid),
        _ => throw new NotSupportedException($"Unsupported comparison type {left.Type}.")
    };
}
