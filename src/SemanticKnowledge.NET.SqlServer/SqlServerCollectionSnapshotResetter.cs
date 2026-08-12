using Microsoft.Data.SqlClient;

namespace SemanticKnowledge.SqlServer;

internal sealed class SqlServerCollectionSnapshotResetter(SemanticKnowledgeSqlServerOptions options) : IKnowledgeCollectionSnapshotResetter
{
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var schema = $"[{options.Schema.Replace("]", "]]", StringComparison.Ordinal)}]";
        await using var command = new SqlCommand($"IF OBJECT_ID(N'{schema}.[sk_collection_snapshot_documents]',N'U') IS NOT NULL DROP TABLE {schema}.[sk_collection_snapshot_documents]; IF OBJECT_ID(N'{schema}.[sk_collection_snapshot_state]',N'U') IS NOT NULL DROP TABLE {schema}.[sk_collection_snapshot_state];", connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
