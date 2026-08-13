using Npgsql;

namespace SemanticKnowledge.PostgreSql;

internal sealed class PostgreSqlLogicalVersionAccessor(
    SemanticKnowledgePostgreSqlOptions options,
    NpgsqlDataSource dataSource) : IKnowledgeLogicalVersionAccessor
{
    public async Task<int?> GetStoredVersionAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var table = $"{Quote(options.Schema)}.{Quote("sk_store_metadata")}";
        await using var exists = new NpgsqlCommand("SELECT to_regclass(@name) IS NOT NULL", connection);
        exists.Parameters.AddWithValue("name", $"{options.Schema}.sk_store_metadata");
        if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not bool present || !present)
            return null;
        await using var command = new NpgsqlCommand($"SELECT database_version FROM {table} WHERE singleton=1", connection);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task SetStoredVersionAsync(int version, CancellationToken cancellationToken = default)
    {
        if (version <= 0) throw new ArgumentOutOfRangeException(nameof(version));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"UPDATE {Quote(options.Schema)}.{Quote("sk_store_metadata")} SET database_version=@version WHERE singleton=1", connection);
        command.Parameters.AddWithValue("version", version);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("SemanticKnowledge PostgreSQL metadata row is missing while updating the logical database version.");
    }

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
