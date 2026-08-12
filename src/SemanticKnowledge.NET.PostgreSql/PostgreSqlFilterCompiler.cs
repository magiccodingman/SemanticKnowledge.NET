using Npgsql;

namespace SemanticKnowledge.PostgreSql;

internal sealed record PostgreSqlCompiledFilter(string Sql, IReadOnlyList<NpgsqlParameter> Parameters);

internal static class PostgreSqlFilterCompiler
{
    public static PostgreSqlCompiledFilter Compile(KnowledgeFilter? filter)
    {
        if (filter is null) return new PostgreSqlCompiledFilter("TRUE", Array.Empty<NpgsqlParameter>());
        var parameters = new List<NpgsqlParameter>(); var state = new ParameterState();
        return new PostgreSqlCompiledFilter(CompileNode(filter, parameters, state), parameters);
    }

    private static string CompileNode(KnowledgeFilter filter, List<NpgsqlParameter> parameters, ParameterState state) => filter switch
    {
        KnowledgeAndFilter and => Join(and.Filters, "AND", parameters, state),
        KnowledgeOrFilter or => Join(or.Filters, "OR", parameters, state),
        KnowledgeNotFilter not => $"NOT ({CompileNode(not.Filter, parameters, state)})",
        KnowledgeComparisonFilter comparison => CompileComparison(comparison, parameters, state),
        _ => throw new NotSupportedException($"Unsupported filter type {filter.GetType().Name}.")
    };

    private static string Join(IReadOnlyList<KnowledgeFilter> filters, string op, List<NpgsqlParameter> parameters, ParameterState state)
    {
        if (filters.Count == 0) return op == "AND" ? "TRUE" : "FALSE";
        var parts = new string[filters.Count]; for (var i = 0; i < filters.Count; i++) parts[i] = CompileNode(filters[i], parameters, state);
        return "(" + string.Join($" {op} ", parts) + ")";
    }

    private static string CompileComparison(KnowledgeComparisonFilter filter, List<NpgsqlParameter> parameters, ParameterState state)
    {
        if (filter.Operator == KnowledgeFilterOperator.TagContains)
        {
            var p = Add(parameters, state, filter.Value ?? throw new InvalidOperationException("TagContains requires a value."));
            return $"EXISTS (SELECT 1 FROM sk_document_tags t WHERE t.document_id=d.id AND lower(t.tag)=lower({p}))";
        }
        if (filter.FieldKey is KnowledgeSystemFields.Title or KnowledgeSystemFields.Description)
            return CompareScalar(filter.FieldKey == KnowledgeSystemFields.Title ? "d.title" : "d.description", filter, parameters, state);
        if (filter.FieldKey == KnowledgeSystemFields.Tags) throw new NotSupportedException("Use KnowledgeFilters.HasTag for tag filtering.");

        var field = $"@sk_f{state.Next()}"; parameters.Add(new NpgsqlParameter(field[1..], filter.FieldKey));
        if (filter.Operator == KnowledgeFilterOperator.IsNull) return $"NOT EXISTS (SELECT 1 FROM sk_document_values v WHERE v.document_id=d.id AND v.field_key={field})";
        if (filter.Operator == KnowledgeFilterOperator.IsNotNull) return $"EXISTS (SELECT 1 FROM sk_document_values v WHERE v.document_id=d.id AND v.field_key={field})";
        var value = filter.Value ?? filter.Values?.FirstOrDefault() ?? throw new InvalidOperationException($"Filter {filter.Operator} requires a value.");
        var column = ValueColumn(value.Type);
        if (filter.Operator == KnowledgeFilterOperator.In)
        {
            var values = filter.Values ?? throw new InvalidOperationException("IN requires Values."); if (values.Count == 0) return "FALSE";
            var names = new string[values.Count]; for (var i = 0; i < values.Count; i++) { if (values[i].Type != value.Type) throw new InvalidOperationException("All IN values must have the same type."); names[i] = Add(parameters, state, values[i]); }
            return $"EXISTS (SELECT 1 FROM sk_document_values v WHERE v.document_id=d.id AND v.field_key={field} AND v.{column} IN ({string.Join(',', names)}))";
        }
        return $"EXISTS (SELECT 1 FROM sk_document_values v WHERE v.document_id=d.id AND v.field_key={field} AND v.{column} {Operator(filter.Operator)} {Add(parameters, state, value)})";
    }

    private static string CompareScalar(string column, KnowledgeComparisonFilter filter, List<NpgsqlParameter> parameters, ParameterState state)
    {
        if (filter.Operator == KnowledgeFilterOperator.IsNull) return $"{column} IS NULL";
        if (filter.Operator == KnowledgeFilterOperator.IsNotNull) return $"{column} IS NOT NULL";
        if (filter.Operator == KnowledgeFilterOperator.In)
        {
            var values = filter.Values ?? throw new InvalidOperationException("IN requires values."); if (values.Count == 0) return "FALSE";
            var names = new string[values.Count]; for (var i = 0; i < values.Count; i++) names[i] = Add(parameters, state, values[i]);
            return $"{column} IN ({string.Join(',', names)})";
        }
        return $"{column} {Operator(filter.Operator)} {Add(parameters, state, filter.Value ?? throw new InvalidOperationException("Comparison requires a value."))}";
    }

    private static string Add(List<NpgsqlParameter> parameters, ParameterState state, KnowledgeValue value)
    {
        var name = $"sk_p{state.Next()}"; parameters.Add(new NpgsqlParameter(name, value.ToObject() ?? DBNull.Value)); return "@" + name;
    }
    private static string ValueColumn(KnowledgeFieldType type) => type switch { KnowledgeFieldType.Text => "text_value", KnowledgeFieldType.Int64 => "int_value", KnowledgeFieldType.Decimal => "decimal_value", KnowledgeFieldType.Boolean => "bool_value", KnowledgeFieldType.DateTimeOffset => "datetime_value", KnowledgeFieldType.Guid => "guid_value", _ => throw new NotSupportedException($"Unsupported field type {type}.") };
    private static string Operator(KnowledgeFilterOperator op) => op switch { KnowledgeFilterOperator.Equal => "=", KnowledgeFilterOperator.NotEqual => "<>", KnowledgeFilterOperator.LessThan => "<", KnowledgeFilterOperator.LessThanOrEqual => "<=", KnowledgeFilterOperator.GreaterThan => ">", KnowledgeFilterOperator.GreaterThanOrEqual => ">=", _ => throw new NotSupportedException($"Operator {op} is not valid for a scalar comparison.") };
    private sealed class ParameterState { private int _value; public int Next() => _value++; }
}
