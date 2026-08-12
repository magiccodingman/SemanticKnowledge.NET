using Npgsql;
using OnnxTextEmbeddings;
using OnnxTextEmbeddings.PgVector;

namespace SemanticKnowledge.PostgreSql;

internal sealed class PostgreSqlKnowledgeStorageProvider(
    SemanticKnowledgePostgreSqlOptions options,
    NpgsqlDataSource dataSource,
    PgVectorSemanticSearch semanticSearch) : IKnowledgeStorageProvider
{
    private const int EngineVersion = 1;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _activeVectorTable;
    private string? _writeVectorTable;
    private string? _pendingVectorTable;
    private PgVectorStorageKind _activeStorageKind;
    private PgVectorStorageKind _writeStorageKind;

    public async Task<KnowledgeProviderCapabilities> InitializeAsync(KnowledgeStorageInitialization initialization, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await VerifyVectorExtensionAsync(connection, cancellationToken).ConfigureAwait(false);
            await CreateBaseSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            var requestedStorage = initialization.StoragePreference == VectorStoragePreference.MaximumPrecision ? PgVectorStorageKind.Vector : PgVectorStorageKind.HalfVector;
            var metadata = await ReadMetadataAsync(connection, cancellationToken).ConfigureAwait(false);
            var rebuild = false;
            if (metadata is null)
            {
                var generation = Guid.NewGuid(); var table = VectorTableName(generation);
                await CreateVectorTableAsync(connection, table, initialization.Embedding.OutputDimensions, requestedStorage, cancellationToken).ConfigureAwait(false);
                await InsertMetadataAsync(connection, initialization, generation, table, requestedStorage, cancellationToken).ConfigureAwait(false);
                _activeVectorTable = _writeVectorTable = table; _activeStorageKind = _writeStorageKind = requestedStorage;
            }
            else
            {
                if (metadata.DatabaseVersion != initialization.DatabaseVersion)
                    throw new InvalidOperationException($"Stored SemanticKnowledge database version is {metadata.DatabaseVersion}, configured version is {initialization.DatabaseVersion}. Register/apply a logical migration before opening this store.");
                _activeVectorTable = metadata.ActiveVectorTable; _activeStorageKind = metadata.ActiveStorageKind;
                var matches = metadata.ActiveDimensions == initialization.Embedding.OutputDimensions
                    && metadata.ActiveStorageKind == requestedStorage
                    && string.Equals(metadata.ActiveFingerprint, initialization.Embedding.EmbeddingSpaceFingerprint, StringComparison.Ordinal);
                if (matches)
                {
                    _writeVectorTable = _activeVectorTable; _writeStorageKind = _activeStorageKind;
                    if (!string.IsNullOrWhiteSpace(metadata.PendingVectorTable)) await DropVectorTableIfExistsAsync(connection, metadata.PendingVectorTable!, cancellationToken).ConfigureAwait(false);
                    await ClearPendingMetadataAsync(connection, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(metadata.PendingVectorTable)) await DropVectorTableIfExistsAsync(connection, metadata.PendingVectorTable!, cancellationToken).ConfigureAwait(false);
                    var generation = Guid.NewGuid(); var table = VectorTableName(generation);
                    await CreateVectorTableAsync(connection, table, initialization.Embedding.OutputDimensions, requestedStorage, cancellationToken).ConfigureAwait(false);
                    await SetPendingMetadataAsync(connection, initialization, generation, table, requestedStorage, cancellationToken).ConfigureAwait(false);
                    _pendingVectorTable = table; _writeVectorTable = table; _writeStorageKind = requestedStorage; rebuild = true;
                }
            }
            return new KnowledgeProviderCapabilities { Provider = "PostgreSQL/pgvector", MaxDimensions = int.MaxValue, PhysicalVectorStorage = requestedStorage == PgVectorStorageKind.HalfVector ? "halfvec" : "vector", ExactVectorSearch = true, ApproximateVectorSearch = true, NativeAotSupported = true, RequiresEmbeddingRebuild = rebuild };
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
            await using (var command = new NpgsqlCommand($"UPDATE {Q("sk_store_metadata")} SET active_generation=pending_generation,active_vector_table=pending_vector_table,active_fingerprint=pending_fingerprint,active_dimensions=pending_dimensions,active_storage_kind=pending_storage_kind,pending_generation=NULL,pending_vector_table=NULL,pending_fingerprint=NULL,pending_dimensions=NULL,pending_storage_kind=NULL WHERE singleton=1", connection, transaction))
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _activeVectorTable = _pendingVectorTable; _activeStorageKind = _writeStorageKind; _pendingVectorTable = null; _writeVectorTable = _activeVectorTable;
            if (!string.IsNullOrWhiteSpace(old) && !string.Equals(old, _activeVectorTable, StringComparison.Ordinal)) await DropVectorTableIfExistsAsync(connection, old!, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<KnowledgeBaseRecord> GetOrCreateKnowledgeBaseAsync(string title, string? externalId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title); await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var found = await FindKnowledgeBaseAsync(connection, title, externalId, cancellationToken).ConfigureAwait(false); if (found is not null) return found;
        var record = new KnowledgeBaseRecord { Id = Guid.NewGuid(), ExternalId = externalId, Title = title.Trim() };
        await using var command = new NpgsqlCommand($"INSERT INTO {Q("sk_knowledge_bases")}(id,external_id,title,description) VALUES(@id,@external,@title,'')", connection);
        command.Parameters.AddWithValue("id", record.Id); command.Parameters.AddWithValue("external", (object?)externalId ?? DBNull.Value); command.Parameters.AddWithValue("title", record.Title); await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); return record;
    }

    public async Task<KnowledgeCollectionRecord> GetOrCreateCollectionAsync(Guid knowledgeBaseId, string title, Guid? parentCollectionId = null, Guid? defaultSchemaId = null, string? externalId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title); await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var found = await FindCollectionAsync(connection, knowledgeBaseId, title, parentCollectionId, externalId, cancellationToken).ConfigureAwait(false); if (found is not null) return found;
        var record = new KnowledgeCollectionRecord { Id = Guid.NewGuid(), KnowledgeBaseId = knowledgeBaseId, ParentCollectionId = parentCollectionId, DefaultSchemaId = defaultSchemaId, ExternalId = externalId, Title = title.Trim() };
        await using var command = new NpgsqlCommand($"INSERT INTO {Q("sk_collections")}(id,knowledge_base_id,parent_collection_id,default_schema_id,external_id,title,description) VALUES(@id,@kb,@parent,@schema,@external,@title,'')", connection);
        command.Parameters.AddWithValue("id", record.Id); command.Parameters.AddWithValue("kb", knowledgeBaseId); command.Parameters.AddWithValue("parent", (object?)parentCollectionId ?? DBNull.Value); command.Parameters.AddWithValue("schema", (object?)defaultSchemaId ?? DBNull.Value); command.Parameters.AddWithValue("external", (object?)externalId ?? DBNull.Value); command.Parameters.AddWithValue("title", record.Title); await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); return record;
    }

    public async Task<IReadOnlyList<KnowledgeCollectionRecord>> GetCollectionsAsync(Guid? knowledgeBaseId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false); var rows = new List<KnowledgeCollectionRecord>();
        await using (var command = new NpgsqlCommand($"SELECT id,knowledge_base_id,parent_collection_id,default_schema_id,external_id,title,description FROM {Q("sk_collections")}" + (knowledgeBaseId is null ? "" : " WHERE knowledge_base_id=@kb") + " ORDER BY title", connection))
        {
            if (knowledgeBaseId is { } kb) command.Parameters.AddWithValue("kb", kb);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) rows.Add(new KnowledgeCollectionRecord { Id = reader.GetGuid(0), KnowledgeBaseId = reader.GetGuid(1), ParentCollectionId = reader.IsDBNull(2) ? null : reader.GetGuid(2), DefaultSchemaId = reader.IsDBNull(3) ? null : reader.GetGuid(3), ExternalId = reader.IsDBNull(4) ? null : reader.GetString(4), Title = reader.GetString(5), Description = reader.GetString(6) });
        }
        var result = new List<KnowledgeCollectionRecord>(rows.Count); foreach (var row in rows) result.Add(row with { Tags = await ReadCollectionTagsAsync(connection, row.Id, cancellationToken).ConfigureAwait(false) }); return result;
    }

    public async Task UpsertCollectionSemanticSourcesAsync(KnowledgeCollectionRecord collection, IReadOnlyList<SemanticSourceRecord> semanticSources, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false); await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await DeleteSemanticRowsAsync(connection, transaction, collection.Id, SemanticEntityKind.Collection, cancellationToken).ConfigureAwait(false); foreach (var source in semanticSources) await InsertSemanticSourceAsync(connection, transaction, source, cancellationToken).ConfigureAwait(false); await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpsertSchemaAsync(KnowledgeSchemaDefinition schema, CancellationToken cancellationToken = default)
    {
        schema.Validate(); await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false); await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = new NpgsqlCommand($"INSERT INTO {Q("sk_schemas")}(id,schema_key,display_name,revision) VALUES(@id,@key,@name,@revision) ON CONFLICT(id) DO UPDATE SET schema_key=EXCLUDED.schema_key,display_name=EXCLUDED.display_name,revision=EXCLUDED.revision", connection, transaction)) { command.Parameters.AddWithValue("id", schema.Id); command.Parameters.AddWithValue("key", schema.Key); command.Parameters.AddWithValue("name", schema.DisplayName); command.Parameters.AddWithValue("revision", schema.Revision); await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
        await ExecuteAsync(connection, transaction, $"DELETE FROM {Q("sk_schema_fields")} WHERE schema_id=@id", cancellationToken, ("id", schema.Id)).ConfigureAwait(false);
        for (var ordinal = 0; ordinal < schema.Fields.Count; ordinal++)
        {
            var field = schema.Fields[ordinal]; await using var command = new NpgsqlCommand($"INSERT INTO {Q("sk_schema_fields")}(id,schema_id,field_key,display_name,field_type,required,system,filterable,semantic_mode,semantic_weight,ordinal) VALUES(@id,@schema,@key,@name,@type,@required,@system,@filterable,@mode,@weight,@ordinal)", connection, transaction);
            command.Parameters.AddWithValue("id", field.Id); command.Parameters.AddWithValue("schema", schema.Id); command.Parameters.AddWithValue("key", field.Key); command.Parameters.AddWithValue("name", field.DisplayName); command.Parameters.AddWithValue("type", (int)field.Type); command.Parameters.AddWithValue("required", field.Required); command.Parameters.AddWithValue("system", field.System); command.Parameters.AddWithValue("filterable", field.Filterable); command.Parameters.AddWithValue("mode", (int)field.SemanticMode); command.Parameters.AddWithValue("weight", field.SemanticWeightPercent); command.Parameters.AddWithValue("ordinal", ordinal); await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<KnowledgeSchemaDefinition?> GetSchemaAsync(Guid schemaId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false); string key, display; int revision;
        await using (var command = new NpgsqlCommand($"SELECT schema_key,display_name,revision FROM {Q("sk_schemas")} WHERE id=@id", connection)) { command.Parameters.AddWithValue("id", schemaId); await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null; key = reader.GetString(0); display = reader.GetString(1); revision = reader.GetInt32(2); }
        var fields = new List<KnowledgeSchemaField>(); await using (var command = new NpgsqlCommand($"SELECT id,field_key,display_name,field_type,required,system,filterable,semantic_mode,semantic_weight FROM {Q("sk_schema_fields")} WHERE schema_id=@id ORDER BY ordinal", connection)) { command.Parameters.AddWithValue("id", schemaId); await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) fields.Add(new KnowledgeSchemaField { Id = reader.GetGuid(0), Key = reader.GetString(1), DisplayName = reader.GetString(2), Type = (KnowledgeFieldType)reader.GetInt32(3), Required = reader.GetBoolean(4), System = reader.GetBoolean(5), Filterable = reader.GetBoolean(6), SemanticMode = (SemanticMode)reader.GetInt32(7), SemanticWeightPercent = reader.GetInt32(8) }); }
        return new KnowledgeSchemaDefinition { Id = schemaId, Key = key, DisplayName = display, Revision = revision, Fields = fields };
    }

    public async Task UpsertDocumentAsync(KnowledgeDocumentRecord document, IReadOnlyList<SemanticSourceRecord> semanticSources, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false); await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = new NpgsqlCommand($"INSERT INTO {Q("sk_documents")}(id,external_id,knowledge_base_id,collection_id,schema_id,title,description,source_hash) VALUES(@id,@external,@kb,@collection,@schema,@title,@description,@hash) ON CONFLICT(id) DO UPDATE SET external_id=EXCLUDED.external_id,knowledge_base_id=EXCLUDED.knowledge_base_id,collection_id=EXCLUDED.collection_id,schema_id=EXCLUDED.schema_id,title=EXCLUDED.title,description=EXCLUDED.description,source_hash=EXCLUDED.source_hash", connection, transaction)) { command.Parameters.AddWithValue("id", document.Id); command.Parameters.AddWithValue("external", (object?)document.ExternalId ?? DBNull.Value); command.Parameters.AddWithValue("kb", document.KnowledgeBaseId); command.Parameters.AddWithValue("collection", document.CollectionId); command.Parameters.AddWithValue("schema", document.SchemaId); command.Parameters.AddWithValue("title", document.Title); command.Parameters.AddWithValue("description", document.Description); command.Parameters.AddWithValue("hash", (object?)document.SourceHash ?? DBNull.Value); await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
        await ExecuteAsync(connection, transaction, $"DELETE FROM {Q("sk_document_tags")} WHERE document_id=@id", cancellationToken, ("id", document.Id)).ConfigureAwait(false); await ExecuteAsync(connection, transaction, $"DELETE FROM {Q("sk_document_values")} WHERE document_id=@id", cancellationToken, ("id", document.Id)).ConfigureAwait(false);
        foreach (var tag in document.Tags) await ExecuteAsync(connection, transaction, $"INSERT INTO {Q("sk_document_tags")}(document_id,tag) VALUES(@id,@tag)", cancellationToken, ("id", document.Id), ("tag", tag)).ConfigureAwait(false);
        foreach (var pair in document.Values) await InsertValueAsync(connection, transaction, document.Id, pair.Key, pair.Value, cancellationToken).ConfigureAwait(false);
        await DeleteSemanticRowsAsync(connection, transaction, document.Id, SemanticEntityKind.Document, cancellationToken).ConfigureAwait(false); foreach (var source in semanticSources) await InsertSemanticSourceAsync(connection, transaction, source, cancellationToken).ConfigureAwait(false); await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<KnowledgeDocumentRecord?> GetDocumentAsync(Guid documentId, CancellationToken cancellationToken = default) { await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false); return await ReadDocumentAsync(connection, documentId, cancellationToken).ConfigureAwait(false); }

    public async Task<IReadOnlyList<KnowledgeDocumentRecord>> GetDocumentsAsync(Guid? knowledgeBaseId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false); var ids = new List<Guid>(); await using (var command = new NpgsqlCommand($"SELECT id FROM {Q("sk_documents")}" + (knowledgeBaseId is null ? "" : " WHERE knowledge_base_id=@kb"), connection)) { if (knowledgeBaseId is { } kb) command.Parameters.AddWithValue("kb", kb); await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) ids.Add(reader.GetGuid(0)); }
        var result = new List<KnowledgeDocumentRecord>(ids.Count); foreach (var id in ids) if (await ReadDocumentAsync(connection, id, cancellationToken).ConfigureAwait(false) is { } document) result.Add(document); return result;
    }

    public async Task<IReadOnlyList<KnowledgeSearchHit>> SearchAsync(QueryEmbedding query, KnowledgeSearchRequest request, CancellationToken cancellationToken = default)
    {
        var table = _activeVectorTable ?? throw new InvalidOperationException("PostgreSQL provider is not initialized."); await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false); var collectionIds = request.CollectionIds.ToArray();
        if (request.Mode == KnowledgeSearchMode.Smart)
        {
            var route = await semanticSearch.SearchAsync<Guid>(connection, query, CandidateQuery(table, "item_kind='collection' AND knowledge_base_id=@sk_kb", _activeStorageKind), new DatabaseSemanticSearchOptions { Top = 8, CandidateCount = 100 }, command => command.Parameters.AddWithValue("sk_kb", request.KnowledgeBaseId), cancellationToken: cancellationToken).ConfigureAwait(false);
            collectionIds = route.Results.Select(x => x.Item).Distinct().ToArray();
        }
        var compiled = PostgreSqlFilterCompiler.Compile(request.Filter); var scope = BuildScopeSql(collectionIds, request.IncludeDescendants, out var scopeParameters);
        var where = $"item_kind='document' AND knowledge_base_id=@sk_kb AND item_id IN (SELECT d.id FROM sk_documents d WHERE d.knowledge_base_id=@sk_kb AND ({compiled.Sql}){scope})";
        var search = await semanticSearch.SearchAsync<Guid>(connection, query, CandidateQuery(table, where, _activeStorageKind), new DatabaseSemanticSearchOptions { Top = request.Top, CandidateCount = request.CandidateCount }, command => { command.Parameters.AddWithValue("sk_kb", request.KnowledgeBaseId); foreach (var p in compiled.Parameters) command.Parameters.Add(Clone(p)); foreach (var p in scopeParameters) command.Parameters.Add(Clone(p)); }, cancellationToken: cancellationToken).ConfigureAwait(false);
        var hits = new List<KnowledgeSearchHit>(); foreach (var result in search.Results) { var document = await ReadDocumentAsync(connection, result.Item, cancellationToken).ConfigureAwait(false); if (document is null) continue; var matches = result.Fields.SelectMany(field => field.Matches.Select(match => new KnowledgeMatchedChunk { FieldKey = field.Name, RawSimilarity = match.RawSimilarity, AdjustedSimilarity = match.AdjustedSimilarity, TokenCount = match.Embedding.Source.TokenCount, CharacterRange = match.Embedding.Source.CharacterRange, Text = request.Include == KnowledgeResultInclude.MetadataOnly ? null : match.Embedding.Text })).ToArray(); hits.Add(new KnowledgeSearchHit { DocumentId = document.Id, CollectionId = document.CollectionId, SchemaId = document.SchemaId, Score = result.Score, Title = document.Title, Description = document.Description, Tags = document.Tags, Matches = matches, Scoring = result.Scoring }); }
        return hits;
    }

    public async Task DeleteDocumentAsync(Guid documentId, CancellationToken cancellationToken = default) { await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false); await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false); await DeleteSemanticRowsAsync(connection, transaction, documentId, SemanticEntityKind.Document, cancellationToken).ConfigureAwait(false); await ExecuteAsync(connection, transaction, $"DELETE FROM {Q("sk_documents")} WHERE id=@id", cancellationToken, ("id", documentId)).ConfigureAwait(false); await transaction.CommitAsync(cancellationToken).ConfigureAwait(false); }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false); var metadata = await ReadMetadataAsync(connection, cancellationToken).ConfigureAwait(false); if (metadata is not null) { await DropVectorTableIfExistsAsync(connection, metadata.ActiveVectorTable, cancellationToken).ConfigureAwait(false); if (!string.IsNullOrWhiteSpace(metadata.PendingVectorTable)) await DropVectorTableIfExistsAsync(connection, metadata.PendingVectorTable!, cancellationToken).ConfigureAwait(false); }
        await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {QI(options.Schema)} CASCADE", connection); await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); _activeVectorTable = _writeVectorTable = _pendingVectorTable = null;
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken) { var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false); await using var command = new NpgsqlCommand($"SET search_path TO {QI(options.Schema)}, public", connection); await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); return connection; }
    private static async Task VerifyVectorExtensionAsync(NpgsqlConnection connection, CancellationToken cancellationToken) { await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname='vector')", connection); if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not bool true) throw new InvalidOperationException("PostgreSQL extension 'vector' is required. Install pgvector before starting SemanticKnowledge.NET; the library does not run privileged CREATE EXTENSION automatically."); }

    private async Task CreateBaseSchemaAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            CREATE SCHEMA IF NOT EXISTS {QI(options.Schema)};
            CREATE TABLE IF NOT EXISTS {Q("sk_store_metadata")}(singleton smallint PRIMARY KEY CHECK(singleton=1),store_id uuid NOT NULL,engine_version integer NOT NULL,database_version integer NOT NULL,persistence_mode integer NOT NULL,active_generation uuid NOT NULL,active_vector_table text NOT NULL,active_fingerprint text NOT NULL,active_dimensions integer NOT NULL,active_storage_kind integer NOT NULL,pending_generation uuid,pending_vector_table text,pending_fingerprint text,pending_dimensions integer,pending_storage_kind integer);
            CREATE TABLE IF NOT EXISTS {Q("sk_knowledge_bases")}(id uuid PRIMARY KEY,external_id text UNIQUE,title text NOT NULL,description text NOT NULL DEFAULT '');
            CREATE TABLE IF NOT EXISTS {Q("sk_collections")}(id uuid PRIMARY KEY,knowledge_base_id uuid NOT NULL REFERENCES {Q("sk_knowledge_bases")}(id) ON DELETE CASCADE,parent_collection_id uuid REFERENCES {Q("sk_collections")}(id) ON DELETE CASCADE,default_schema_id uuid,external_id text,title text NOT NULL,description text NOT NULL DEFAULT '');
            CREATE UNIQUE INDEX IF NOT EXISTS ix_sk_pg_collections_external ON {Q("sk_collections")}(knowledge_base_id,external_id) WHERE external_id IS NOT NULL;
            CREATE INDEX IF NOT EXISTS ix_sk_pg_collections_parent ON {Q("sk_collections")}(parent_collection_id);
            CREATE TABLE IF NOT EXISTS {Q("sk_collection_tags")}(collection_id uuid NOT NULL REFERENCES {Q("sk_collections")}(id) ON DELETE CASCADE,tag text NOT NULL,PRIMARY KEY(collection_id,tag));
            CREATE TABLE IF NOT EXISTS {Q("sk_schemas")}(id uuid PRIMARY KEY,schema_key text NOT NULL UNIQUE,display_name text NOT NULL,revision integer NOT NULL);
            CREATE TABLE IF NOT EXISTS {Q("sk_schema_fields")}(id uuid PRIMARY KEY,schema_id uuid NOT NULL REFERENCES {Q("sk_schemas")}(id) ON DELETE CASCADE,field_key text NOT NULL,display_name text NOT NULL,field_type integer NOT NULL,required boolean NOT NULL,system boolean NOT NULL,filterable boolean NOT NULL,semantic_mode integer NOT NULL,semantic_weight integer NOT NULL,ordinal integer NOT NULL,UNIQUE(schema_id,field_key));
            CREATE TABLE IF NOT EXISTS {Q("sk_documents")}(id uuid PRIMARY KEY,external_id text,knowledge_base_id uuid NOT NULL REFERENCES {Q("sk_knowledge_bases")}(id) ON DELETE CASCADE,collection_id uuid NOT NULL REFERENCES {Q("sk_collections")}(id) ON DELETE CASCADE,schema_id uuid NOT NULL REFERENCES {Q("sk_schemas")}(id),title text NOT NULL,description text NOT NULL DEFAULT '',source_hash text);
            CREATE UNIQUE INDEX IF NOT EXISTS ix_sk_pg_documents_external ON {Q("sk_documents")}(knowledge_base_id,external_id) WHERE external_id IS NOT NULL;
            CREATE INDEX IF NOT EXISTS ix_sk_pg_documents_collection ON {Q("sk_documents")}(collection_id);
            CREATE TABLE IF NOT EXISTS {Q("sk_document_tags")}(document_id uuid NOT NULL REFERENCES {Q("sk_documents")}(id) ON DELETE CASCADE,tag text NOT NULL,PRIMARY KEY(document_id,tag)); CREATE INDEX IF NOT EXISTS ix_sk_pg_tags_tag ON {Q("sk_document_tags")}(tag);
            CREATE TABLE IF NOT EXISTS {Q("sk_document_values")}(document_id uuid NOT NULL REFERENCES {Q("sk_documents")}(id) ON DELETE CASCADE,field_key text NOT NULL,value_type integer NOT NULL,text_value text,int_value bigint,decimal_value numeric,bool_value boolean,datetime_value timestamptz,guid_value uuid,PRIMARY KEY(document_id,field_key));
            CREATE INDEX IF NOT EXISTS ix_sk_pg_values_text ON {Q("sk_document_values")}(field_key,text_value); CREATE INDEX IF NOT EXISTS ix_sk_pg_values_int ON {Q("sk_document_values")}(field_key,int_value); CREATE INDEX IF NOT EXISTS ix_sk_pg_values_decimal ON {Q("sk_document_values")}(field_key,decimal_value); CREATE INDEX IF NOT EXISTS ix_sk_pg_values_bool ON {Q("sk_document_values")}(field_key,bool_value); CREATE INDEX IF NOT EXISTS ix_sk_pg_values_datetime ON {Q("sk_document_values")}(field_key,datetime_value); CREATE INDEX IF NOT EXISTS ix_sk_pg_values_guid ON {Q("sk_document_values")}(field_key,guid_value);
            CREATE TABLE IF NOT EXISTS {Q("sk_semantic_sources")}(id uuid PRIMARY KEY,item_id uuid NOT NULL,entity_kind integer NOT NULL,knowledge_base_id uuid NOT NULL,collection_id uuid NOT NULL,document_id uuid,field_id uuid NOT NULL,field_key text NOT NULL,field_weight real NOT NULL,fingerprint text NOT NULL,token_count integer NOT NULL,char_start integer NOT NULL,char_length integer NOT NULL,record_json text NOT NULL); CREATE INDEX IF NOT EXISTS ix_sk_pg_semantic_item ON {Q("sk_semantic_sources")}(item_id,entity_kind);
            """, connection); await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task CreateVectorTableAsync(NpgsqlConnection connection, string table, int dimensions, PgVectorStorageKind storage, CancellationToken cancellationToken) { var type = storage == PgVectorStorageKind.HalfVector ? $"halfvec({dimensions})" : $"vector({dimensions})"; await using var command = new NpgsqlCommand($"CREATE TABLE {Q(table)}(item_id uuid NOT NULL,item_kind text NOT NULL,field_name text NOT NULL,fingerprint text NOT NULL,knowledge_base_id uuid NOT NULL,collection_id uuid NOT NULL,embedding {type} NOT NULL,record_json text NOT NULL,field_weight real NOT NULL)", connection); await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }

    private async Task InsertSemanticSourceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, SemanticSourceRecord source, CancellationToken cancellationToken)
    {
        var table = _writeVectorTable ?? throw new InvalidOperationException("No writable vector generation is configured."); var json = EmbeddingSerializer.SerializeJson(source.Embedding);
        await using (var command = new NpgsqlCommand($"INSERT INTO {Q("sk_semantic_sources")}(id,item_id,entity_kind,knowledge_base_id,collection_id,document_id,field_id,field_key,field_weight,fingerprint,token_count,char_start,char_length,record_json) VALUES(@id,@item,@kind,@kb,@collection,@document,@field,@key,@weight,@fingerprint,@tokens,@start,@length,@json)", connection, transaction)) { command.Parameters.AddWithValue("id", source.Id); command.Parameters.AddWithValue("item", source.ItemId); command.Parameters.AddWithValue("kind", (int)source.EntityKind); command.Parameters.AddWithValue("kb", source.KnowledgeBaseId); command.Parameters.AddWithValue("collection", source.CollectionId); command.Parameters.AddWithValue("document", (object?)source.DocumentId ?? DBNull.Value); command.Parameters.AddWithValue("field", source.FieldId); command.Parameters.AddWithValue("key", source.FieldKey); command.Parameters.AddWithValue("weight", source.ScorerWeight); command.Parameters.AddWithValue("fingerprint", source.Embedding.Identity.EmbeddingSpaceFingerprint); command.Parameters.AddWithValue("tokens", source.Embedding.Source.TokenCount); command.Parameters.AddWithValue("start", source.Embedding.Source.CharacterRange.Start); command.Parameters.AddWithValue("length", source.Embedding.Source.CharacterRange.Length); command.Parameters.AddWithValue("json", json); await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
        await using (var command = new NpgsqlCommand($"INSERT INTO {Q(table)}(item_id,item_kind,field_name,fingerprint,knowledge_base_id,collection_id,embedding,record_json,field_weight) VALUES(@item,@kind,@field,@fingerprint,@kb,@collection,@embedding,@json,@weight)", connection, transaction)) { command.Parameters.AddWithValue("item", source.ItemId); command.Parameters.AddWithValue("kind", source.EntityKind == SemanticEntityKind.Document ? "document" : "collection"); command.Parameters.AddWithValue("field", source.FieldKey); command.Parameters.AddWithValue("fingerprint", source.Embedding.Identity.EmbeddingSpaceFingerprint); command.Parameters.AddWithValue("kb", source.KnowledgeBaseId); command.Parameters.AddWithValue("collection", source.CollectionId); command.Parameters.AddWithValue("embedding", _writeStorageKind == PgVectorStorageKind.HalfVector ? source.Embedding.Vector.ToPgHalfVector() : source.Embedding.Vector.ToPgVector()); command.Parameters.AddWithValue("json", json); command.Parameters.AddWithValue("weight", source.ScorerWeight); await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
    }

    private async Task DeleteSemanticRowsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid itemId, SemanticEntityKind kind, CancellationToken cancellationToken) { await ExecuteAsync(connection, transaction, $"DELETE FROM {Q("sk_semantic_sources")} WHERE item_id=@id AND entity_kind=@kind", cancellationToken, ("id", itemId), ("kind", (int)kind)).ConfigureAwait(false); if (_writeVectorTable is { } table) await ExecuteAsync(connection, transaction, $"DELETE FROM {Q(table)} WHERE item_id=@id AND item_kind=@kind", cancellationToken, ("id", itemId), ("kind", kind == SemanticEntityKind.Document ? "document" : "collection")).ConfigureAwait(false); }
    private async Task InsertValueAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid documentId, string key, KnowledgeValue value, CancellationToken cancellationToken) { await using var command = new NpgsqlCommand($"INSERT INTO {Q("sk_document_values")}(document_id,field_key,value_type,text_value,int_value,decimal_value,bool_value,datetime_value,guid_value) VALUES(@doc,@key,@type,@text,@int,@decimal,@bool,@datetime,@guid)", connection, transaction); command.Parameters.AddWithValue("doc", documentId); command.Parameters.AddWithValue("key", key); command.Parameters.AddWithValue("type", (int)value.Type); command.Parameters.AddWithValue("text", (object?)value.Text ?? DBNull.Value); command.Parameters.AddWithValue("int", (object?)value.Int64 ?? DBNull.Value); command.Parameters.AddWithValue("decimal", (object?)value.Decimal ?? DBNull.Value); command.Parameters.AddWithValue("bool", (object?)value.Boolean ?? DBNull.Value); command.Parameters.AddWithValue("datetime", (object?)value.DateTimeOffset ?? DBNull.Value); command.Parameters.AddWithValue("guid", (object?)value.Guid ?? DBNull.Value); await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }

    private async Task<KnowledgeDocumentRecord?> ReadDocumentAsync(NpgsqlConnection connection, Guid id, CancellationToken cancellationToken)
    {
        string? external, hash; Guid kb, collection, schema; string title, description; await using (var command = new NpgsqlCommand($"SELECT external_id,knowledge_base_id,collection_id,schema_id,title,description,source_hash FROM {Q("sk_documents")} WHERE id=@id", connection)) { command.Parameters.AddWithValue("id", id); await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null; external = reader.IsDBNull(0) ? null : reader.GetString(0); kb = reader.GetGuid(1); collection = reader.GetGuid(2); schema = reader.GetGuid(3); title = reader.GetString(4); description = reader.GetString(5); hash = reader.IsDBNull(6) ? null : reader.GetString(6); }
        var tags = new List<string>(); await using (var command = new NpgsqlCommand($"SELECT tag FROM {Q("sk_document_tags")} WHERE document_id=@id ORDER BY tag", connection)) { command.Parameters.AddWithValue("id", id); await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) tags.Add(reader.GetString(0)); }
        var values = new Dictionary<string, KnowledgeValue>(StringComparer.OrdinalIgnoreCase); await using (var command = new NpgsqlCommand($"SELECT field_key,value_type,text_value,int_value,decimal_value,bool_value,datetime_value,guid_value FROM {Q("sk_document_values")} WHERE document_id=@id", connection)) { command.Parameters.AddWithValue("id", id); await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) values[reader.GetString(0)] = ReadValue(reader); }
        return new KnowledgeDocumentRecord { Id = id, ExternalId = external, KnowledgeBaseId = kb, CollectionId = collection, SchemaId = schema, Title = title, Description = description, Tags = tags, Values = values, SourceHash = hash };
    }
    private static KnowledgeValue ReadValue(NpgsqlDataReader reader) => (KnowledgeFieldType)reader.GetInt32(1) switch { KnowledgeFieldType.Text => KnowledgeValue.From(reader.IsDBNull(2) ? null : reader.GetString(2)), KnowledgeFieldType.Int64 => KnowledgeValue.From(reader.GetInt64(3)), KnowledgeFieldType.Decimal => KnowledgeValue.From(reader.GetDecimal(4)), KnowledgeFieldType.Boolean => KnowledgeValue.From(reader.GetBoolean(5)), KnowledgeFieldType.DateTimeOffset => KnowledgeValue.From(reader.GetFieldValue<DateTimeOffset>(6)), KnowledgeFieldType.Guid => KnowledgeValue.From(reader.GetGuid(7)), var type => throw new InvalidOperationException($"Unsupported stored value type {type}.") };

    private PgVectorCandidateQuery CandidateQuery(string table, string where, PgVectorStorageKind storage) => new() { Table = Qualified(table), ItemKeyColumn = "item_id", FieldNameColumn = "field_name", FingerprintColumn = "fingerprint", VectorColumn = "embedding", RecordJsonColumn = "record_json", FieldWeightColumn = "field_weight", AdditionalWhereSql = where, StorageKind = storage, SearchMode = PgVectorSearchMode.Exact };
    private static string BuildScopeSql(IReadOnlyList<Guid> collections, bool descendants, out IReadOnlyList<NpgsqlParameter> parameters) { var ps = new List<NpgsqlParameter>(); parameters = ps; if (collections.Count == 0) return string.Empty; var names = new List<string>(); for (var i = 0; i < collections.Count; i++) { var name = $"sk_c{i}"; names.Add("@" + name); ps.Add(new NpgsqlParameter(name, collections[i])); } if (!descendants) return $" AND d.collection_id IN ({string.Join(',', names)})"; var seeds = string.Join(" UNION ALL ", names.Select(name => $"SELECT {name}::uuid")); return $" AND d.collection_id IN (WITH RECURSIVE sk_scope(id) AS ({seeds} UNION ALL SELECT c.id FROM sk_collections c JOIN sk_scope s ON c.parent_collection_id=s.id) SELECT id FROM sk_scope)"; }
    private static NpgsqlParameter Clone(NpgsqlParameter p) => new(p.ParameterName, p.Value);

    private async Task<KnowledgeBaseRecord?> FindKnowledgeBaseAsync(NpgsqlConnection connection, string title, string? externalId, CancellationToken cancellationToken) { await using var command = new NpgsqlCommand(externalId is null ? $"SELECT id,external_id,title,description FROM {Q("sk_knowledge_bases")} WHERE title=@title LIMIT 1" : $"SELECT id,external_id,title,description FROM {Q("sk_knowledge_bases")} WHERE external_id=@external LIMIT 1", connection); command.Parameters.AddWithValue("title", title.Trim()); command.Parameters.AddWithValue("external", (object?)externalId ?? DBNull.Value); await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? new KnowledgeBaseRecord { Id = reader.GetGuid(0), ExternalId = reader.IsDBNull(1) ? null : reader.GetString(1), Title = reader.GetString(2), Description = reader.GetString(3) } : null; }
    private async Task<KnowledgeCollectionRecord?> FindCollectionAsync(NpgsqlConnection connection, Guid kb, string title, Guid? parent, string? externalId, CancellationToken cancellationToken) { await using var command = new NpgsqlCommand(externalId is null ? $"SELECT id,default_schema_id,external_id,title,description FROM {Q("sk_collections")} WHERE knowledge_base_id=@kb AND title=@title AND ((@parent IS NULL AND parent_collection_id IS NULL) OR parent_collection_id=@parent) LIMIT 1" : $"SELECT id,default_schema_id,external_id,title,description FROM {Q("sk_collections")} WHERE knowledge_base_id=@kb AND external_id=@external LIMIT 1", connection); command.Parameters.AddWithValue("kb", kb); command.Parameters.AddWithValue("title", title.Trim()); command.Parameters.AddWithValue("parent", (object?)parent ?? DBNull.Value); command.Parameters.AddWithValue("external", (object?)externalId ?? DBNull.Value); await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? new KnowledgeCollectionRecord { Id = reader.GetGuid(0), KnowledgeBaseId = kb, ParentCollectionId = parent, DefaultSchemaId = reader.IsDBNull(1) ? null : reader.GetGuid(1), ExternalId = reader.IsDBNull(2) ? null : reader.GetString(2), Title = reader.GetString(3), Description = reader.GetString(4) } : null; }
    private async Task<IReadOnlyList<string>> ReadCollectionTagsAsync(NpgsqlConnection connection, Guid id, CancellationToken cancellationToken) { var result = new List<string>(); await using var command = new NpgsqlCommand($"SELECT tag FROM {Q("sk_collection_tags")} WHERE collection_id=@id ORDER BY tag", connection); command.Parameters.AddWithValue("id", id); await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(reader.GetString(0)); return result; }

    private async Task<StoreMetadata?> ReadMetadataAsync(NpgsqlConnection connection, CancellationToken cancellationToken) { await using var command = new NpgsqlCommand($"SELECT database_version,active_vector_table,active_fingerprint,active_dimensions,active_storage_kind,pending_vector_table FROM {Q("sk_store_metadata")} WHERE singleton=1", connection); await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null; return new StoreMetadata(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), (PgVectorStorageKind)reader.GetInt32(4), reader.IsDBNull(5) ? null : reader.GetString(5)); }
    private async Task InsertMetadataAsync(NpgsqlConnection connection, KnowledgeStorageInitialization init, Guid generation, string table, PgVectorStorageKind storage, CancellationToken cancellationToken) { await using var command = new NpgsqlCommand($"INSERT INTO {Q("sk_store_metadata")}(singleton,store_id,engine_version,database_version,persistence_mode,active_generation,active_vector_table,active_fingerprint,active_dimensions,active_storage_kind) VALUES(1,@store,@engine,@db,@mode,@generation,@table,@fingerprint,@dimensions,@storage)", connection); command.Parameters.AddWithValue("store", Guid.NewGuid()); command.Parameters.AddWithValue("engine", EngineVersion); command.Parameters.AddWithValue("db", init.DatabaseVersion); command.Parameters.AddWithValue("mode", (int)init.PersistenceMode); command.Parameters.AddWithValue("generation", generation); command.Parameters.AddWithValue("table", table); command.Parameters.AddWithValue("fingerprint", init.Embedding.EmbeddingSpaceFingerprint); command.Parameters.AddWithValue("dimensions", init.Embedding.OutputDimensions); command.Parameters.AddWithValue("storage", (int)storage); await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
    private async Task SetPendingMetadataAsync(NpgsqlConnection connection, KnowledgeStorageInitialization init, Guid generation, string table, PgVectorStorageKind storage, CancellationToken cancellationToken) { await using var command = new NpgsqlCommand($"UPDATE {Q("sk_store_metadata")} SET database_version=@db,persistence_mode=@mode,pending_generation=@generation,pending_vector_table=@table,pending_fingerprint=@fingerprint,pending_dimensions=@dimensions,pending_storage_kind=@storage WHERE singleton=1", connection); command.Parameters.AddWithValue("db", init.DatabaseVersion); command.Parameters.AddWithValue("mode", (int)init.PersistenceMode); command.Parameters.AddWithValue("generation", generation); command.Parameters.AddWithValue("table", table); command.Parameters.AddWithValue("fingerprint", init.Embedding.EmbeddingSpaceFingerprint); command.Parameters.AddWithValue("dimensions", init.Embedding.OutputDimensions); command.Parameters.AddWithValue("storage", (int)storage); await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
    private async Task ClearPendingMetadataAsync(NpgsqlConnection connection, CancellationToken cancellationToken) { await using var command = new NpgsqlCommand($"UPDATE {Q("sk_store_metadata")} SET pending_generation=NULL,pending_vector_table=NULL,pending_fingerprint=NULL,pending_dimensions=NULL,pending_storage_kind=NULL WHERE singleton=1", connection); await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
    private async Task DropVectorTableIfExistsAsync(NpgsqlConnection connection, string table, CancellationToken cancellationToken) { await using var command = new NpgsqlCommand($"DROP TABLE IF EXISTS {Q(table)}", connection); await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
    private string Q(string table) => $"{QI(options.Schema)}.{QI(table)}"; private string Qualified(string table) => $"{options.Schema}.{table}"; private static string QI(string id) => $"\"{id.Replace("\"", "\"\"", StringComparison.Ordinal)}\""; private static string VectorTableName(Guid generation) => "sk_vectors_" + generation.ToString("N");
    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters) { await using var command = new NpgsqlCommand(sql, connection, transaction); foreach (var p in parameters) command.Parameters.AddWithValue(p.Name, p.Value); await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
    private sealed record StoreMetadata(int DatabaseVersion, string ActiveVectorTable, string ActiveFingerprint, int ActiveDimensions, PgVectorStorageKind ActiveStorageKind, string? PendingVectorTable);
}
