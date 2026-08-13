using Microsoft.Data.SqlClient;

namespace SemanticKnowledge.SqlServer;

internal sealed class SqlServerCollectionSnapshotResetter(SemanticKnowledgeSqlServerOptions options) :
    IKnowledgeCollectionSnapshotResetter,
    IKnowledgeCollectionSnapshotRecoveryProvider
{
    public async Task DiscardStagedCollectionSnapshotAsync(Guid collectionId, Guid? expectedSnapshotId = null, CancellationToken cancellationToken = default)
    {
        if (collectionId == Guid.Empty) throw new ArgumentException("CollectionId cannot be empty.", nameof(collectionId));
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var schema = $"[{options.Schema.Replace("]", "]]", StringComparison.Ordinal)}]";
        await using (var exists = new SqlCommand($"SELECT CASE WHEN OBJECT_ID(N'{schema}.[sk_collection_snapshot_state]',N'U') IS NULL THEN 0 ELSE 1 END", connection))
        {
            if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 0) return;
        }

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        Guid? staged;
        await using (var read = new SqlCommand($"SELECT staging_snapshot_id FROM {schema}.[sk_collection_snapshot_state] WITH (UPDLOCK,HOLDLOCK) WHERE collection_id=@collection", connection, transaction))
        {
            read.Parameters.AddWithValue("@collection", collectionId);
            var value = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            staged = value is null or DBNull ? null : (Guid)value;
        }
        if (staged is null) { await transaction.CommitAsync(cancellationToken).ConfigureAwait(false); return; }
        if (expectedSnapshotId is { } expected && expected != staged)
            throw new InvalidOperationException($"Collection {collectionId} is staging snapshot {staged}, not expected snapshot {expected}.");

        await using (var lexicalExists = new SqlCommand($"SELECT CASE WHEN OBJECT_ID(N'{schema}.[sk_lexical_sources]',N'U') IS NULL THEN 0 ELSE 1 END", connection, transaction))
        {
            if (Convert.ToInt32(await lexicalExists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0)
            {
                await using var deleteLexical = new SqlCommand($"DELETE FROM {schema}.[sk_lexical_sources] WHERE collection_id=@collection AND entity_kind=@kind", connection, transaction);
                deleteLexical.Parameters.AddWithValue("@collection", collectionId); deleteLexical.Parameters.AddWithValue("@kind", KnowledgeCollectionSnapshotStorage.PendingLexicalEntityKind);
                await deleteLexical.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        await using (var deleteStage = new SqlCommand($"DELETE FROM {schema}.[sk_collection_snapshot_documents] WHERE snapshot_id=@snapshot", connection, transaction))
        {
            deleteStage.Parameters.AddWithValue("@snapshot", staged.Value); await deleteStage.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var clear = new SqlCommand($"UPDATE {schema}.[sk_collection_snapshot_state] SET staging_snapshot_id=NULL,staging_source_revision=NULL,staging_schema_id=NULL,staging_started_at=NULL WHERE collection_id=@collection AND staging_snapshot_id=@snapshot", connection, transaction))
        {
            clear.Parameters.AddWithValue("@collection", collectionId); clear.Parameters.AddWithValue("@snapshot", staged.Value); await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var schema = $"[{options.Schema.Replace("]", "]]", StringComparison.Ordinal)}]";
        await using var command = new SqlCommand($"IF OBJECT_ID(N'{schema}.[sk_collection_snapshot_documents]',N'U') IS NOT NULL DROP TABLE {schema}.[sk_collection_snapshot_documents]; IF OBJECT_ID(N'{schema}.[sk_collection_snapshot_state]',N'U') IS NOT NULL DROP TABLE {schema}.[sk_collection_snapshot_state];", connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
