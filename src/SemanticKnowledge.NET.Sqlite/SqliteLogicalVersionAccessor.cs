using Microsoft.Data.Sqlite;

namespace SemanticKnowledge.Sqlite;

internal sealed class SqliteLogicalVersionAccessor(SemanticKnowledgeSqliteOptions options) : IKnowledgeLogicalVersionAccessor
{
    public async Task<int?> GetStoredVersionAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='sk_store_metadata')";
        if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) == 0)
            return null;
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT database_version FROM sk_store_metadata WHERE singleton=1";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task SetStoredVersionAsync(int version, CancellationToken cancellationToken = default)
    {
        if (version <= 0) throw new ArgumentOutOfRangeException(nameof(version));
        await using var connection = new SqliteConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE sk_store_metadata SET database_version=$version WHERE singleton=1";
        command.Parameters.AddWithValue("$version", version);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("SemanticKnowledge SQLite metadata row is missing while updating the logical database version.");
    }
}
