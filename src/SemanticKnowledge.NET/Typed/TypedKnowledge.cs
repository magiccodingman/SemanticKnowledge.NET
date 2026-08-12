using System.Linq.Expressions;
using System.Reflection;

namespace SemanticKnowledge;

/// <summary>Convenience base model for strongly typed knowledge schemas.</summary>
public abstract class KnowledgeDocument
{
    public required string Title { get; init; }
    public string Description { get; init; } = string.Empty;
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Builds the same runtime schema records as <see cref="KnowledgeSchemaBuilder"/>, but derives stable field keys from
/// C# member expressions instead of caller-supplied strings. Expressions are inspected and are never compiled.
/// </summary>
public sealed class TypedKnowledgeSchemaBuilder<T> where T : KnowledgeDocument
{
    private readonly string _key;
    private readonly string _displayName;
    private readonly Guid? _id;
    private readonly Dictionary<string, TypedField> _fields = new(StringComparer.OrdinalIgnoreCase);
    private int _titleWeight = 35;
    private int _descriptionWeight = 25;
    private int _tagsWeight = 20;

    public TypedKnowledgeSchemaBuilder(string key, string? displayName = null, Guid? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _key = key;
        _displayName = displayName ?? key;
        _id = id;
    }

    public TypedKnowledgeSchemaBuilder<T> Semantic<TValue>(Expression<Func<T, TValue>> member, int weightPercent, SemanticMode mode = SemanticMode.Whole, bool required = false, bool filterable = false)
    {
        var key = MemberKey(member);
        if (IsSystem(key))
        {
            if (key == KnowledgeSystemFields.Tags && typeof(TValue) != typeof(IReadOnlyList<string>) && typeof(TValue) != typeof(string[]))
                throw new InvalidOperationException("The system Tags member must be a string collection.");
            SetSystemWeight(key, weightPercent);
            return this;
        }
        if (typeof(TValue) != typeof(string)) throw new InvalidOperationException($"Only string/Text members may be semantic in v1. '{key}' is {typeof(TValue).Name}.");
        _fields[key] = new TypedField(KnowledgeFieldType.Text, required, filterable, mode, weightPercent);
        return this;
    }

    public TypedKnowledgeSchemaBuilder<T> Filterable<TValue>(Expression<Func<T, TValue>> member, bool required = false)
    {
        var key = MemberKey(member);
        if (IsSystem(key)) return this;
        var type = MapType(typeof(TValue));
        if (_fields.TryGetValue(key, out var existing))
            _fields[key] = existing with { Filterable = true, Required = existing.Required || required };
        else
            _fields[key] = new TypedField(type, required, true, SemanticMode.None, 0);
        return this;
    }

    public KnowledgeSchemaDefinition Build(int revision = 1)
    {
        var builder = new KnowledgeSchemaBuilder(_key, _displayName, _id)
            .SetSemanticWeight(KnowledgeSystemFields.Title, _titleWeight)
            .SetSemanticWeight(KnowledgeSystemFields.Description, _descriptionWeight)
            .SetSemanticWeight(KnowledgeSystemFields.Tags, _tagsWeight);

        foreach (var (key, field) in _fields)
        {
            switch (field.Type)
            {
                case KnowledgeFieldType.Text: builder.Text(key, field.Weight, field.Mode, field.Required, field.Filterable); break;
                case KnowledgeFieldType.Int64: builder.Int64(key, field.Required, field.Filterable); break;
                case KnowledgeFieldType.Decimal: builder.Decimal(key, field.Required, field.Filterable); break;
                case KnowledgeFieldType.Boolean: builder.Boolean(key, field.Required, field.Filterable); break;
                case KnowledgeFieldType.DateTimeOffset: builder.DateTimeOffset(key, field.Required, field.Filterable); break;
                case KnowledgeFieldType.Guid: builder.GuidField(key, field.Required, field.Filterable); break;
                default: throw new NotSupportedException($"Unsupported typed schema member {field.Type}.");
            }
        }
        return builder.Build(revision);
    }

    private void SetSystemWeight(string key, int weight)
    {
        if (weight is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(weight));
        switch (key)
        {
            case KnowledgeSystemFields.Title: _titleWeight = weight; break;
            case KnowledgeSystemFields.Description: _descriptionWeight = weight; break;
            case KnowledgeSystemFields.Tags: _tagsWeight = weight; break;
            default: throw new InvalidOperationException($"Unknown system field '{key}'.");
        }
    }

    internal static string MemberKey<TValue>(Expression<Func<T, TValue>> expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        Expression body = expression.Body;
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary) body = unary.Operand;
        if (body is not MemberExpression { Expression: ParameterExpression } member)
            throw new NotSupportedException("A direct property/member access such as x => x.Level is required.");
        return KnowledgeSchemaBuilder.NormalizeKey(member.Member.Name);
    }

    private static bool IsSystem(string key) => key is KnowledgeSystemFields.Title or KnowledgeSystemFields.Description or KnowledgeSystemFields.Tags;

    private static KnowledgeFieldType MapType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string)) return KnowledgeFieldType.Text;
        if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte)) return KnowledgeFieldType.Int64;
        if (type == typeof(decimal) || type == typeof(double) || type == typeof(float)) return KnowledgeFieldType.Decimal;
        if (type == typeof(bool)) return KnowledgeFieldType.Boolean;
        if (type == typeof(DateTimeOffset) || type == typeof(DateTime)) return KnowledgeFieldType.DateTimeOffset;
        if (type == typeof(Guid)) return KnowledgeFieldType.Guid;
        throw new NotSupportedException($"Member type {type.Name} is not supported by SemanticKnowledge v1.");
    }

    private sealed record TypedField(KnowledgeFieldType Type, bool Required, bool Filterable, SemanticMode Mode, int Weight);
}

public static class KnowledgeFilterExpression
{
    public static KnowledgeFilter Parse<T>(Expression<Func<T, bool>> expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return ParseNode(expression.Body, expression.Parameters.Single());
    }

    private static KnowledgeFilter ParseNode(Expression expression, ParameterExpression parameter)
    {
        expression = StripConvert(expression);
        if (expression is BinaryExpression binary)
        {
            if (binary.NodeType == ExpressionType.AndAlso) return KnowledgeFilters.And(ParseNode(binary.Left, parameter), ParseNode(binary.Right, parameter));
            if (binary.NodeType == ExpressionType.OrElse) return KnowledgeFilters.Or(ParseNode(binary.Left, parameter), ParseNode(binary.Right, parameter));
            var memberOnLeft = TryMember(binary.Left, parameter, out var leftKey);
            var memberOnRight = TryMember(binary.Right, parameter, out var rightKey);
            if (memberOnLeft == memberOnRight) throw new NotSupportedException("A comparison must contain exactly one document member and one constant/captured value.");
            var key = memberOnLeft ? leftKey! : rightKey!;
            var valueExpression = memberOnLeft ? binary.Right : binary.Left;
            var value = ToKnowledgeValue(ReadValue(valueExpression));
            var nodeType = memberOnLeft ? binary.NodeType : Reverse(binary.NodeType);
            return nodeType switch
            {
                ExpressionType.Equal => KnowledgeFilters.Eq(key, value),
                ExpressionType.NotEqual => KnowledgeFilters.Ne(key, value),
                ExpressionType.GreaterThan => KnowledgeFilters.Gt(key, value),
                ExpressionType.GreaterThanOrEqual => KnowledgeFilters.Gte(key, value),
                ExpressionType.LessThan => KnowledgeFilters.Lt(key, value),
                ExpressionType.LessThanOrEqual => KnowledgeFilters.Lte(key, value),
                _ => throw new NotSupportedException($"Comparison operator {binary.NodeType} is not supported.")
            };
        }

        if (expression is UnaryExpression { NodeType: ExpressionType.Not } not) return KnowledgeFilters.Not(ParseNode(not.Operand, parameter));

        if (expression is MethodCallExpression call && call.Method.Name == nameof(Enumerable.Contains))
        {
            if (call.Object is MemberExpression objectMember && IsParameterMember(objectMember, parameter) && KnowledgeSchemaBuilder.NormalizeKey(objectMember.Member.Name) == KnowledgeSystemFields.Tags)
                return KnowledgeFilters.HasTag(Convert.ToString(ReadValue(call.Arguments.Single()), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
            if (call.Arguments.Count == 2 && call.Arguments[0] is MemberExpression staticMember && IsParameterMember(staticMember, parameter) && KnowledgeSchemaBuilder.NormalizeKey(staticMember.Member.Name) == KnowledgeSystemFields.Tags)
                return KnowledgeFilters.HasTag(Convert.ToString(ReadValue(call.Arguments[1]), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
        }

        throw new NotSupportedException($"Expression node '{expression.NodeType}' is not supported by SemanticKnowledge filtering.");
    }

    private static bool TryMember(Expression expression, ParameterExpression parameter, out string? key)
    {
        expression = StripConvert(expression);
        if (expression is MemberExpression member && IsParameterMember(member, parameter))
        {
            key = KnowledgeSchemaBuilder.NormalizeKey(member.Member.Name);
            return true;
        }
        key = null;
        return false;
    }

    private static bool IsParameterMember(MemberExpression member, ParameterExpression parameter) => StripConvert(member.Expression!) == parameter;

    private static object? ReadValue(Expression expression)
    {
        expression = StripConvert(expression);
        if (expression is ConstantExpression constant) return constant.Value;
        if (expression is MemberExpression member)
        {
            var target = member.Expression is null ? null : ReadValue(member.Expression);
            return member.Member switch
            {
                FieldInfo field => field.GetValue(target),
                PropertyInfo property => property.GetValue(target),
                _ => throw new NotSupportedException($"Captured member {member.Member.MemberType} is not supported.")
            };
        }
        throw new NotSupportedException("Filter values must be constants or captured values; method execution is intentionally not supported.");
    }

    private static KnowledgeValue ToKnowledgeValue(object? value) => value switch
    {
        string text => KnowledgeValue.From(text),
        int number => KnowledgeValue.From(number),
        long number => KnowledgeValue.From(number),
        short number => KnowledgeValue.From((long)number),
        byte number => KnowledgeValue.From((long)number),
        decimal number => KnowledgeValue.From(number),
        double number => KnowledgeValue.From((decimal)number),
        float number => KnowledgeValue.From((decimal)number),
        bool flag => KnowledgeValue.From(flag),
        DateTimeOffset time => KnowledgeValue.From(time),
        DateTime time => KnowledgeValue.From(new DateTimeOffset(time)),
        Guid guid => KnowledgeValue.From(guid),
        null => KnowledgeValue.From((string?)null),
        _ => throw new NotSupportedException($"Captured filter value type {value.GetType().Name} is not supported.")
    };

    private static Expression StripConvert(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary) expression = unary.Operand;
        return expression;
    }

    private static ExpressionType Reverse(ExpressionType type) => type switch
    {
        ExpressionType.GreaterThan => ExpressionType.LessThan,
        ExpressionType.GreaterThanOrEqual => ExpressionType.LessThanOrEqual,
        ExpressionType.LessThan => ExpressionType.GreaterThan,
        ExpressionType.LessThanOrEqual => ExpressionType.GreaterThanOrEqual,
        _ => type
    };
}

public sealed class KnowledgeSearchRequestBuilder<T>
{
    private readonly Guid _knowledgeBaseId;
    private KnowledgeFilter? _filter;
    private readonly List<Guid> _collections = [];
    private int _top = 10;
    private KnowledgeSearchMode _mode = KnowledgeSearchMode.Global;
    private bool _descendants = true;
    private KnowledgeResultInclude _include = KnowledgeResultInclude.MetadataOnly;

    public KnowledgeSearchRequestBuilder(Guid knowledgeBaseId) => _knowledgeBaseId = knowledgeBaseId;
    public KnowledgeSearchRequestBuilder<T> Where(Expression<Func<T, bool>> expression) { var parsed = KnowledgeFilterExpression.Parse(expression); _filter = _filter is null ? parsed : KnowledgeFilters.And(_filter, parsed); return this; }
    public KnowledgeSearchRequestBuilder<T> InCollection(Guid collectionId, bool includeDescendants = true) { _collections.Add(collectionId); _mode = KnowledgeSearchMode.Scoped; _descendants = includeDescendants; return this; }
    public KnowledgeSearchRequestBuilder<T> Smart() { _mode = KnowledgeSearchMode.Smart; return this; }
    public KnowledgeSearchRequestBuilder<T> Top(int top) { if (top <= 0) throw new ArgumentOutOfRangeException(nameof(top)); _top = top; return this; }
    public KnowledgeSearchRequestBuilder<T> Include(KnowledgeResultInclude include) { _include = include; return this; }
    public KnowledgeSearchRequest Build() => new() { KnowledgeBaseId = _knowledgeBaseId, Mode = _mode, CollectionIds = _collections.ToArray(), IncludeDescendants = _descendants, Filter = _filter, Top = _top, Include = _include };
}
