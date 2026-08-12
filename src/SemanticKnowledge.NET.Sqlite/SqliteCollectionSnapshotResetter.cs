using Microsoft.Data.Sqlite;

namespace SemanticKnowledge.Sqlite;

internal sealed class SqliteCollectionSnapshotResetter(SemanticKnowledgeSqliteOptions options) : IKnowledgeCollectionSnapshotResetter
{
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DROP TABLE IF EXISTS sk_collection_snapshot_documents; DROP TABLE IF EXISTS sk_collection_snapshot_state;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
