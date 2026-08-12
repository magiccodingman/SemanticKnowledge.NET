using Microsoft.Data.Sqlite;

namespace SemanticKnowledge.Sqlite;

internal sealed record SqliteCompiledFilter(string Sql, IReadOnlyList<SqliteParameter> Parameters);

internal static class SqliteFilterCompiler
{
    public static SqliteCompiledFilter Compile(KnowledgeFilter? filter)
    {
        if (filter is null) return new SqliteCompiledFilter("1=1", Array.Empty<SqliteParameter>());
        var parameters = new List<SqliteParameter>();
        var index = 0;
        return new SqliteCompiledFilter(CompileNode(filter, parameters, ref index), parameters);
    }

    private static string CompileNode(KnowledgeFilter filter, List<SqliteParameter> parameters, ref int index) => filter switch
    {
        KnowledgeAndFilter and => Join(and.Filters, "AND", parameters, ref index),
        KnowledgeOrFilter or => Join(or.Filters, "OR", parameters, ref index),
        KnowledgeNotFilter not => $"NOT ({CompileNode(not.Filter, parameters, ref index)})",
        KnowledgeComparisonFilter comparison => CompileComparison(comparison, parameters, ref index),
        _ => throw new NotSupportedException($"Unsupported filter type {filter.GetType().Name}.")
    };

    private static string Join(IReadOnlyList<KnowledgeFilter> filters, string op, List<SqliteParameter> parameters, ref int index)
    {
        if (filters.Count == 0) return op == "AND" ? "1=1" : "1=0";
        return "(" + string.Join($" {op} ", filters.Select(x => CompileNode(x, parameters, ref index))) + ")";
    }

    private static string CompileComparison(KnowledgeComparisonFilter filter, List<SqliteParameter> parameters, ref int index)
    {
        if (filter.Operator == KnowledgeFilterOperator.TagContains)
        {
            var p = Add(parameters, ref index, filter.Value ?? throw new InvalidOperationException("TagContains requires a value."));
            return $"EXISTS (SELECT 1 FROM sk_document_tags t WHERE t.document_id=d.id AND t.tag={p})";
        }

        if (filter.FieldKey is KnowledgeSystemFields.Title or KnowledgeSystemFields.Description)
        {
            var column = filter.FieldKey == KnowledgeSystemFields.Title ? "d.title" : "d.description";
            return CompareScalar(column, filter, parameters, ref index);
        }

        if (filter.FieldKey == KnowledgeSystemFields.Tags) throw new NotSupportedException("Use KnowledgeFilters.HasTag for tag filtering.");

        var fieldParam = $"$sk_f{index++}";
        parameters.Add(new SqliteParameter(fieldParam, filter.FieldKey));
        if (filter.Operator == KnowledgeFilterOperator.IsNull) return $"NOT EXISTS (SELECT 1 FROM sk_document_values v WHERE v.document_id=d.id AND v.field_key={fieldParam})";
        if (filter.Operator == KnowledgeFilterOperator.IsNotNull) return $"EXISTS (SELECT 1 FROM sk_document_values v WHERE v.document_id=d.id AND v.field_key={fieldParam})";

        var value = filter.Value ?? filter.Values?.FirstOrDefault() ?? throw new InvalidOperationException($"Filter {filter.Operator} requires a value.");
        var valueColumn = ValueColumn(value.Type);
        if (filter.Operator == KnowledgeFilterOperator.In)
        {
            var values = filter.Values ?? throw new InvalidOperationException("IN requires Values.");
            if (values.Count == 0) return "1=0";
            if (values.Any(x => x.Type != value.Type)) throw new InvalidOperationException("All IN values must have the same type.");
            var ps = values.Select(x => Add(parameters, ref index, x)).ToArray();
            return $"EXISTS (SELECT 1 FROM sk_document_values v WHERE v.document_id=d.id AND v.field_key={fieldParam} AND v.{valueColumn} IN ({string.Join(',', ps)}))";
        }
        var p = Add(parameters, ref index, value);
        return $"EXISTS (SELECT 1 FROM sk_document_values v WHERE v.document_id=d.id AND v.field_key={fieldParam} AND v.{valueColumn} {Operator(filter.Operator)} {p})";
    }

    private static string CompareScalar(string column, KnowledgeComparisonFilter filter, List<SqliteParameter> parameters, ref int index)
    {
        if (filter.Operator == KnowledgeFilterOperator.IsNull) return $"{column} IS NULL";
        if (filter.Operator == KnowledgeFilterOperator.IsNotNull) return $"{column} IS NOT NULL";
        if (filter.Operator == KnowledgeFilterOperator.In)
        {
            var values = filter.Values ?? throw new InvalidOperationException("IN requires values.");
            var ps = values.Select(x => Add(parameters, ref index, x)).ToArray();
            return ps.Length == 0 ? "1=0" : $"{column} IN ({string.Join(',', ps)})";
        }
        return $"{column} {Operator(filter.Operator)} {Add(parameters, ref index, filter.Value ?? throw new InvalidOperationException("Comparison requires a value."))}";
    }

    private static string Add(List<SqliteParameter> parameters, ref int index, KnowledgeValue value)
    {
        var name = $"$sk_p{index++}";
        var parameter = new SqliteParameter(name, value.ToObject() ?? DBNull.Value);
        if (value.Type == KnowledgeFieldType.Boolean && value.Boolean is { } b) parameter.Value = b ? 1 : 0;
        if (value.Type == KnowledgeFieldType.DateTimeOffset && value.DateTimeOffset is { } dto) parameter.Value = dto.ToUniversalTime().ToString("O");
        if (value.Type == KnowledgeFieldType.Guid && value.Guid is { } guid) parameter.Value = guid.ToString("D");
        parameters.Add(parameter); return name;
    }

    private static string ValueColumn(KnowledgeFieldType type) => type switch
    {
        KnowledgeFieldType.Text => "text_value", KnowledgeFieldType.Int64 => "int_value", KnowledgeFieldType.Decimal => "decimal_value",
        KnowledgeFieldType.Boolean => "bool_value", KnowledgeFieldType.DateTimeOffset => "datetime_value", KnowledgeFieldType.Guid => "guid_value",
        _ => throw new NotSupportedException($"Unsupported field type {type}.")
    };

    private static string Operator(KnowledgeFilterOperator op) => op switch
    {
        KnowledgeFilterOperator.Equal => "=", KnowledgeFilterOperator.NotEqual => "<>", KnowledgeFilterOperator.LessThan => "<",
        KnowledgeFilterOperator.LessThanOrEqual => "<=", KnowledgeFilterOperator.GreaterThan => ">", KnowledgeFilterOperator.GreaterThanOrEqual => ">=",
        _ => throw new NotSupportedException($"Operator {op} is not valid for a scalar comparison.")
    };
}
