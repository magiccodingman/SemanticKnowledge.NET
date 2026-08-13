using Microsoft.Data.SqlClient;

namespace SemanticKnowledge.SqlServer;

internal sealed class SqlServerLogicalVersionAccessor(SemanticKnowledgeSqlServerOptions options) : IKnowledgeLogicalVersionAccessor
{
    public async Task<int?> GetStoredVersionAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var qualified = $"{Quote(options.Schema)}.{Quote("sk_store_metadata")}";
        await using var exists = new SqlCommand("SELECT CASE WHEN OBJECT_ID(@name, N'U') IS NULL THEN 0 ELSE 1 END", connection);
        exists.Parameters.AddWithValue("@name", qualified);
        if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) == 0)
            return null;
        await using var command = new SqlCommand($"SELECT database_version FROM {qualified} WHERE singleton=1", connection);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task SetStoredVersionAsync(int version, CancellationToken cancellationToken = default)
    {
        if (version <= 0) throw new ArgumentOutOfRangeException(nameof(version));
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand($"UPDATE {Quote(options.Schema)}.{Quote("sk_store_metadata")} SET database_version=@version WHERE singleton=1", connection);
        command.Parameters.AddWithValue("@version", version);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("SemanticKnowledge SQL Server metadata row is missing while updating the logical database version.");
    }

    private static string Quote(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";
}
