using Npgsql;

namespace SemanticKnowledge.PostgreSql;

internal sealed class PostgreSqlCollectionSnapshotResetter(
    SemanticKnowledgePostgreSqlOptions options,
    NpgsqlDataSource dataSource) :
    IKnowledgeCollectionSnapshotResetter,
    IKnowledgeCollectionSnapshotRecoveryProvider
{
    public async Task DiscardStagedCollectionSnapshotAsync(Guid collectionId, Guid? expectedSnapshotId = null, CancellationToken cancellationToken = default)
    {
        if (collectionId == Guid.Empty) throw new ArgumentException("CollectionId cannot be empty.", nameof(collectionId));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var schema = Quote(options.Schema);
        await using (var exists = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM information_schema.tables WHERE table_schema=@schema AND table_name='sk_collection_snapshot_state')", connection))
        {
            exists.Parameters.AddWithValue("schema", options.Schema);
            if (!(bool)(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false)) return;
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        Guid? staged;
        await using (var read = new NpgsqlCommand($"SELECT staging_snapshot_id FROM {schema}.\"sk_collection_snapshot_state\" WHERE collection_id=@collection FOR UPDATE", connection, transaction))
        {
            read.Parameters.AddWithValue("collection", collectionId);
            var value = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            staged = value is null or DBNull ? null : (Guid)value;
        }
        if (staged is null) { await transaction.CommitAsync(cancellationToken).ConfigureAwait(false); return; }
        if (expectedSnapshotId is { } expected && expected != staged)
            throw new InvalidOperationException($"Collection {collectionId} is staging snapshot {staged}, not expected snapshot {expected}.");

        await using (var lexicalExists = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM information_schema.tables WHERE table_schema=@schema AND table_name='sk_lexical_sources')", connection, transaction))
        {
            lexicalExists.Parameters.AddWithValue("schema", options.Schema);
            if ((bool)(await lexicalExists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false))
            {
                await using var deleteLexical = new NpgsqlCommand($"DELETE FROM {schema}.\"sk_lexical_sources\" WHERE collection_id=@collection AND entity_kind=@kind", connection, transaction);
                deleteLexical.Parameters.AddWithValue("collection", collectionId); deleteLexical.Parameters.AddWithValue("kind", KnowledgeCollectionSnapshotStorage.PendingLexicalEntityKind);
                await deleteLexical.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        await using (var deleteStage = new NpgsqlCommand($"DELETE FROM {schema}.\"sk_collection_snapshot_documents\" WHERE snapshot_id=@snapshot", connection, transaction))
        {
            deleteStage.Parameters.AddWithValue("snapshot", staged.Value); await deleteStage.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var clear = new NpgsqlCommand($"UPDATE {schema}.\"sk_collection_snapshot_state\" SET staging_snapshot_id=NULL,staging_source_revision=NULL,staging_schema_id=NULL,staging_started_at=NULL WHERE collection_id=@collection AND staging_snapshot_id=@snapshot", connection, transaction))
        {
            clear.Parameters.AddWithValue("collection", collectionId); clear.Parameters.AddWithValue("snapshot", staged.Value); await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var schema = Quote(options.Schema);
        await using var command = new NpgsqlCommand($"DROP TABLE IF EXISTS {schema}.\"sk_collection_snapshot_documents\"; DROP TABLE IF EXISTS {schema}.\"sk_collection_snapshot_state\";", connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Quote(string identifier) => '"' + identifier.Replace("\"", "\"\"") + '"';
}
