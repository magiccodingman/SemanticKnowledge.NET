using Microsoft.Data.SqlClient;

namespace SemanticKnowledge.SqlServer;

internal sealed record SqlServerCompiledFilter(string Sql, IReadOnlyList<SqlParameter> Parameters);

internal static class SqlServerFilterCompiler
{
    public static SqlServerCompiledFilter Compile(KnowledgeFilter? filter, string schema)
    {
        if (filter is null) return new SqlServerCompiledFilter("1=1", Array.Empty<SqlParameter>());
        var parameters = new List<SqlParameter>(); var state = new ParameterState();
        return new SqlServerCompiledFilter(CompileNode(filter, schema, parameters, state), parameters);
    }

    private static string CompileNode(KnowledgeFilter filter, string schema, List<SqlParameter> parameters, ParameterState state) => filter switch
    {
        KnowledgeAndFilter and => Join(and.Filters, "AND", schema, parameters, state),
        KnowledgeOrFilter or => Join(or.Filters, "OR", schema, parameters, state),
        KnowledgeNotFilter not => $"NOT ({CompileNode(not.Filter, schema, parameters, state)})",
        KnowledgeComparisonFilter comparison => CompileComparison(comparison, schema, parameters, state),
        _ => throw new NotSupportedException($"Unsupported filter type {filter.GetType().Name}.")
    };

    private static string Join(IReadOnlyList<KnowledgeFilter> filters, string op, string schema, List<SqlParameter> parameters, ParameterState state)
    {
        if (filters.Count == 0) return op == "AND" ? "1=1" : "1=0";
        var parts = new string[filters.Count]; for (var i = 0; i < filters.Count; i++) parts[i] = CompileNode(filters[i], schema, parameters, state);
        return "(" + string.Join($" {op} ", parts) + ")";
    }

    private static string CompileComparison(KnowledgeComparisonFilter filter, string schema, List<SqlParameter> parameters, ParameterState state)
    {
        var tagsTable = $"{Q(schema)}.[sk_document_tags]"; var valuesTable = $"{Q(schema)}.[sk_document_values]";
        if (filter.Operator == KnowledgeFilterOperator.TagContains)
        {
            var parameter = Add(parameters, state, filter.Value ?? throw new InvalidOperationException("TagContains requires a value."));
            return $"EXISTS (SELECT 1 FROM {tagsTable} t WHERE t.document_id=d.id AND t.tag={parameter})";
        }
        if (filter.FieldKey is KnowledgeSystemFields.Title or KnowledgeSystemFields.Description)
            return CompareScalar(filter.FieldKey == KnowledgeSystemFields.Title ? "d.title" : "d.description", filter, parameters, state);
        if (filter.FieldKey == KnowledgeSystemFields.Tags) throw new NotSupportedException("Use KnowledgeFilters.HasTag for tag filtering.");
        var fieldName = $"@sk_f{state.Next()}"; parameters.AddWithValue(fieldName, filter.FieldKey);
        if (filter.Operator == KnowledgeFilterOperator.IsNull) return $"NOT EXISTS (SELECT 1 FROM {valuesTable} v WHERE v.document_id=d.id AND v.field_key={fieldName})";
        if (filter.Operator == KnowledgeFilterOperator.IsNotNull) return $"EXISTS (SELECT 1 FROM {valuesTable} v WHERE v.document_id=d.id AND v.field_key={fieldName})";
        var value = filter.Value ?? filter.Values?.FirstOrDefault() ?? throw new InvalidOperationException($"Filter {filter.Operator} requires a value."); var column = ValueColumn(value.Type);
        if (filter.Operator == KnowledgeFilterOperator.In)
        {
            var values = filter.Values ?? throw new InvalidOperationException("IN requires Values."); if (values.Count == 0) return "1=0";
            var names = new string[values.Count]; for (var i = 0; i < values.Count; i++) { if (values[i].Type != value.Type) throw new InvalidOperationException("All IN values must have the same type."); names[i] = Add(parameters, state, values[i]); }
            return $"EXISTS (SELECT 1 FROM {valuesTable} v WHERE v.document_id=d.id AND v.field_key={fieldName} AND v.{column} IN ({string.Join(',', names)}))";
        }
        return $"EXISTS (SELECT 1 FROM {valuesTable} v WHERE v.document_id=d.id AND v.field_key={fieldName} AND v.{column} {Operator(filter.Operator)} {Add(parameters, state, value)})";
    }

    private static string CompareScalar(string column, KnowledgeComparisonFilter filter, List<SqlParameter> parameters, ParameterState state)
    {
        if (filter.Operator == KnowledgeFilterOperator.IsNull) return $"{column} IS NULL";
        if (filter.Operator == KnowledgeFilterOperator.IsNotNull) return $"{column} IS NOT NULL";
        if (filter.Operator == KnowledgeFilterOperator.In)
        {
            var values = filter.Values ?? throw new InvalidOperationException("IN requires Values."); if (values.Count == 0) return "1=0";
            var names = new string[values.Count]; for (var i = 0; i < values.Count; i++) names[i] = Add(parameters, state, values[i]); return $"{column} IN ({string.Join(',', names)})";
        }
        return $"{column} {Operator(filter.Operator)} {Add(parameters, state, filter.Value ?? throw new InvalidOperationException("Comparison requires a value."))}";
    }

    private static string Add(List<SqlParameter> parameters, ParameterState state, KnowledgeValue value)
    {
        var name = $"@sk_p{state.Next()}"; var parameter = new SqlParameter(name, value.ToObject() ?? DBNull.Value);
        if (value.Type == KnowledgeFieldType.DateTimeOffset && value.DateTimeOffset is { } dto) parameter.Value = dto;
        parameters.Add(parameter); return name;
    }
    private static string ValueColumn(KnowledgeFieldType type) => type switch { KnowledgeFieldType.Text => "text_value", KnowledgeFieldType.Int64 => "int_value", KnowledgeFieldType.Decimal => "decimal_value", KnowledgeFieldType.Boolean => "bool_value", KnowledgeFieldType.DateTimeOffset => "datetime_value", KnowledgeFieldType.Guid => "guid_value", _ => throw new NotSupportedException($"Unsupported field type {type}.") };
    private static string Operator(KnowledgeFilterOperator op) => op switch { KnowledgeFilterOperator.Equal => "=", KnowledgeFilterOperator.NotEqual => "<>", KnowledgeFilterOperator.LessThan => "<", KnowledgeFilterOperator.LessThanOrEqual => "<=", KnowledgeFilterOperator.GreaterThan => ">", KnowledgeFilterOperator.GreaterThanOrEqual => ">=", _ => throw new NotSupportedException($"Operator {op} is not valid for a scalar comparison.") };
    private static string Q(string id) => $"[{id.Replace("]", "]]", StringComparison.Ordinal)}]";
    private sealed class ParameterState { private int _value; public int Next() => _value++; }
}
