using Microsoft.Data.Sqlite;
using OnnxTextEmbeddings;
using OnnxTextEmbeddings.SqliteVec;

namespace SemanticKnowledge.Sqlite;

internal sealed class SqliteCollectionSnapshotProvider(SemanticKnowledgeSqliteOptions options) : IKnowledgeCollectionSnapshotProvider
{
    private const string LexicalTable = "sk_lexical_sources_fts";
    private const string StateTable = "sk_collection_snapshot_state";
    private const string StagingTable = "sk_collection_snapshot_documents";

    public async Task<KnowledgeCollectionSnapshotState?> GetCollectionSnapshotStateAsync(Guid collectionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT active_snapshot_id,source_revision,published_at,staging_snapshot_id,staging_source_revision FROM {StateTable} WHERE collection_id=$collection";
        command.Parameters.AddWithValue("$collection", collectionId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return ReadState(collectionId, reader);
    }

    public async Task<KnowledgeCollectionSnapshotHandle> BeginCollectionSnapshotAsync(Guid knowledgeBaseId, Guid collectionId, Guid schemaId, string sourceRevision, CancellationToken cancellationToken = default)
    {
        if (knowledgeBaseId == Guid.Empty || collectionId == Guid.Empty || schemaId == Guid.Empty) throw new ArgumentException("KnowledgeBaseId, CollectionId and SchemaId are required.");
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRevision);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        await ValidateCollectionAsync(connection, knowledgeBaseId, collectionId, schemaId, cancellationToken).ConfigureAwait(false);

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var existing = await ReadStateAsync(connection, transaction, collectionId, cancellationToken).ConfigureAwait(false);
        if (existing?.StagingSnapshotId is not null && !string.Equals(existing.StagingSourceRevision, sourceRevision, StringComparison.Ordinal))
            throw new InvalidOperationException($"Collection {collectionId} already has staged snapshot {existing.StagingSnapshotId} for source revision '{existing.StagingSourceRevision}'. Retry that revision or allow the existing synchronization to finish.");

        // Retrying the same opaque revision is safe: discard only its invisible staging state and start clean.
        await DeletePendingLexicalAsync(connection, transaction, collectionId, cancellationToken).ConfigureAwait(false);
        if (existing?.StagingSnapshotId is { } previous)
            await ExecuteAsync(connection, transaction, $"DELETE FROM {StagingTable} WHERE snapshot_id=$snapshot", cancellationToken, ("$snapshot", previous.ToString("D"))).ConfigureAwait(false);

        var snapshotId = Guid.NewGuid();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"""
                INSERT INTO {StateTable}(collection_id,staging_snapshot_id,staging_source_revision,staging_schema_id,staging_started_at)
                VALUES($collection,$snapshot,$revision,$schema,$started)
                ON CONFLICT(collection_id) DO UPDATE SET
                    staging_snapshot_id=excluded.staging_snapshot_id,
                    staging_source_revision=excluded.staging_source_revision,
                    staging_schema_id=excluded.staging_schema_id,
                    staging_started_at=excluded.staging_started_at
                """;
            command.Parameters.AddWithValue("$collection", collectionId.ToString("D"));
            command.Parameters.AddWithValue("$snapshot", snapshotId.ToString("D"));
            command.Parameters.AddWithValue("$revision", sourceRevision);
            command.Parameters.AddWithValue("$schema", schemaId.ToString("D"));
            command.Parameters.AddWithValue("$started", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new KnowledgeCollectionSnapshotHandle { SnapshotId = snapshotId, KnowledgeBaseId = knowledgeBaseId, CollectionId = collectionId, SchemaId = schemaId, SourceRevision = sourceRevision };
    }

    public async Task StageCollectionSnapshotDocumentAsync(KnowledgeCollectionSnapshotHandle snapshot, KnowledgePreparedSnapshotDocument prepared, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(prepared);
        ValidatePrepared(snapshot, prepared.Document);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        await EnsureStagingHandleAsync(connection, snapshot, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await InsertStageRowAsync(connection, transaction, snapshot, prepared.Document, false, KnowledgeSnapshotPayloadSerializer.Serialize(prepared), cancellationToken).ConfigureAwait(false);
        await StageLexicalAsync(connection, transaction, snapshot, prepared.LexicalSources, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StageExistingCollectionSnapshotDocumentAsync(KnowledgeCollectionSnapshotHandle snapshot, Guid documentId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        await EnsureStagingHandleAsync(connection, snapshot, cancellationToken).ConfigureAwait(false);
        var existing = await ReadDocumentIdentityAsync(connection, documentId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Cannot stage missing document {documentId}.");
        if (existing.CollectionId != snapshot.CollectionId || existing.KnowledgeBaseId != snapshot.KnowledgeBaseId)
            throw new InvalidOperationException($"Document {documentId} does not belong to snapshot Collection {snapshot.CollectionId}.");

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await InsertStageRowAsync(connection, transaction, snapshot, existing, true, null, cancellationToken).ConfigureAwait(false);
        await CopyExistingLexicalAsync(connection, transaction, documentId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<KnowledgeCollectionSnapshotState> PublishCollectionSnapshotAsync(KnowledgeCollectionSnapshotHandle snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        await EnsureStagingHandleAsync(connection, snapshot, cancellationToken).ConfigureAwait(false);

        var metadata = await ReadVectorMetadataAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var deletedIds = await GetDeletedDocumentIdsAsync(connection, transaction, snapshot, cancellationToken).ConfigureAwait(false);
        foreach (var id in deletedIds) await DeleteLiveDocumentAsync(connection, transaction, metadata.VectorTable, id, cancellationToken).ConfigureAwait(false);

        var payloads = await ReadChangedPayloadsAsync(connection, transaction, snapshot.SnapshotId, cancellationToken).ConfigureAwait(false);
        foreach (var payload in payloads)
        {
            var prepared = KnowledgeSnapshotPayloadSerializer.Deserialize(payload);
            await UpsertLivePreparedAsync(connection, transaction, metadata, prepared, cancellationToken).ConfigureAwait(false);
        }

        if (await LexicalTableExistsAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
        {
            await ExecuteAsync(connection, transaction, $"DELETE FROM {LexicalTable} WHERE collection_id=$collection AND entity_kind=$kind", cancellationToken,
                ("$collection", snapshot.CollectionId.ToString("D")), ("$kind", (int)SemanticEntityKind.Document)).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, $"UPDATE {LexicalTable} SET entity_kind=$documentKind WHERE collection_id=$collection AND entity_kind=$pendingKind", cancellationToken,
                ("$documentKind", (int)SemanticEntityKind.Document), ("$collection", snapshot.CollectionId.ToString("D")), ("$pendingKind", KnowledgeCollectionSnapshotStorage.PendingLexicalEntityKind)).ConfigureAwait(false);
        }

        var publishedAt = DateTimeOffset.UtcNow;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"""
                UPDATE {StateTable}
                SET active_snapshot_id=staging_snapshot_id,
                    source_revision=staging_source_revision,
                    published_at=$published,
                    staging_snapshot_id=NULL,
                    staging_source_revision=NULL,
                    staging_schema_id=NULL,
                    staging_started_at=NULL
                WHERE collection_id=$collection AND staging_snapshot_id=$snapshot
                """;
            command.Parameters.AddWithValue("$published", publishedAt.ToString("O"));
            command.Parameters.AddWithValue("$collection", snapshot.CollectionId.ToString("D"));
            command.Parameters.AddWithValue("$snapshot", snapshot.SnapshotId.ToString("D"));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("The staged Collection snapshot changed before it could be published.");
        }
        await ExecuteAsync(connection, transaction, $"DELETE FROM {StagingTable} WHERE snapshot_id=$snapshot", cancellationToken, ("$snapshot", snapshot.SnapshotId.ToString("D"))).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new KnowledgeCollectionSnapshotState { CollectionId = snapshot.CollectionId, ActiveSnapshotId = snapshot.SnapshotId, SourceRevision = snapshot.SourceRevision, PublishedAt = publishedAt };
    }

    public async Task AbortCollectionSnapshotAsync(KnowledgeCollectionSnapshotHandle snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var state = await ReadStateAsync(connection, transaction, snapshot.CollectionId, cancellationToken).ConfigureAwait(false);
        if (state?.StagingSnapshotId != snapshot.SnapshotId) { await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false); return; }
        await DeletePendingLexicalAsync(connection, transaction, snapshot.CollectionId, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, $"DELETE FROM {StagingTable} WHERE snapshot_id=$snapshot", cancellationToken, ("$snapshot", snapshot.SnapshotId.ToString("D"))).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, $"UPDATE {StateTable} SET staging_snapshot_id=NULL,staging_source_revision=NULL,staging_schema_id=NULL,staging_started_at=NULL WHERE collection_id=$collection AND staging_snapshot_id=$snapshot", cancellationToken,
            ("$collection", snapshot.CollectionId.ToString("D")), ("$snapshot", snapshot.SnapshotId.ToString("D"))).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(options.ConnectionString);
        connection.LoadOnnxTextEmbeddingsSqliteVec();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (options.ForeignKeys)
        {
            await using var command = connection.CreateCommand(); command.CommandText = "PRAGMA foreign_keys=ON";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        return connection;
    }

    private static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {StateTable}(
                collection_id TEXT PRIMARY KEY REFERENCES sk_collections(id) ON DELETE CASCADE,
                active_snapshot_id TEXT,
                source_revision TEXT,
                published_at TEXT,
                staging_snapshot_id TEXT,
                staging_source_revision TEXT,
                staging_schema_id TEXT,
                staging_started_at TEXT
            );
            CREATE TABLE IF NOT EXISTS {StagingTable}(
                snapshot_id TEXT NOT NULL,
                document_id TEXT NOT NULL,
                external_id TEXT,
                source_hash TEXT,
                reuse_existing INTEGER NOT NULL,
                payload_json TEXT,
                PRIMARY KEY(snapshot_id,document_id),
                UNIQUE(snapshot_id,external_id)
            );
            CREATE INDEX IF NOT EXISTS ix_sk_snapshot_documents_snapshot ON {StagingTable}(snapshot_id);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ValidateCollectionAsync(SqliteConnection connection, Guid knowledgeBaseId, Guid collectionId, Guid schemaId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sk_collections WHERE id=$collection AND knowledge_base_id=$kb; SELECT COUNT(*) FROM sk_schemas WHERE id=$schema;";
        command.Parameters.AddWithValue("$collection", collectionId.ToString("D")); command.Parameters.AddWithValue("$kb", knowledgeBaseId.ToString("D")); command.Parameters.AddWithValue("$schema", schemaId.ToString("D"));
        // Microsoft.Data.Sqlite does not expose multiple scalar result sets conveniently; keep the checks explicit.
        await using var collectionCheck = connection.CreateCommand(); collectionCheck.CommandText = "SELECT COUNT(*) FROM sk_collections WHERE id=$collection AND knowledge_base_id=$kb"; collectionCheck.Parameters.AddWithValue("$collection", collectionId.ToString("D")); collectionCheck.Parameters.AddWithValue("$kb", knowledgeBaseId.ToString("D"));
        if (Convert.ToInt32(await collectionCheck.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 1) throw new InvalidOperationException($"Collection {collectionId} does not belong to KnowledgeBase {knowledgeBaseId}.");
        await using var schemaCheck = connection.CreateCommand(); schemaCheck.CommandText = "SELECT COUNT(*) FROM sk_schemas WHERE id=$schema"; schemaCheck.Parameters.AddWithValue("$schema", schemaId.ToString("D"));
        if (Convert.ToInt32(await schemaCheck.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 1) throw new InvalidOperationException($"Schema {schemaId} is not registered.");
    }

    private static async Task<KnowledgeCollectionSnapshotState?> ReadStateAsync(SqliteConnection connection, SqliteTransaction transaction, Guid collectionId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = $"SELECT active_snapshot_id,source_revision,published_at,staging_snapshot_id,staging_source_revision FROM {StateTable} WHERE collection_id=$collection";
        command.Parameters.AddWithValue("$collection", collectionId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadState(collectionId, reader) : null;
    }

    private static KnowledgeCollectionSnapshotState ReadState(Guid collectionId, SqliteDataReader reader) => new()
    {
        CollectionId = collectionId,
        ActiveSnapshotId = reader.IsDBNull(0) ? null : Guid.Parse(reader.GetString(0)),
        SourceRevision = reader.IsDBNull(1) ? null : reader.GetString(1),
        PublishedAt = reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture),
        StagingSnapshotId = reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)),
        StagingSourceRevision = reader.IsDBNull(4) ? null : reader.GetString(4)
    };

    private static async Task EnsureStagingHandleAsync(SqliteConnection connection, KnowledgeCollectionSnapshotHandle snapshot, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {StateTable} WHERE collection_id=$collection AND staging_snapshot_id=$snapshot AND staging_schema_id=$schema";
        command.Parameters.AddWithValue("$collection", snapshot.CollectionId.ToString("D")); command.Parameters.AddWithValue("$snapshot", snapshot.SnapshotId.ToString("D")); command.Parameters.AddWithValue("$schema", snapshot.SchemaId.ToString("D"));
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 1) throw new InvalidOperationException("The Collection snapshot handle is no longer the active staging snapshot.");
    }

    private static void ValidatePrepared(KnowledgeCollectionSnapshotHandle snapshot, KnowledgeDocumentRecord document)
    {
        if (document.KnowledgeBaseId != snapshot.KnowledgeBaseId || document.CollectionId != snapshot.CollectionId || document.SchemaId != snapshot.SchemaId)
            throw new InvalidOperationException("Prepared snapshot document identity does not match the snapshot handle.");
        if (string.IsNullOrWhiteSpace(document.ExternalId)) throw new InvalidOperationException("Atomic source synchronization requires ExternalId for staged source documents.");
    }

    private static async Task InsertStageRowAsync(SqliteConnection connection, SqliteTransaction transaction, KnowledgeCollectionSnapshotHandle snapshot, KnowledgeDocumentRecord document, bool reuse, string? payload, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = $"INSERT INTO {StagingTable}(snapshot_id,document_id,external_id,source_hash,reuse_existing,payload_json) VALUES($snapshot,$document,$external,$hash,$reuse,$payload)";
        command.Parameters.AddWithValue("$snapshot", snapshot.SnapshotId.ToString("D")); command.Parameters.AddWithValue("$document", document.Id.ToString("D")); command.Parameters.AddWithValue("$external", (object?)document.ExternalId ?? DBNull.Value); command.Parameters.AddWithValue("$hash", (object?)document.SourceHash ?? DBNull.Value); command.Parameters.AddWithValue("$reuse", reuse ? 1 : 0); command.Parameters.AddWithValue("$payload", (object?)payload ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task StageLexicalAsync(SqliteConnection connection, SqliteTransaction transaction, KnowledgeCollectionSnapshotHandle snapshot, IReadOnlyList<LexicalSourceRecord> sources, CancellationToken cancellationToken)
    {
        if (!await LexicalTableExistsAsync(connection, transaction, cancellationToken).ConfigureAwait(false)) return;
        foreach (var source in sources)
        {
            await using var insert = connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = $"INSERT INTO {LexicalTable}(item_id,entity_kind,knowledge_base_id,collection_id,document_id,field_key,text_value) VALUES($item,$kind,$kb,$collection,$document,$field,$text)";
            insert.Parameters.AddWithValue("$item", source.ItemId.ToString("D")); insert.Parameters.AddWithValue("$kind", KnowledgeCollectionSnapshotStorage.PendingLexicalEntityKind); insert.Parameters.AddWithValue("$kb", snapshot.KnowledgeBaseId.ToString("D")); insert.Parameters.AddWithValue("$collection", snapshot.CollectionId.ToString("D")); insert.Parameters.AddWithValue("$document", (object?)source.DocumentId?.ToString("D") ?? DBNull.Value); insert.Parameters.AddWithValue("$field", source.FieldKey); insert.Parameters.AddWithValue("$text", source.Text);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task CopyExistingLexicalAsync(SqliteConnection connection, SqliteTransaction transaction, Guid documentId, CancellationToken cancellationToken)
    {
        if (!await LexicalTableExistsAsync(connection, transaction, cancellationToken).ConfigureAwait(false)) return;
        var rows = new List<(string Item, string Kb, string Collection, string? Document, string Field, string Text)>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = $"SELECT item_id,knowledge_base_id,collection_id,document_id,field_key,text_value FROM {LexicalTable} WHERE item_id=$item AND entity_kind=$kind";
            select.Parameters.AddWithValue("$item", documentId.ToString("D")); select.Parameters.AddWithValue("$kind", (int)SemanticEntityKind.Document);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        }
        foreach (var row in rows)
        {
            await using var insert = connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = $"INSERT INTO {LexicalTable}(item_id,entity_kind,knowledge_base_id,collection_id,document_id,field_key,text_value) VALUES($item,$kind,$kb,$collection,$document,$field,$text)";
            insert.Parameters.AddWithValue("$item", row.Item); insert.Parameters.AddWithValue("$kind", KnowledgeCollectionSnapshotStorage.PendingLexicalEntityKind); insert.Parameters.AddWithValue("$kb", row.Kb); insert.Parameters.AddWithValue("$collection", row.Collection); insert.Parameters.AddWithValue("$document", (object?)row.Document ?? DBNull.Value); insert.Parameters.AddWithValue("$field", row.Field); insert.Parameters.AddWithValue("$text", row.Text);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task DeletePendingLexicalAsync(SqliteConnection connection, SqliteTransaction transaction, Guid collectionId, CancellationToken cancellationToken)
    {
        if (!await LexicalTableExistsAsync(connection, transaction, cancellationToken).ConfigureAwait(false)) return;
        await ExecuteAsync(connection, transaction, $"DELETE FROM {LexicalTable} WHERE collection_id=$collection AND entity_kind=$kind", cancellationToken,
            ("$collection", collectionId.ToString("D")), ("$kind", KnowledgeCollectionSnapshotStorage.PendingLexicalEntityKind)).ConfigureAwait(false);
    }

    private static async Task<bool> LexicalTableExistsAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name"; command.Parameters.AddWithValue("$name", LexicalTable);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0;
    }

    private static async Task<KnowledgeDocumentRecord?> ReadDocumentIdentityAsync(SqliteConnection connection, Guid id, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT external_id,knowledge_base_id,collection_id,schema_id,title,description,source_hash FROM sk_documents WHERE id=$id"; command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return new KnowledgeDocumentRecord { Id = id, ExternalId = reader.IsDBNull(0) ? null : reader.GetString(0), KnowledgeBaseId = Guid.Parse(reader.GetString(1)), CollectionId = Guid.Parse(reader.GetString(2)), SchemaId = Guid.Parse(reader.GetString(3)), Title = reader.GetString(4), Description = reader.GetString(5), SourceHash = reader.IsDBNull(6) ? null : reader.GetString(6) };
    }

    private static async Task<IReadOnlyList<Guid>> GetDeletedDocumentIdsAsync(SqliteConnection connection, SqliteTransaction transaction, KnowledgeCollectionSnapshotHandle snapshot, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = $"SELECT d.id FROM sk_documents d WHERE d.collection_id=$collection AND NOT EXISTS(SELECT 1 FROM {StagingTable} s WHERE s.snapshot_id=$snapshot AND s.document_id=d.id)";
        command.Parameters.AddWithValue("$collection", snapshot.CollectionId.ToString("D")); command.Parameters.AddWithValue("$snapshot", snapshot.SnapshotId.ToString("D"));
        var ids = new List<Guid>(); await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) ids.Add(Guid.Parse(reader.GetString(0))); return ids;
    }

    private static async Task<IReadOnlyList<string>> ReadChangedPayloadsAsync(SqliteConnection connection, SqliteTransaction transaction, Guid snapshotId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = $"SELECT payload_json FROM {StagingTable} WHERE snapshot_id=$snapshot AND reuse_existing=0 ORDER BY document_id"; command.Parameters.AddWithValue("$snapshot", snapshotId.ToString("D"));
        var payloads = new List<string>(); await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) payloads.Add(reader.GetString(0)); return payloads;
    }

    private static async Task DeleteLiveDocumentAsync(SqliteConnection connection, SqliteTransaction transaction, string vectorTable, Guid documentId, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, "DELETE FROM sk_semantic_sources WHERE item_id=$id AND entity_kind=$kind", cancellationToken, ("$id", documentId.ToString("D")), ("$kind", (int)SemanticEntityKind.Document)).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, $"DELETE FROM {Quote(vectorTable)} WHERE item_id=$id AND item_kind='document'", cancellationToken, ("$id", documentId.ToString("D"))).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "DELETE FROM sk_documents WHERE id=$id", cancellationToken, ("$id", documentId.ToString("D"))).ConfigureAwait(false);
    }

    private static async Task UpsertLivePreparedAsync(SqliteConnection connection, SqliteTransaction transaction, VectorMetadata metadata, KnowledgePreparedSnapshotDocument prepared, CancellationToken cancellationToken)
    {
        var document = prepared.Document;
        await ExecuteAsync(connection, transaction, "DELETE FROM sk_semantic_sources WHERE item_id=$id AND entity_kind=$kind", cancellationToken, ("$id", document.Id.ToString("D")), ("$kind", (int)SemanticEntityKind.Document)).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, $"DELETE FROM {Quote(metadata.VectorTable)} WHERE item_id=$id AND item_kind='document'", cancellationToken, ("$id", document.Id.ToString("D"))).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "DELETE FROM sk_document_tags WHERE document_id=$id", cancellationToken, ("$id", document.Id.ToString("D"))).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "DELETE FROM sk_document_values WHERE document_id=$id", cancellationToken, ("$id", document.Id.ToString("D"))).ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO sk_documents(id,external_id,knowledge_base_id,collection_id,schema_id,title,description,source_hash) VALUES($id,$external,$kb,$collection,$schema,$title,$description,$hash) ON CONFLICT(id) DO UPDATE SET external_id=excluded.external_id,knowledge_base_id=excluded.knowledge_base_id,collection_id=excluded.collection_id,schema_id=excluded.schema_id,title=excluded.title,description=excluded.description,source_hash=excluded.source_hash";
            command.Parameters.AddWithValue("$id", document.Id.ToString("D")); command.Parameters.AddWithValue("$external", (object?)document.ExternalId ?? DBNull.Value); command.Parameters.AddWithValue("$kb", document.KnowledgeBaseId.ToString("D")); command.Parameters.AddWithValue("$collection", document.CollectionId.ToString("D")); command.Parameters.AddWithValue("$schema", document.SchemaId.ToString("D")); command.Parameters.AddWithValue("$title", document.Title); command.Parameters.AddWithValue("$description", document.Description); command.Parameters.AddWithValue("$hash", (object?)document.SourceHash ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        foreach (var tag in document.Tags) await ExecuteAsync(connection, transaction, "INSERT INTO sk_document_tags(document_id,tag) VALUES($id,$tag)", cancellationToken, ("$id", document.Id.ToString("D")), ("$tag", tag)).ConfigureAwait(false);
        foreach (var pair in document.Values) await InsertValueAsync(connection, transaction, document.Id, pair.Key, pair.Value, cancellationToken).ConfigureAwait(false);
        foreach (var source in prepared.SemanticSources) await InsertSemanticAsync(connection, transaction, metadata, source, cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertSemanticAsync(SqliteConnection connection, SqliteTransaction transaction, VectorMetadata metadata, SemanticSourceRecord source, CancellationToken cancellationToken)
    {
        var json = EmbeddingSerializer.SerializeJson(source.Embedding);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO sk_semantic_sources(id,item_id,entity_kind,knowledge_base_id,collection_id,document_id,field_id,field_key,field_weight,fingerprint,token_count,char_start,char_length,record_json) VALUES($id,$item,$kind,$kb,$collection,$document,$field,$key,$weight,$fingerprint,$tokens,$start,$length,$json)";
            command.Parameters.AddWithValue("$id", source.Id.ToString("D")); command.Parameters.AddWithValue("$item", source.ItemId.ToString("D")); command.Parameters.AddWithValue("$kind", (int)source.EntityKind); command.Parameters.AddWithValue("$kb", source.KnowledgeBaseId.ToString("D")); command.Parameters.AddWithValue("$collection", source.CollectionId.ToString("D")); command.Parameters.AddWithValue("$document", (object?)source.DocumentId?.ToString("D") ?? DBNull.Value); command.Parameters.AddWithValue("$field", source.FieldId.ToString("D")); command.Parameters.AddWithValue("$key", source.FieldKey); command.Parameters.AddWithValue("$weight", source.ScorerWeight); command.Parameters.AddWithValue("$fingerprint", source.Embedding.Identity.EmbeddingSpaceFingerprint); command.Parameters.AddWithValue("$tokens", source.Embedding.Source.TokenCount); command.Parameters.AddWithValue("$start", source.Embedding.Source.CharacterRange.Start); command.Parameters.AddWithValue("$length", source.Embedding.Source.CharacterRange.Length); command.Parameters.AddWithValue("$json", json);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        var vectorFormat = metadata.StorageKind == SqliteVecStorageKind.Int8 ? EmbeddingVectorFormat.Int8 : EmbeddingVectorFormat.Float32;
        var constructor = metadata.StorageKind == SqliteVecStorageKind.Int8 ? "vec_int8" : "vec_f32";
        var data = source.Embedding.Vector.ConvertTo(vectorFormat).Data;
        await using var vector = connection.CreateCommand(); vector.Transaction = transaction;
        vector.CommandText = $"INSERT INTO {Quote(metadata.VectorTable)}(item_id,item_kind,field_name,fingerprint,knowledge_base_id,collection_id,embedding,record_json,field_weight) VALUES($item,'document',$field,$fingerprint,$kb,$collection,{constructor}($vector),$json,$weight)";
        vector.Parameters.AddWithValue("$item", source.ItemId.ToString("D")); vector.Parameters.AddWithValue("$field", source.FieldKey); vector.Parameters.AddWithValue("$fingerprint", source.Embedding.Identity.EmbeddingSpaceFingerprint); vector.Parameters.AddWithValue("$kb", source.KnowledgeBaseId.ToString("D")); vector.Parameters.AddWithValue("$collection", source.CollectionId.ToString("D")); vector.Parameters.Add("$vector", SqliteType.Blob).Value = data; vector.Parameters.AddWithValue("$json", json); vector.Parameters.AddWithValue("$weight", source.ScorerWeight);
        await vector.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertValueAsync(SqliteConnection connection, SqliteTransaction transaction, Guid documentId, string key, KnowledgeValue value, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO sk_document_values(document_id,field_key,value_type,text_value,int_value,decimal_value,bool_value,datetime_value,guid_value) VALUES($doc,$key,$type,$text,$int,$decimal,$bool,$datetime,$guid)";
        command.Parameters.AddWithValue("$doc", documentId.ToString("D")); command.Parameters.AddWithValue("$key", key); command.Parameters.AddWithValue("$type", (int)value.Type); command.Parameters.AddWithValue("$text", (object?)value.Text ?? DBNull.Value); command.Parameters.AddWithValue("$int", (object?)value.Int64 ?? DBNull.Value); command.Parameters.AddWithValue("$decimal", value.Decimal is { } d ? (double)d : DBNull.Value); command.Parameters.AddWithValue("$bool", value.Boolean is { } b ? b ? 1 : 0 : DBNull.Value); command.Parameters.AddWithValue("$datetime", value.DateTimeOffset is { } dto ? dto.ToUniversalTime().ToString("O") : DBNull.Value); command.Parameters.AddWithValue("$guid", value.Guid is { } guid ? guid.ToString("D") : DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<VectorMetadata> ReadVectorMetadataAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT active_vector_table,active_storage_kind FROM sk_store_metadata WHERE singleton=1";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("SemanticKnowledge SQLite metadata is missing."); return new VectorMetadata(reader.GetString(0), (SqliteVecStorageKind)reader.GetInt32(1));
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Quote(string name) => '"' + name.Replace("\"", "\"\"") + '"';
    private sealed record VectorMetadata(string VectorTable, SqliteVecStorageKind StorageKind);
}
