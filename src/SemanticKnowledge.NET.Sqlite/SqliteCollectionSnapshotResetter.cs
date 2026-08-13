using Microsoft.Data.Sqlite;

namespace SemanticKnowledge.Sqlite;

internal sealed class SqliteCollectionSnapshotResetter(SemanticKnowledgeSqliteOptions options) :
    IKnowledgeCollectionSnapshotResetter,
    IKnowledgeCollectionSnapshotRecoveryProvider
{
    public async Task DiscardStagedCollectionSnapshotAsync(Guid collectionId, Guid? expectedSnapshotId = null, CancellationToken cancellationToken = default)
    {
        if (collectionId == Guid.Empty) throw new ArgumentException("CollectionId cannot be empty.", nameof(collectionId));
        await using var connection = new SqliteConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (var exists = connection.CreateCommand())
        {
            exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='sk_collection_snapshot_state'";
            if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 0) return;
        }

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        Guid? staged;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT staging_snapshot_id FROM sk_collection_snapshot_state WHERE collection_id=$collection";
            read.Parameters.AddWithValue("$collection", collectionId.ToString("D"));
            var value = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            staged = value is null or DBNull ? null : Guid.Parse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!);
        }
        if (staged is null) { await transaction.CommitAsync(cancellationToken).ConfigureAwait(false); return; }
        if (expectedSnapshotId is { } expected && expected != staged)
            throw new InvalidOperationException($"Collection {collectionId} is staging snapshot {staged}, not expected snapshot {expected}.");

        await using (var lexicalExists = connection.CreateCommand())
        {
            lexicalExists.Transaction = transaction;
            lexicalExists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='sk_lexical_sources_fts'";
            if (Convert.ToInt32(await lexicalExists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0)
            {
                await using var deleteLexical = connection.CreateCommand(); deleteLexical.Transaction = transaction;
                deleteLexical.CommandText = "DELETE FROM sk_lexical_sources_fts WHERE collection_id=$collection AND entity_kind=$kind";
                deleteLexical.Parameters.AddWithValue("$collection", collectionId.ToString("D")); deleteLexical.Parameters.AddWithValue("$kind", KnowledgeCollectionSnapshotStorage.PendingLexicalEntityKind);
                await deleteLexical.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        await using (var deleteStage = connection.CreateCommand())
        {
            deleteStage.Transaction = transaction; deleteStage.CommandText = "DELETE FROM sk_collection_snapshot_documents WHERE snapshot_id=$snapshot"; deleteStage.Parameters.AddWithValue("$snapshot", staged.Value.ToString("D")); await deleteStage.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction; clear.CommandText = "UPDATE sk_collection_snapshot_state SET staging_snapshot_id=NULL,staging_source_revision=NULL,staging_schema_id=NULL,staging_started_at=NULL WHERE collection_id=$collection AND staging_snapshot_id=$snapshot"; clear.Parameters.AddWithValue("$collection", collectionId.ToString("D")); clear.Parameters.AddWithValue("$snapshot", staged.Value.ToString("D")); await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DROP TABLE IF EXISTS sk_collection_snapshot_documents; DROP TABLE IF EXISTS sk_collection_snapshot_state;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
