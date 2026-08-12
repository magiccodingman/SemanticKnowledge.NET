using Npgsql;
using OnnxTextEmbeddings;
using OnnxTextEmbeddings.PgVector;

namespace SemanticKnowledge.PostgreSql;

internal sealed class PostgreSqlAdvancedKnowledgeSearchProvider(
    SemanticKnowledgePostgreSqlOptions options,
    NpgsqlDataSource dataSource,
    PgVectorSemanticSearch semanticSearch,
    PgVectorLexicalSearch lexicalSearch) : IKnowledgeAdvancedSearchProvider
{
    private const string LexicalTable = "sk_lexical_sources";
    public string ProviderName => "PostgreSQL Full-Text Search";

    public async Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var exists = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM information_schema.tables WHERE table_schema=@schema AND table_name=@table)", connection);
        exists.Parameters.AddWithValue("schema", options.Schema); exists.Parameters.AddWithValue("table", LexicalTable);
        var missing = !(bool)(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false);
        if (missing)
        {
            await using var create = new NpgsqlCommand($"""
                CREATE TABLE {Q(LexicalTable)}(
                    id uuid PRIMARY KEY,
                    item_id uuid NOT NULL,
                    entity_kind integer NOT NULL,
                    knowledge_base_id uuid NOT NULL,
                    collection_id uuid NOT NULL,
                    document_id uuid,
                    field_id uuid NOT NULL,
                    field_key text NOT NULL,
                    text_value text NOT NULL,
                    search_vector tsvector GENERATED ALWAYS AS (setweight(to_tsvector('simple', coalesce(text_value,'')), 'A')) STORED
                );
                CREATE INDEX ix_sk_pg_lexical_search ON {Q(LexicalTable)} USING GIN(search_vector);
                CREATE INDEX ix_sk_pg_lexical_scope ON {Q(LexicalTable)}(knowledge_base_id,entity_kind,field_key);
                CREATE INDEX ix_sk_pg_lexical_item ON {Q(LexicalTable)}(item_id,entity_kind);
                """, connection);
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        return missing;
    }

    public async Task UpsertSourcesAsync(Guid itemId, SemanticEntityKind kind, IReadOnlyList<LexicalSourceRecord> sources, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false); await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var delete = new NpgsqlCommand($"DELETE FROM {Q(LexicalTable)} WHERE item_id=@item AND entity_kind=@kind", connection, transaction))
        { delete.Parameters.AddWithValue("item", itemId); delete.Parameters.AddWithValue("kind", (int)kind); await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
        foreach (var source in sources)
        {
            await using var insert = new NpgsqlCommand($"INSERT INTO {Q(LexicalTable)}(id,item_id,entity_kind,knowledge_base_id,collection_id,document_id,field_id,field_key,text_value) VALUES(@id,@item,@kind,@kb,@collection,@document,@fieldId,@fieldKey,@text)", connection, transaction);
            insert.Parameters.AddWithValue("id", source.Id); insert.Parameters.AddWithValue("item", source.ItemId); insert.Parameters.AddWithValue("kind", (int)source.EntityKind); insert.Parameters.AddWithValue("kb", source.KnowledgeBaseId); insert.Parameters.AddWithValue("collection", source.CollectionId); insert.Parameters.AddWithValue("document", (object?)source.DocumentId ?? DBNull.Value); insert.Parameters.AddWithValue("fieldId", source.FieldId); insert.Parameters.AddWithValue("fieldKey", source.FieldKey); insert.Parameters.AddWithValue("text", source.Text);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteSourcesAsync(Guid itemId, SemanticEntityKind kind, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false); await using var command = new NpgsqlCommand($"DELETE FROM {Q(LexicalTable)} WHERE item_id=@item AND entity_kind=@kind", connection);
        command.Parameters.AddWithValue("item", itemId); command.Parameters.AddWithValue("kind", (int)kind); await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false); await using var command = new NpgsqlCommand($"DROP TABLE IF EXISTS {Q(LexicalTable)}", connection); await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<KnowledgeAdvancedSearchCandidate>> SearchAsync(KnowledgeSearchQuery query, QueryEmbedding? semanticQuery, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var metadata = await ReadMetadataAsync(connection, cancellationToken).ConfigureAwait(false);
        var collectionIds = await ResolveCollectionsAsync(connection, query, semanticQuery, metadata, cancellationToken).ConfigureAwait(false);
        var rankings = new List<SearchStageRanking<Guid>>();
        foreach (var stage in query.Retrievals.Where(stage => stage.Weight > 0))
            rankings.Add(stage.Kind == KnowledgeRetrievalKind.Semantic
                ? await SearchSemanticStageAsync(connection, query, stage, collectionIds, semanticQuery ?? throw new InvalidOperationException("A semantic retrieval stage requires a query embedding."), metadata, cancellationToken).ConfigureAwait(false)
                : await SearchLexicalStageAsync(connection, query, stage, collectionIds, SemanticEntityKind.Document, cancellationToken).ConfigureAwait(false));
        var fused = SearchRankFusion.Fuse(rankings, new SearchFusionOptions { RankConstant = query.FusionRankConstant });
        return fused.Select(result => ToCandidate(result, query.Include)).ToArray();
    }

    private async Task<SearchStageRanking<Guid>> SearchSemanticStageAsync(NpgsqlConnection connection, KnowledgeSearchQuery query, KnowledgeRetrievalStage stage, IReadOnlyList<Guid> collectionIds, QueryEmbedding semanticQuery, Metadata metadata, CancellationToken cancellationToken)
    {
        var filter = PostgreSqlFilterCompiler.Compile(KnowledgeFilters.CombineAnd(query.Filter, stage.Filter));
        var scope = BuildScopeSql(collectionIds, query.IncludeDescendants, out var scopeParameters);
        var where = $"t.item_kind='document' AND t.knowledge_base_id=@sk_kb AND t.item_id IN (SELECT d.id FROM {Q("sk_documents")} d WHERE d.knowledge_base_id=@sk_kb AND ({filter.Sql}){scope})";
        var selected = stage.Fields.Where(field => field.Weight > 0).ToArray();
        var candidate = new PgVectorCandidateQuery
        {
            Table = Qualified(metadata.VectorTable), ItemKeyColumn = "item_id", FieldNameColumn = "field_name", FingerprintColumn = "fingerprint", VectorColumn = "embedding", RecordJsonColumn = "record_json", FieldWeightColumn = "field_weight", AdditionalWhereSql = where, StorageKind = metadata.StorageKind, SearchMode = PgVectorSearchMode.Exact,
            IncludeFields = selected.Length == 0 ? null : selected.Select(field => field.FieldKey).ToArray(),
            QueryFieldWeights = selected.Length == 0 ? null : selected.ToDictionary(field => field.FieldKey, field => field.Weight, StringComparer.OrdinalIgnoreCase)
        };
        var stageCount = query.ResolveCandidateCount(stage);
        var result = await semanticSearch.SearchAsync<Guid>(connection, semanticQuery, candidate, new DatabaseSemanticSearchOptions { Top = stageCount, CandidateCount = (int)Math.Min(int.MaxValue, Math.Max(100L, (long)stageCount * 10L)) }, command =>
        {
            command.Parameters.AddWithValue("sk_kb", query.KnowledgeBaseId); foreach (var parameter in filter.Parameters) command.Parameters.Add(Clone(parameter)); foreach (var parameter in scopeParameters) command.Parameters.Add(Clone(parameter));
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new SearchStageRanking<Guid> { StageName = stage.Name, Kind = SearchRetrievalKind.Semantic, Weight = stage.Weight, Candidates = result.Results.Where(item => item.Score > 0).Select(item => new SearchStageCandidate<Guid> { Item = item.Item, RawScore = item.Score, BestSemanticMatch = item.BestMatch, SemanticFields = item.Fields }).ToArray() };
    }

    private async Task<SearchStageRanking<Guid>> SearchLexicalStageAsync(NpgsqlConnection connection, KnowledgeSearchQuery query, KnowledgeRetrievalStage stage, IReadOnlyList<Guid> collectionIds, SemanticEntityKind kind, CancellationToken cancellationToken)
    {
        var fields = stage.Fields.Where(field => field.Weight > 0).ToArray();
        if (fields.Length == 0) fields = (await GetFieldKeysAsync(connection, query.KnowledgeBaseId, kind, cancellationToken).ConfigureAwait(false)).Select(KnowledgeSearchField.Create).ToArray();
        var fieldRankings = new List<SearchStageRanking<Guid>>(fields.Length);
        var filter = kind == SemanticEntityKind.Document ? PostgreSqlFilterCompiler.Compile(KnowledgeFilters.CombineAnd(query.Filter, stage.Filter)) : new PostgreSqlCompiledFilter("TRUE", Array.Empty<NpgsqlParameter>());
        IReadOnlyList<NpgsqlParameter> scopeParameters = Array.Empty<NpgsqlParameter>();
        var scope = kind == SemanticEntityKind.Document ? BuildScopeSql(collectionIds, query.IncludeDescendants, out scopeParameters) : string.Empty;
        var stageCount = query.ResolveCandidateCount(stage);
        foreach (var field in fields)
        {
            var where = new List<string> { "t.entity_kind=@sk_kind", "t.knowledge_base_id=@sk_kb", "t.field_key=@sk_field" };
            if (kind == SemanticEntityKind.Document) where.Add($"t.item_id IN (SELECT d.id FROM {Q("sk_documents")} d WHERE d.knowledge_base_id=@sk_kb AND ({filter.Sql}){scope})");
            var mapping = new PgVectorLexicalQuery
            {
                Table = Qualified(LexicalTable), ItemKeyColumn = "item_id", SearchVectorColumn = "search_vector", Fields = [new PgTextSearchField("text", PgTextSearchWeight.A)], TextSearchConfiguration = "simple", QueryMode = stage.LexicalMode == KnowledgeLexicalQueryMode.NativeSyntax ? PgTextSearchQueryMode.Native : PgTextSearchQueryMode.WebSearch, RankMode = PgTextSearchRankMode.CoverDensity, AdditionalWhereSql = string.Join(" AND ", where)
            };
            var result = await lexicalSearch.SearchAsync<Guid>(connection, query.Text, mapping, [SearchFieldWeight.Create("text")], new DatabaseLexicalSearchOptions { Top = stageCount }, command =>
            {
                command.Parameters.AddWithValue("sk_kind", (int)kind); command.Parameters.AddWithValue("sk_kb", query.KnowledgeBaseId); command.Parameters.AddWithValue("sk_field", field.FieldKey); foreach (var parameter in filter.Parameters) command.Parameters.Add(Clone(parameter)); foreach (var parameter in scopeParameters) command.Parameters.Add(Clone(parameter));
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
            fieldRankings.Add(new SearchStageRanking<Guid> { StageName = field.FieldKey, Kind = SearchRetrievalKind.Lexical, Weight = field.Weight, Candidates = result.Results.Select(item => new SearchStageCandidate<Guid> { Item = item.Item, RawScore = item.Score, LexicalFields = [new LexicalFieldMatch { Name = field.FieldKey, Weight = field.Weight, Score = item.Score }] }).ToArray() });
        }
        if (fieldRankings.Count == 0) return new SearchStageRanking<Guid> { StageName = stage.Name, Kind = SearchRetrievalKind.Lexical, Weight = stage.Weight, Candidates = Array.Empty<SearchStageCandidate<Guid>>() };
        var fused = SearchRankFusion.Fuse(fieldRankings, new SearchFusionOptions { RankConstant = query.FusionRankConstant });
        return new SearchStageRanking<Guid> { StageName = stage.Name, Kind = SearchRetrievalKind.Lexical, Weight = stage.Weight, Candidates = fused.Select(item => new SearchStageCandidate<Guid> { Item = item.Item, RawScore = item.Score, LexicalFields = item.Contributions.SelectMany(contribution => contribution.LexicalFields ?? Array.Empty<LexicalFieldMatch>()).ToArray() }).ToArray() };
    }

    private async Task<IReadOnlyList<Guid>> ResolveCollectionsAsync(NpgsqlConnection connection, KnowledgeSearchQuery query, QueryEmbedding? semanticQuery, Metadata metadata, CancellationToken cancellationToken)
    {
        if (query.Mode == KnowledgeSearchMode.Global) return Array.Empty<Guid>();
        if (query.Mode == KnowledgeSearchMode.Scoped) return query.CollectionIds;
        var ids = new HashSet<Guid>();
        if (semanticQuery is not null)
        {
            var route = await semanticSearch.SearchAsync<Guid>(connection, semanticQuery, new PgVectorCandidateQuery { Table = Qualified(metadata.VectorTable), ItemKeyColumn = "item_id", FieldNameColumn = "field_name", FingerprintColumn = "fingerprint", VectorColumn = "embedding", RecordJsonColumn = "record_json", FieldWeightColumn = "field_weight", StorageKind = metadata.StorageKind, SearchMode = PgVectorSearchMode.Exact, AdditionalWhereSql = "t.item_kind='collection' AND t.knowledge_base_id=@sk_kb" }, new DatabaseSemanticSearchOptions { Top = 8, CandidateCount = 100 }, command => command.Parameters.AddWithValue("sk_kb", query.KnowledgeBaseId), cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var item in route.Results.Where(item => item.Score > 0)) ids.Add(item.Item);
        }
        if (query.Retrievals.Any(stage => stage.Kind == KnowledgeRetrievalKind.Lexical && stage.Weight > 0))
        {
            var stage = KnowledgeRetrievalStage.Lexical("collection-route", KnowledgeSearchField.Title(3), KnowledgeSearchField.Tags(2), KnowledgeSearchField.Description()).Candidates(8);
            var lexical = await SearchLexicalStageAsync(connection, query, stage, Array.Empty<Guid>(), SemanticEntityKind.Collection, cancellationToken).ConfigureAwait(false); foreach (var item in lexical.Candidates) ids.Add(item.Item);
        }
        return ids.ToArray();
    }

    private static KnowledgeAdvancedSearchCandidate ToCandidate(SearchResult<Guid> result, KnowledgeResultInclude include)
    {
        var matches = result.Contributions.Where(item => item.Kind == SearchRetrievalKind.Semantic).SelectMany(item => item.SemanticFields ?? Array.Empty<SemanticFieldMatch>()).SelectMany(field => field.Matches.Select(match => new KnowledgeMatchedChunk { FieldKey = field.Name, RawSimilarity = match.RawSimilarity, AdjustedSimilarity = match.AdjustedSimilarity, TokenCount = match.Embedding.Source.TokenCount, CharacterRange = match.Embedding.Source.CharacterRange, Text = include == KnowledgeResultInclude.MetadataOnly ? null : match.Embedding.Text })).GroupBy(match => (match.FieldKey, match.CharacterRange.Start, match.CharacterRange.Length)).Select(group => group.OrderByDescending(match => match.AdjustedSimilarity).First()).ToArray();
        var contributions = result.Contributions.Select(item => new KnowledgeSearchContribution { StageName = item.StageName, Kind = item.Kind == SearchRetrievalKind.Semantic ? KnowledgeRetrievalKind.Semantic : KnowledgeRetrievalKind.Lexical, Rank = item.Rank, RawScore = item.RawScore, FusionContribution = item.FusionContribution, LexicalFields = (item.LexicalFields ?? Array.Empty<LexicalFieldMatch>()).Select(field => new KnowledgeLexicalFieldMatch { FieldKey = field.Name, Weight = field.Weight, Score = field.Score }).ToArray() }).ToArray();
        return new KnowledgeAdvancedSearchCandidate { DocumentId = result.Item, Score = result.Score, Matches = matches, Contributions = contributions };
    }

    private async Task<IReadOnlyList<string>> GetFieldKeysAsync(NpgsqlConnection connection, Guid knowledgeBaseId, SemanticEntityKind kind, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT DISTINCT field_key FROM {Q(LexicalTable)} WHERE knowledge_base_id=@kb AND entity_kind=@kind ORDER BY field_key", connection); command.Parameters.AddWithValue("kb", knowledgeBaseId); command.Parameters.AddWithValue("kind", (int)kind);
        var fields = new List<string>(); await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) fields.Add(reader.GetString(0)); return fields;
    }

    private string BuildScopeSql(IReadOnlyList<Guid> collections, bool descendants, out IReadOnlyList<NpgsqlParameter> parameters)
    {
        var ps = new List<NpgsqlParameter>(); parameters = ps; if (collections.Count == 0) return string.Empty; var names = new List<string>(); for (var i = 0; i < collections.Count; i++) { var name = $"sk_c{i}"; names.Add("@" + name); ps.Add(new NpgsqlParameter(name, collections[i])); }
        if (!descendants) return $" AND d.collection_id IN ({string.Join(',', names)})";
        var seeds = string.Join(" UNION ALL ", names.Select(name => $"SELECT {name}::uuid")); return $" AND d.collection_id IN (WITH RECURSIVE sk_scope(id) AS ({seeds} UNION ALL SELECT c.id FROM {Q("sk_collections")} c JOIN sk_scope s ON c.parent_collection_id=s.id) SELECT id FROM sk_scope)";
    }

    private async Task<Metadata> ReadMetadataAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT active_vector_table,active_storage_kind FROM {Q("sk_store_metadata")} WHERE singleton=1", connection); await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("SemanticKnowledge PostgreSQL metadata is missing."); return new Metadata(reader.GetString(0), (PgVectorStorageKind)reader.GetInt32(1));
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false); await using var command = new NpgsqlCommand($"SET search_path TO {QI(options.Schema)}, public", connection); await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); return connection;
    }

    private string Q(string table) => $"{QI(options.Schema)}.{QI(table)}";
    private string Qualified(string table) => $"{options.Schema}.{table}";
    private static string QI(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    private static NpgsqlParameter Clone(NpgsqlParameter parameter) => new(parameter.ParameterName, parameter.Value);
    private sealed record Metadata(string VectorTable, PgVectorStorageKind StorageKind);
}
