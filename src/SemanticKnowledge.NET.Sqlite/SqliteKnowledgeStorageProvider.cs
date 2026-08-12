using System.Data;
using Microsoft.Data.Sqlite;
using OnnxTextEmbeddings;
using OnnxTextEmbeddings.SqliteVec;

namespace SemanticKnowledge.Sqlite;

internal sealed class SqliteKnowledgeStorageProvider(
    SemanticKnowledgeSqliteOptions options,
    SqliteVecSemanticSearch semanticSearch) : IKnowledgeStorageProvider
{
    private const int EngineVersion = 1;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _activeVectorTable;
    private string? _writeVectorTable;
    private string? _pendingVectorTable;
    private SqliteVecStorageKind _activeStorageKind;
    private SqliteVecStorageKind _writeStorageKind;
    private KnowledgeStorageInitialization? _initialization;

    public async Task<KnowledgeProviderCapabilities> InitializeAsync(KnowledgeStorageInitialization initialization, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(initialization);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _initialization = initialization;
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await CreateBaseSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            var requestedStorage = initialization.StoragePreference == VectorStoragePreference.MaximumPrecision
                ? SqliteVecStorageKind.Float32
                : SqliteVecStorageKind.Int8;

            var metadata = await ReadMetadataAsync(connection, cancellationToken).ConfigureAwait(false);
            var requiresRebuild = false;
            if (metadata is null)
            {
                var generation = Guid.NewGuid();
                var table = VectorTableName(generation);
                await CreateVectorTableAsync(connection, table, initialization.Embedding.OutputDimensions, requestedStorage, cancellationToken).ConfigureAwait(false);
                await InsertMetadataAsync(connection, initialization, generation, table, requestedStorage, cancellationToken).ConfigureAwait(false);
                _activeVectorTable = _writeVectorTable = table;
                _activeStorageKind = _writeStorageKind = requestedStorage;
            }
            else
            {
                if (metadata.DatabaseVersion != initialization.DatabaseVersion)
                    throw new InvalidOperationException($"Stored SemanticKnowledge database version is {metadata.DatabaseVersion}, configured version is {initialization.DatabaseVersion}. Register/apply a logical migration before opening this store.");

                _activeVectorTable = metadata.ActiveVectorTable;
                _activeStorageKind = metadata.ActiveStorageKind;
                var matches = metadata.ActiveDimensions == initialization.Embedding.OutputDimensions
                    && metadata.ActiveStorageKind == requestedStorage
                    && string.Equals(metadata.ActiveFingerprint, initialization.Embedding.EmbeddingSpaceFingerprint, StringComparison.Ordinal);

                if (matches)
                {
                    _writeVectorTable = _activeVectorTable;
                    _writeStorageKind = _activeStorageKind;
                    if (!string.IsNullOrWhiteSpace(metadata.PendingVectorTable))
                        await DropVectorTableIfExistsAsync(connection, metadata.PendingVectorTable!, cancellationToken).ConfigureAwait(false);
                    await ClearPendingMetadataAsync(connection, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(metadata.PendingVectorTable))
                        await DropVectorTableIfExistsAsync(connection, metadata.PendingVectorTable!, cancellationToken).ConfigureAwait(false);
                    var generation = Guid.NewGuid();
                    var table = VectorTableName(generation);
                    await CreateVectorTableAsync(connection, table, initialization.Embedding.OutputDimensions, requestedStorage, cancellationToken).ConfigureAwait(false);
                    await SetPendingMetadataAsync(connection, initialization, generation, table, requestedStorage, cancellationToken).ConfigureAwait(false);
                    _pendingVectorTable = table;
                    _writeVectorTable = table;
                    _writeStorageKind = requestedStorage;
                    requiresRebuild = true;
                }
            }

            _ = await connection.GetSqliteVecCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            return new KnowledgeProviderCapabilities
            {
                Provider = "SQLite/sqlite-vec",
                MaxDimensions = int.MaxValue,
                PhysicalVectorStorage = (_writeStorageKind == SqliteVecStorageKind.Int8 ? "INT8" : "FP32"),
                ExactVectorSearch = true,
                ApproximateVectorSearch = false,
                NativeAotSupported = true,
                RequiresEmbeddingRebuild = requiresRebuild
            };
        }
        finally { _gate.Release(); }
    }

    public async Task CompleteEmbeddingRebuildAsync(CancellationToken cancellationToken = default)
    {
        if (_pendingVectorTable is null) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var old = _activeVectorTable;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = """
                    UPDATE sk_store_metadata
                    SET active_generation=pending_generation,
                        active_vector_table=pending_vector_table,
                        active_fingerprint=pending_fingerprint,
                        active_dimensions=pending_dimensions,
                        active_storage_kind=pending_storage_kind,
                        pending_generation=NULL,pending_vector_table=NULL,pending_fingerprint=NULL,pending_dimensions=NULL,pending_storage_kind=NULL
                    WHERE singleton=1;
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _activeVectorTable = _pendingVectorTable;
            _activeStorageKind = _writeStorageKind;
            _pendingVectorTable = null;
            _writeVectorTable = _activeVectorTable;
            if (!string.IsNullOrWhiteSpace(old) && !string.Equals(old, _activeVectorTable, StringComparison.Ordinal))
                await DropVectorTableIfExistsAsync(connection, old!, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<KnowledgeBaseRecord> GetOrCreateKnowledgeBaseAsync(string title, string? externalId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var found = await FindKnowledgeBaseAsync(connection, title, externalId, cancellationToken).ConfigureAwait(false);
        if (found is not null) return found;
        var record = new KnowledgeBaseRecord { Id = Guid.NewGuid(), ExternalId = externalId, Title = title.Trim() };
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO sk_knowledge_bases(id,external_id,title,description) VALUES($id,$external,$title,'')";
        command.Parameters.AddWithValue("$id", record.Id.ToString("D"));
        command.Parameters.AddWithValue("$external", (object?)record.ExternalId ?? DBNull.Value);
        command.Parameters.AddWithValue("$title", record.Title);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    public async Task<KnowledgeCollectionRecord> GetOrCreateCollectionAsync(Guid knowledgeBaseId, string title, Guid? parentCollectionId = null, Guid? defaultSchemaId = null, string? externalId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var found = await FindCollectionAsync(connection, knowledgeBaseId, title, parentCollectionId, externalId, cancellationToken).ConfigureAwait(false);
        if (found is not null) return found;
        var record = new KnowledgeCollectionRecord { Id = Guid.NewGuid(), KnowledgeBaseId = knowledgeBaseId, ParentCollectionId = parentCollectionId, DefaultSchemaId = defaultSchemaId, ExternalId = externalId, Title = title.Trim() };
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO sk_collections(id,knowledge_base_id,parent_collection_id,default_schema_id,external_id,title,description) VALUES($id,$kb,$parent,$schema,$external,$title,'')";
        AddGuid(command, "$id", record.Id); AddGuid(command, "$kb", knowledgeBaseId); AddNullableGuid(command, "$parent", parentCollectionId); AddNullableGuid(command, "$schema", defaultSchemaId);
        command.Parameters.AddWithValue("$external", (object?)externalId ?? DBNull.Value); command.Parameters.AddWithValue("$title", record.Title);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    public async Task<IReadOnlyList<KnowledgeCollectionRecord>> GetCollectionsAsync(Guid? knowledgeBaseId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,knowledge_base_id,parent_collection_id,default_schema_id,external_id,title,description FROM sk_collections" + (knowledgeBaseId is null ? "" : " WHERE knowledge_base_id=$kb") + " ORDER BY title";
        if (knowledgeBaseId is { } kb) AddGuid(command, "$kb", kb);
        var list = new List<KnowledgeCollectionRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) list.Add(new KnowledgeCollectionRecord { Id = Guid.Parse(reader.GetString(0)), KnowledgeBaseId = Guid.Parse(reader.GetString(1)), ParentCollectionId = ReadNullableGuid(reader, 2), DefaultSchemaId = ReadNullableGuid(reader, 3), ExternalId = reader.IsDBNull(4) ? null : reader.GetString(4), Title = reader.GetString(5), Description = reader.GetString(6), Tags = await ReadCollectionTagsAsync(connection, Guid.Parse(reader.GetString(0)), cancellationToken).ConfigureAwait(false) });
        return list;
    }

    public async Task UpsertCollectionSemanticSourcesAsync(KnowledgeCollectionRecord collection, IReadOnlyList<SemanticSourceRecord> semanticSources, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await DeleteSemanticRowsAsync(connection, (SqliteTransaction)transaction, collection.Id, SemanticEntityKind.Collection, cancellationToken).ConfigureAwait(false);
        foreach (var source in semanticSources) await InsertSemanticSourceAsync(connection, (SqliteTransaction)transaction, source, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpsertSchemaAsync(KnowledgeSchemaDefinition schema, CancellationToken cancellationToken = default)
    {
        schema.Validate();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "INSERT INTO sk_schemas(id,schema_key,display_name,revision) VALUES($id,$key,$name,$revision) ON CONFLICT(id) DO UPDATE SET schema_key=excluded.schema_key,display_name=excluded.display_name,revision=excluded.revision";
            AddGuid(command, "$id", schema.Id); command.Parameters.AddWithValue("$key", schema.Key); command.Parameters.AddWithValue("$name", schema.DisplayName); command.Parameters.AddWithValue("$revision", schema.Revision);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await ExecuteAsync(connection, (SqliteTransaction)transaction, "DELETE FROM sk_schema_fields WHERE schema_id=$id", cancellationToken, ("$id", schema.Id.ToString("D"))).ConfigureAwait(false);
        foreach (var field in schema.Fields)
        {
            await using var command = connection.CreateCommand(); command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "INSERT INTO sk_schema_fields(id,schema_id,field_key,display_name,field_type,required,system,filterable,semantic_mode,semantic_weight,ordinal) VALUES($id,$schema,$key,$name,$type,$required,$system,$filterable,$mode,$weight,$ordinal)";
            AddGuid(command, "$id", field.Id); AddGuid(command, "$schema", schema.Id); command.Parameters.AddWithValue("$key", field.Key); command.Parameters.AddWithValue("$name", field.DisplayName); command.Parameters.AddWithValue("$type", (int)field.Type); command.Parameters.AddWithValue("$required", field.Required ? 1 : 0); command.Parameters.AddWithValue("$system", field.System ? 1 : 0); command.Parameters.AddWithValue("$filterable", field.Filterable ? 1 : 0); command.Parameters.AddWithValue("$mode", (int)field.SemanticMode); command.Parameters.AddWithValue("$weight", field.SemanticWeightPercent); command.Parameters.AddWithValue("$ordinal", schema.Fields.IndexOf(field));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<KnowledgeSchemaDefinition?> GetSchemaAsync(Guid schemaId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT schema_key,display_name,revision FROM sk_schemas WHERE id=$id"; AddGuid(command, "$id", schemaId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var key = reader.GetString(0); var name = reader.GetString(1); var revision = reader.GetInt32(2); await reader.DisposeAsync().ConfigureAwait(false);
        await using var fieldsCommand = connection.CreateCommand(); fieldsCommand.CommandText = "SELECT id,field_key,display_name,field_type,required,system,filterable,semantic_mode,semantic_weight FROM sk_schema_fields WHERE schema_id=$id ORDER BY ordinal"; AddGuid(fieldsCommand, "$id", schemaId);
        var fields = new List<KnowledgeSchemaField>(); await using var fieldsReader = await fieldsCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await fieldsReader.ReadAsync(cancellationToken).ConfigureAwait(false)) fields.Add(new KnowledgeSchemaField { Id = Guid.Parse(fieldsReader.GetString(0)), Key = fieldsReader.GetString(1), DisplayName = fieldsReader.GetString(2), Type = (KnowledgeFieldType)fieldsReader.GetInt32(3), Required = fieldsReader.GetInt32(4) != 0, System = fieldsReader.GetInt32(5) != 0, Filterable = fieldsReader.GetInt32(6) != 0, SemanticMode = (SemanticMode)fieldsReader.GetInt32(7), SemanticWeightPercent = fieldsReader.GetInt32(8) });
        return new KnowledgeSchemaDefinition { Id = schemaId, Key = key, DisplayName = name, Revision = revision, Fields = fields };
    }

    public async Task UpsertDocumentAsync(KnowledgeDocumentRecord document, IReadOnlyList<SemanticSourceRecord> semanticSources, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "INSERT INTO sk_documents(id,external_id,knowledge_base_id,collection_id,schema_id,title,description,source_hash) VALUES($id,$external,$kb,$collection,$schema,$title,$description,$hash) ON CONFLICT(id) DO UPDATE SET external_id=excluded.external_id,knowledge_base_id=excluded.knowledge_base_id,collection_id=excluded.collection_id,schema_id=excluded.schema_id,title=excluded.title,description=excluded.description,source_hash=excluded.source_hash";
            AddGuid(command, "$id", document.Id); command.Parameters.AddWithValue("$external", (object?)document.ExternalId ?? DBNull.Value); AddGuid(command, "$kb", document.KnowledgeBaseId); AddGuid(command, "$collection", document.CollectionId); AddGuid(command, "$schema", document.SchemaId); command.Parameters.AddWithValue("$title", document.Title); command.Parameters.AddWithValue("$description", document.Description); command.Parameters.AddWithValue("$hash", (object?)document.SourceHash ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await ExecuteAsync(connection, (SqliteTransaction)transaction, "DELETE FROM sk_document_tags WHERE document_id=$id", cancellationToken, ("$id", document.Id.ToString("D"))).ConfigureAwait(false);
        await ExecuteAsync(connection, (SqliteTransaction)transaction, "DELETE FROM sk_document_values WHERE document_id=$id", cancellationToken, ("$id", document.Id.ToString("D"))).ConfigureAwait(false);
        foreach (var tag in document.Tags) await ExecuteAsync(connection, (SqliteTransaction)transaction, "INSERT INTO sk_document_tags(document_id,tag) VALUES($id,$tag)", cancellationToken, ("$id", document.Id.ToString("D")), ("$tag", tag)).ConfigureAwait(false);
        foreach (var pair in document.Values) await InsertValueAsync(connection, (SqliteTransaction)transaction, document.Id, pair.Key, pair.Value, cancellationToken).ConfigureAwait(false);
        await DeleteSemanticRowsAsync(connection, (SqliteTransaction)transaction, document.Id, SemanticEntityKind.Document, cancellationToken).ConfigureAwait(false);
        foreach (var source in semanticSources) await InsertSemanticSourceAsync(connection, (SqliteTransaction)transaction, source, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<KnowledgeDocumentRecord?> GetDocumentAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadDocumentAsync(connection, documentId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<KnowledgeDocumentRecord>> GetDocumentsAsync(Guid? knowledgeBaseId = null, CancellationToken cancellationToken = default)
    {
        var ids = new List<Guid>(); await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id FROM sk_documents" + (knowledgeBaseId is null ? "" : " WHERE knowledge_base_id=$kb"); if (knowledgeBaseId is { } kb) AddGuid(command, "$kb", kb);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) ids.Add(Guid.Parse(reader.GetString(0)));
        }
        var result = new List<KnowledgeDocumentRecord>(ids.Count); foreach (var id in ids) if (await ReadDocumentAsync(connection, id, cancellationToken).ConfigureAwait(false) is { } document) result.Add(document); return result;
    }

    public async Task<IReadOnlyList<KnowledgeSearchHit>> SearchAsync(QueryEmbedding query, KnowledgeSearchRequest request, CancellationToken cancellationToken = default)
    {
        var table = _activeVectorTable ?? throw new InvalidOperationException("SQLite provider is not initialized.");
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var collectionIds = request.CollectionIds.ToArray();
        if (request.Mode == KnowledgeSearchMode.Smart)
        {
            var route = await semanticSearch.SearchAsync<Guid>(connection, query, CandidateQuery(table, "item_kind='collection' AND knowledge_base_id=$sk_kb", _activeStorageKind), new DatabaseSemanticSearchOptions { Top = 8, CandidateCount = 100 }, command => AddGuid(command, "$sk_kb", request.KnowledgeBaseId), cancellationToken).ConfigureAwait(false);
            collectionIds = route.Results.Select(x => x.Item).Distinct().ToArray();
        }

        var compiled = SqliteFilterCompiler.Compile(request.Filter);
        var scope = BuildScopeSql(collectionIds, request.IncludeDescendants, out var scopeParameters);
        var documentWhere = $"item_kind='document' AND knowledge_base_id=$sk_kb AND item_id IN (SELECT d.id FROM sk_documents d WHERE d.knowledge_base_id=$sk_kb AND ({compiled.Sql}){scope})";
        var search = await semanticSearch.SearchAsync<Guid>(connection, query, CandidateQuery(table, documentWhere, _activeStorageKind), new DatabaseSemanticSearchOptions { Top = request.Top, CandidateCount = request.CandidateCount }, command =>
        {
            AddGuid(command, "$sk_kb", request.KnowledgeBaseId); foreach (var p in compiled.Parameters) command.Parameters.Add(Clone(p)); foreach (var p in scopeParameters) command.Parameters.Add(Clone(p));
        }, cancellationToken).ConfigureAwait(false);

        var hits = new List<KnowledgeSearchHit>(search.Results.Count);
        foreach (var result in search.Results)
        {
            var document = await ReadDocumentAsync(connection, result.Item, cancellationToken).ConfigureAwait(false); if (document is null) continue;
            var matches = result.Fields.SelectMany(field => field.Matches.Select(match => new KnowledgeMatchedChunk { FieldKey = field.Name, RawSimilarity = match.RawSimilarity, AdjustedSimilarity = match.AdjustedSimilarity, TokenCount = match.Embedding.Source.TokenCount, CharacterRange = match.Embedding.Source.CharacterRange, Text = request.Include == KnowledgeResultInclude.MetadataOnly ? null : match.Embedding.Text })).ToArray();
            hits.Add(new KnowledgeSearchHit { DocumentId = document.Id, CollectionId = document.CollectionId, SchemaId = document.SchemaId, Score = result.Score, Title = document.Title, Description = document.Description, Tags = document.Tags, Matches = matches, Scoring = result.Scoring });
        }
        return hits;
    }

    public async Task DeleteDocumentAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false); await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await DeleteSemanticRowsAsync(connection, (SqliteTransaction)transaction, documentId, SemanticEntityKind.Document, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, (SqliteTransaction)transaction, "DELETE FROM sk_documents WHERE id=$id", cancellationToken, ("$id", documentId.ToString("D"))).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var metadata = await ReadMetadataAsync(connection, cancellationToken).ConfigureAwait(false);
        if (metadata is not null)
        {
            await DropVectorTableIfExistsAsync(connection, metadata.ActiveVectorTable, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(metadata.PendingVectorTable)) await DropVectorTableIfExistsAsync(connection, metadata.PendingVectorTable!, cancellationToken).ConfigureAwait(false);
        }
        await using var command = connection.CreateCommand(); command.CommandText = "DROP TABLE IF EXISTS sk_semantic_sources; DROP TABLE IF EXISTS sk_document_values; DROP TABLE IF EXISTS sk_document_tags; DROP TABLE IF EXISTS sk_documents; DROP TABLE IF EXISTS sk_schema_fields; DROP TABLE IF EXISTS sk_schemas; DROP TABLE IF EXISTS sk_collection_tags; DROP TABLE IF EXISTS sk_collections; DROP TABLE IF EXISTS sk_knowledge_bases; DROP TABLE IF EXISTS sk_store_metadata;"; await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        _activeVectorTable = _writeVectorTable = _pendingVectorTable = null;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(options.ConnectionString); connection.LoadOnnxTextEmbeddingsSqliteVec(); await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (options.ForeignKeys) { await using var command = connection.CreateCommand(); command.CommandText = "PRAGMA foreign_keys=ON"; await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
        return connection;
    }

    private static async Task CreateBaseSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.CommandText = """
            CREATE TABLE IF NOT EXISTS sk_store_metadata(singleton INTEGER PRIMARY KEY CHECK(singleton=1),store_id TEXT NOT NULL,engine_version INTEGER NOT NULL,database_version INTEGER NOT NULL,persistence_mode INTEGER NOT NULL,active_generation TEXT NOT NULL,active_vector_table TEXT NOT NULL,active_fingerprint TEXT NOT NULL,active_dimensions INTEGER NOT NULL,active_storage_kind INTEGER NOT NULL,pending_generation TEXT,pending_vector_table TEXT,pending_fingerprint TEXT,pending_dimensions INTEGER,pending_storage_kind INTEGER);
            CREATE TABLE IF NOT EXISTS sk_knowledge_bases(id TEXT PRIMARY KEY,external_id TEXT UNIQUE,title TEXT NOT NULL,description TEXT NOT NULL DEFAULT '');
            CREATE TABLE IF NOT EXISTS sk_collections(id TEXT PRIMARY KEY,knowledge_base_id TEXT NOT NULL REFERENCES sk_knowledge_bases(id) ON DELETE CASCADE,parent_collection_id TEXT REFERENCES sk_collections(id) ON DELETE CASCADE,default_schema_id TEXT,external_id TEXT,title TEXT NOT NULL,description TEXT NOT NULL DEFAULT '');
            CREATE UNIQUE INDEX IF NOT EXISTS ix_sk_collections_external ON sk_collections(knowledge_base_id,external_id) WHERE external_id IS NOT NULL;
            CREATE INDEX IF NOT EXISTS ix_sk_collections_parent ON sk_collections(parent_collection_id);
            CREATE TABLE IF NOT EXISTS sk_collection_tags(collection_id TEXT NOT NULL REFERENCES sk_collections(id) ON DELETE CASCADE,tag TEXT NOT NULL COLLATE NOCASE,PRIMARY KEY(collection_id,tag));
            CREATE TABLE IF NOT EXISTS sk_schemas(id TEXT PRIMARY KEY,schema_key TEXT NOT NULL UNIQUE,display_name TEXT NOT NULL,revision INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS sk_schema_fields(id TEXT PRIMARY KEY,schema_id TEXT NOT NULL REFERENCES sk_schemas(id) ON DELETE CASCADE,field_key TEXT NOT NULL,display_name TEXT NOT NULL,field_type INTEGER NOT NULL,required INTEGER NOT NULL,system INTEGER NOT NULL,filterable INTEGER NOT NULL,semantic_mode INTEGER NOT NULL,semantic_weight INTEGER NOT NULL,ordinal INTEGER NOT NULL,UNIQUE(schema_id,field_key));
            CREATE TABLE IF NOT EXISTS sk_documents(id TEXT PRIMARY KEY,external_id TEXT,knowledge_base_id TEXT NOT NULL REFERENCES sk_knowledge_bases(id) ON DELETE CASCADE,collection_id TEXT NOT NULL REFERENCES sk_collections(id) ON DELETE CASCADE,schema_id TEXT NOT NULL REFERENCES sk_schemas(id),title TEXT NOT NULL,description TEXT NOT NULL DEFAULT '',source_hash TEXT);
            CREATE UNIQUE INDEX IF NOT EXISTS ix_sk_documents_external ON sk_documents(knowledge_base_id,external_id) WHERE external_id IS NOT NULL;
            CREATE INDEX IF NOT EXISTS ix_sk_documents_collection ON sk_documents(collection_id);
            CREATE TABLE IF NOT EXISTS sk_document_tags(document_id TEXT NOT NULL REFERENCES sk_documents(id) ON DELETE CASCADE,tag TEXT NOT NULL COLLATE NOCASE,PRIMARY KEY(document_id,tag));
            CREATE INDEX IF NOT EXISTS ix_sk_document_tags_tag ON sk_document_tags(tag);
            CREATE TABLE IF NOT EXISTS sk_document_values(document_id TEXT NOT NULL REFERENCES sk_documents(id) ON DELETE CASCADE,field_key TEXT NOT NULL,value_type INTEGER NOT NULL,text_value TEXT,int_value INTEGER,decimal_value REAL,bool_value INTEGER,datetime_value TEXT,guid_value TEXT,PRIMARY KEY(document_id,field_key));
            CREATE INDEX IF NOT EXISTS ix_sk_values_text ON sk_document_values(field_key,text_value);
            CREATE INDEX IF NOT EXISTS ix_sk_values_int ON sk_document_values(field_key,int_value);
            CREATE INDEX IF NOT EXISTS ix_sk_values_decimal ON sk_document_values(field_key,decimal_value);
            CREATE INDEX IF NOT EXISTS ix_sk_values_bool ON sk_document_values(field_key,bool_value);
            CREATE INDEX IF NOT EXISTS ix_sk_values_datetime ON sk_document_values(field_key,datetime_value);
            CREATE INDEX IF NOT EXISTS ix_sk_values_guid ON sk_document_values(field_key,guid_value);
            CREATE TABLE IF NOT EXISTS sk_semantic_sources(id TEXT PRIMARY KEY,item_id TEXT NOT NULL,entity_kind INTEGER NOT NULL,knowledge_base_id TEXT NOT NULL,collection_id TEXT NOT NULL,document_id TEXT,field_id TEXT NOT NULL,field_key TEXT NOT NULL,field_weight REAL NOT NULL,fingerprint TEXT NOT NULL,token_count INTEGER NOT NULL,char_start INTEGER NOT NULL,char_length INTEGER NOT NULL,record_json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_sk_semantic_item ON sk_semantic_sources(item_id,entity_kind);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task CreateVectorTableAsync(SqliteConnection connection, string table, int dimensions, SqliteVecStorageKind storage, CancellationToken cancellationToken)
    {
        if (dimensions <= 0) throw new InvalidOperationException("Vector dimensions must be greater than zero.");
        var vectorType = storage == SqliteVecStorageKind.Int8 ? $"int8[{dimensions}]" : $"float[{dimensions}]";
        await using var command = connection.CreateCommand(); command.CommandText = $"CREATE VIRTUAL TABLE {Quote(table)} USING vec0(item_id text,item_kind text,field_name text,fingerprint text,knowledge_base_id text,collection_id text,embedding {vectorType} distance_metric=cosine,+record_json text,+field_weight float)"; await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task InsertSemanticSourceAsync(SqliteConnection connection, SqliteTransaction transaction, SemanticSourceRecord source, CancellationToken cancellationToken)
    {
        var table = _writeVectorTable ?? throw new InvalidOperationException("No writable vector generation is configured.");
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction; command.CommandText = "INSERT INTO sk_semantic_sources(id,item_id,entity_kind,knowledge_base_id,collection_id,document_id,field_id,field_key,field_weight,fingerprint,token_count,char_start,char_length,record_json) VALUES($id,$item,$kind,$kb,$collection,$document,$field,$key,$weight,$fingerprint,$tokens,$start,$length,$json)";
            AddGuid(command, "$id", source.Id); AddGuid(command, "$item", source.ItemId); command.Parameters.AddWithValue("$kind", (int)source.EntityKind); AddGuid(command, "$kb", source.KnowledgeBaseId); AddGuid(command, "$collection", source.CollectionId); AddNullableGuid(command, "$document", source.DocumentId); AddGuid(command, "$field", source.FieldId); command.Parameters.AddWithValue("$key", source.FieldKey); command.Parameters.AddWithValue("$weight", source.ScorerWeight); command.Parameters.AddWithValue("$fingerprint", source.Embedding.Identity.EmbeddingSpaceFingerprint); command.Parameters.AddWithValue("$tokens", source.Embedding.Source.TokenCount); command.Parameters.AddWithValue("$start", source.Embedding.Source.CharacterRange.Start); command.Parameters.AddWithValue("$length", source.Embedding.Source.CharacterRange.Length); command.Parameters.AddWithValue("$json", EmbeddingSerializer.SerializeJson(source.Embedding)); await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction; var constructor = _writeStorageKind == SqliteVecStorageKind.Int8 ? "vec_int8" : "vec_f32"; var data = source.Embedding.Vector.ConvertTo(_writeStorageKind == SqliteVecStorageKind.Int8 ? EmbeddingVectorFormat.Int8 : EmbeddingVectorFormat.Float32).Data;
            command.CommandText = $"INSERT INTO {Quote(table)}(item_id,item_kind,field_name,fingerprint,knowledge_base_id,collection_id,embedding,record_json,field_weight) VALUES($item,$kind,$field,$fingerprint,$kb,$collection,{constructor}($vector),$json,$weight)";
            AddGuid(command, "$item", source.ItemId); command.Parameters.AddWithValue("$kind", source.EntityKind == SemanticEntityKind.Document ? "document" : "collection"); command.Parameters.AddWithValue("$field", source.FieldKey); command.Parameters.AddWithValue("$fingerprint", source.Embedding.Identity.EmbeddingSpaceFingerprint); AddGuid(command, "$kb", source.KnowledgeBaseId); AddGuid(command, "$collection", source.CollectionId); command.Parameters.Add("$vector", SqliteType.Blob).Value = data; command.Parameters.AddWithValue("$json", EmbeddingSerializer.SerializeJson(source.Embedding)); command.Parameters.AddWithValue("$weight", source.ScorerWeight); await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DeleteSemanticRowsAsync(SqliteConnection connection, SqliteTransaction transaction, Guid itemId, SemanticEntityKind kind, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, "DELETE FROM sk_semantic_sources WHERE item_id=$id AND entity_kind=$kind", cancellationToken, ("$id", itemId.ToString("D")), ("$kind", (int)kind)).ConfigureAwait(false);
        if (_writeVectorTable is { } table) await ExecuteAsync(connection, transaction, $"DELETE FROM {Quote(table)} WHERE item_id=$id AND item_kind=$kind", cancellationToken, ("$id", itemId.ToString("D")), ("$kind", kind == SemanticEntityKind.Document ? "document" : "collection")).ConfigureAwait(false);
    }

    private static async Task InsertValueAsync(SqliteConnection connection, SqliteTransaction transaction, Guid documentId, string key, KnowledgeValue value, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "INSERT INTO sk_document_values(document_id,field_key,value_type,text_value,int_value,decimal_value,bool_value,datetime_value,guid_value) VALUES($doc,$key,$type,$text,$int,$decimal,$bool,$datetime,$guid)"; AddGuid(command, "$doc", documentId); command.Parameters.AddWithValue("$key", key); command.Parameters.AddWithValue("$type", (int)value.Type); command.Parameters.AddWithValue("$text", (object?)value.Text ?? DBNull.Value); command.Parameters.AddWithValue("$int", (object?)value.Int64 ?? DBNull.Value); command.Parameters.AddWithValue("$decimal", value.Decimal is { } d ? (double)d : DBNull.Value); command.Parameters.AddWithValue("$bool", value.Boolean is { } b ? b ? 1 : 0 : DBNull.Value); command.Parameters.AddWithValue("$datetime", value.DateTimeOffset is { } dto ? dto.ToUniversalTime().ToString("O") : DBNull.Value); command.Parameters.AddWithValue("$guid", value.Guid is { } guid ? guid.ToString("D") : DBNull.Value); await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<KnowledgeDocumentRecord?> ReadDocumentAsync(SqliteConnection connection, Guid id, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT external_id,knowledge_base_id,collection_id,schema_id,title,description,source_hash FROM sk_documents WHERE id=$id"; AddGuid(command, "$id", id); await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var external = reader.IsDBNull(0) ? null : reader.GetString(0); var kb = Guid.Parse(reader.GetString(1)); var collection = Guid.Parse(reader.GetString(2)); var schema = Guid.Parse(reader.GetString(3)); var title = reader.GetString(4); var description = reader.GetString(5); var hash = reader.IsDBNull(6) ? null : reader.GetString(6); await reader.DisposeAsync().ConfigureAwait(false);
        var tags = new List<string>(); await using (var tagsCommand = connection.CreateCommand()) { tagsCommand.CommandText = "SELECT tag FROM sk_document_tags WHERE document_id=$id ORDER BY tag"; AddGuid(tagsCommand, "$id", id); await using var r = await tagsCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); while (await r.ReadAsync(cancellationToken).ConfigureAwait(false)) tags.Add(r.GetString(0)); }
        var values = new Dictionary<string, KnowledgeValue>(StringComparer.OrdinalIgnoreCase); await using (var valuesCommand = connection.CreateCommand()) { valuesCommand.CommandText = "SELECT field_key,value_type,text_value,int_value,decimal_value,bool_value,datetime_value,guid_value FROM sk_document_values WHERE document_id=$id"; AddGuid(valuesCommand, "$id", id); await using var r = await valuesCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); while (await r.ReadAsync(cancellationToken).ConfigureAwait(false)) values[r.GetString(0)] = ReadValue(r); }
        return new KnowledgeDocumentRecord { Id = id, ExternalId = external, KnowledgeBaseId = kb, CollectionId = collection, SchemaId = schema, Title = title, Description = description, Tags = tags, Values = values, SourceHash = hash };
    }

    private static KnowledgeValue ReadValue(SqliteDataReader reader) => (KnowledgeFieldType)reader.GetInt32(1) switch
    {
        KnowledgeFieldType.Text => KnowledgeValue.From(reader.IsDBNull(2) ? null : reader.GetString(2)),
        KnowledgeFieldType.Int64 => KnowledgeValue.From(reader.GetInt64(3)),
        KnowledgeFieldType.Decimal => KnowledgeValue.From((decimal)reader.GetDouble(4)),
        KnowledgeFieldType.Boolean => KnowledgeValue.From(reader.GetInt32(5) != 0),
        KnowledgeFieldType.DateTimeOffset => KnowledgeValue.From(DateTimeOffset.Parse(reader.GetString(6), System.Globalization.CultureInfo.InvariantCulture)),
        KnowledgeFieldType.Guid => KnowledgeValue.From(Guid.Parse(reader.GetString(7))),
        var type => throw new InvalidOperationException($"Unsupported stored value type {type}.")
    };

    private static SqliteVecCandidateQuery CandidateQuery(string table, string where, SqliteVecStorageKind storage) => new() { Table = table, ItemKeyColumn = "item_id", FieldNameColumn = "field_name", FingerprintColumn = "fingerprint", VectorColumn = "embedding", RecordJsonColumn = "record_json", FieldWeightColumn = "field_weight", AdditionalWhereSql = where, StorageKind = storage };

    private static string BuildScopeSql(IReadOnlyList<Guid> collections, bool descendants, out IReadOnlyList<SqliteParameter> parameters)
    {
        var ps = new List<SqliteParameter>(); parameters = ps; if (collections.Count == 0) return string.Empty;
        var names = new List<string>(); for (var i = 0; i < collections.Count; i++) { var name = $"$sk_c{i}"; names.Add(name); ps.Add(new SqliteParameter(name, collections[i].ToString("D"))); }
        if (!descendants) return $" AND d.collection_id IN ({string.Join(',', names)})";
        var seeds = string.Join(" UNION ALL ", names.Select(name => $"SELECT {name}"));
        return $" AND d.collection_id IN (WITH RECURSIVE sk_scope(id) AS ({seeds} UNION ALL SELECT c.id FROM sk_collections c JOIN sk_scope s ON c.parent_collection_id=s.id) SELECT id FROM sk_scope)";
    }

    private static SqliteParameter Clone(SqliteParameter parameter) => new(parameter.ParameterName, parameter.Value);

    private static async Task<KnowledgeBaseRecord?> FindKnowledgeBaseAsync(SqliteConnection connection, string title, string? externalId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.CommandText = externalId is null ? "SELECT id,external_id,title,description FROM sk_knowledge_bases WHERE title=$title LIMIT 1" : "SELECT id,external_id,title,description FROM sk_knowledge_bases WHERE external_id=$external LIMIT 1"; command.Parameters.AddWithValue("$title", title.Trim()); command.Parameters.AddWithValue("$external", (object?)externalId ?? DBNull.Value); await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? new KnowledgeBaseRecord { Id = Guid.Parse(reader.GetString(0)), ExternalId = reader.IsDBNull(1) ? null : reader.GetString(1), Title = reader.GetString(2), Description = reader.GetString(3) } : null;
    }

    private static async Task<KnowledgeCollectionRecord?> FindCollectionAsync(SqliteConnection connection, Guid kb, string title, Guid? parent, string? externalId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.CommandText = externalId is null ? "SELECT id,default_schema_id,external_id,title,description FROM sk_collections WHERE knowledge_base_id=$kb AND title=$title AND (($parent IS NULL AND parent_collection_id IS NULL) OR parent_collection_id=$parent) LIMIT 1" : "SELECT id,default_schema_id,external_id,title,description FROM sk_collections WHERE knowledge_base_id=$kb AND external_id=$external LIMIT 1"; AddGuid(command, "$kb", kb); command.Parameters.AddWithValue("$title", title.Trim()); AddNullableGuid(command, "$parent", parent); command.Parameters.AddWithValue("$external", (object?)externalId ?? DBNull.Value); await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? new KnowledgeCollectionRecord { Id = Guid.Parse(reader.GetString(0)), KnowledgeBaseId = kb, ParentCollectionId = parent, DefaultSchemaId = ReadNullableGuid(reader, 1), ExternalId = reader.IsDBNull(2) ? null : reader.GetString(2), Title = reader.GetString(3), Description = reader.GetString(4) } : null;
    }

    private static async Task<IReadOnlyList<string>> ReadCollectionTagsAsync(SqliteConnection connection, Guid collectionId, CancellationToken cancellationToken)
    {
        var result = new List<string>(); await using var command = connection.CreateCommand(); command.CommandText = "SELECT tag FROM sk_collection_tags WHERE collection_id=$id ORDER BY tag"; AddGuid(command, "$id", collectionId); await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(reader.GetString(0)); return result;
    }

    private static async Task<StoreMetadata?> ReadMetadataAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT database_version,active_vector_table,active_fingerprint,active_dimensions,active_storage_kind,pending_vector_table FROM sk_store_metadata WHERE singleton=1"; await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null; return new StoreMetadata(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), (SqliteVecStorageKind)reader.GetInt32(4), reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    private static async Task InsertMetadataAsync(SqliteConnection connection, KnowledgeStorageInitialization init, Guid generation, string table, SqliteVecStorageKind storage, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.CommandText = "INSERT INTO sk_store_metadata(singleton,store_id,engine_version,database_version,persistence_mode,active_generation,active_vector_table,active_fingerprint,active_dimensions,active_storage_kind) VALUES(1,$store,$engine,$db,$mode,$generation,$table,$fingerprint,$dimensions,$storage)"; command.Parameters.AddWithValue("$store", Guid.NewGuid().ToString("D")); command.Parameters.AddWithValue("$engine", EngineVersion); command.Parameters.AddWithValue("$db", init.DatabaseVersion); command.Parameters.AddWithValue("$mode", (int)init.PersistenceMode); command.Parameters.AddWithValue("$generation", generation.ToString("D")); command.Parameters.AddWithValue("$table", table); command.Parameters.AddWithValue("$fingerprint", init.Embedding.EmbeddingSpaceFingerprint); command.Parameters.AddWithValue("$dimensions", init.Embedding.OutputDimensions); command.Parameters.AddWithValue("$storage", (int)storage); await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task SetPendingMetadataAsync(SqliteConnection connection, KnowledgeStorageInitialization init, Guid generation, string table, SqliteVecStorageKind storage, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.CommandText = "UPDATE sk_store_metadata SET database_version=$db,persistence_mode=$mode,pending_generation=$generation,pending_vector_table=$table,pending_fingerprint=$fingerprint,pending_dimensions=$dimensions,pending_storage_kind=$storage WHERE singleton=1"; command.Parameters.AddWithValue("$db", init.DatabaseVersion); command.Parameters.AddWithValue("$mode", (int)init.PersistenceMode); command.Parameters.AddWithValue("$generation", generation.ToString("D")); command.Parameters.AddWithValue("$table", table); command.Parameters.AddWithValue("$fingerprint", init.Embedding.EmbeddingSpaceFingerprint); command.Parameters.AddWithValue("$dimensions", init.Embedding.OutputDimensions); command.Parameters.AddWithValue("$storage", (int)storage); await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ClearPendingMetadataAsync(SqliteConnection connection, CancellationToken cancellationToken) { await using var command = connection.CreateCommand(); command.CommandText = "UPDATE sk_store_metadata SET pending_generation=NULL,pending_vector_table=NULL,pending_fingerprint=NULL,pending_dimensions=NULL,pending_storage_kind=NULL WHERE singleton=1"; await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
    private static async Task DropVectorTableIfExistsAsync(SqliteConnection connection, string table, CancellationToken cancellationToken) { await using var command = connection.CreateCommand(); command.CommandText = $"DROP TABLE IF EXISTS {Quote(table)}"; await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
    private static string VectorTableName(Guid generation) => "sk_vectors_" + generation.ToString("N");
    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    private static Guid? ReadNullableGuid(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : Guid.Parse(reader.GetString(ordinal));
    private static void AddGuid(SqliteCommand command, string name, Guid value) => command.Parameters.AddWithValue(name, value.ToString("D"));
    private static void AddNullableGuid(SqliteCommand command, string name, Guid? value) => command.Parameters.AddWithValue(name, value is { } guid ? guid.ToString("D") : DBNull.Value);
    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters) { await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; foreach (var p in parameters) command.Parameters.AddWithValue(p.Name, p.Value); await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
    private sealed record StoreMetadata(int DatabaseVersion, string ActiveVectorTable, string ActiveFingerprint, int ActiveDimensions, SqliteVecStorageKind ActiveStorageKind, string? PendingVectorTable);
}
