using Npgsql;

namespace SemanticKnowledge.PostgreSql;

internal sealed class PostgreSqlCollectionSnapshotResetter(
    SemanticKnowledgePostgreSqlOptions options,
    NpgsqlDataSource dataSource) : IKnowledgeCollectionSnapshotResetter
{
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var schema = '"' + options.Schema.Replace("\"", "\"\"") + '"';
        await using var command = new NpgsqlCommand($"DROP TABLE IF EXISTS {schema}.\"sk_collection_snapshot_documents\"; DROP TABLE IF EXISTS {schema}.\"sk_collection_snapshot_state\";", connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
