using Microsoft.Data.SqlClient;
using OnnxTextEmbeddings;
using OnnxTextEmbeddings.SqlServer;

namespace SemanticKnowledge.SqlServer;

internal sealed class SqlServerAdvancedKnowledgeSearchProvider(
    SemanticKnowledgeSqlServerOptions options,
    SqlServerSemanticSearch semanticSearch,
    SqlServerFullTextSearch lexicalSearch) : IKnowledgeAdvancedSearchProvider
{
    private const string LexicalTable = "sk_lexical_sources";
    private const string FullTextCatalog = "SemanticKnowledgeSearch";
    private const string PrimaryKeyName = "PK_sk_lexical_sources";
    private bool _lexicalSearchAvailable;

    public string ProviderName => "SQL Server Full-Text Search";
    public bool LexicalSearchAvailable => _lexicalSearchAvailable;

    public async Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var probe = new SqlCommand("SELECT FULLTEXTSERVICEPROPERTY('IsFullTextInstalled')", connection))
        {
            var installed = Convert.ToInt32(await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0);
            _lexicalSearchAvailable = installed == 1;
        }
        if (!_lexicalSearchAvailable) return false;

        var qualifiedLiteral = EscapeLiteral($"{options.Schema}.{LexicalTable}");
        await using var exists = new SqlCommand($"SELECT CASE WHEN OBJECT_ID(N'{qualifiedLiteral}', N'U') IS NULL THEN 0 ELSE 1 END", connection);
        var missing = Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0) == 0;
        if (missing)
        {
            await using var create = new SqlCommand($"""
                CREATE TABLE {Q(LexicalTable)}(
                    lexical_id bigint IDENTITY(1,1) NOT NULL,
                    source_id uniqueidentifier NOT NULL,
                    item_id uniqueidentifier NOT NULL,
                    entity_kind int NOT NULL,
                    knowledge_base_id uniqueidentifier NOT NULL,
                    collection_id uniqueidentifier NOT NULL,
                    document_id uniqueidentifier NULL,
                    field_id uniqueidentifier NOT NULL,
                    field_key nvarchar(450) NOT NULL,
                    text_value nvarchar(max) NOT NULL,
                    CONSTRAINT {QI(PrimaryKeyName)} PRIMARY KEY (lexical_id)
                );
                CREATE INDEX IX_sk_sql_lexical_scope ON {Q(LexicalTable)}(knowledge_base_id,entity_kind,field_key);
                CREATE INDEX IX_sk_sql_lexical_item ON {Q(LexicalTable)}(item_id,entity_kind);
                """, connection);
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var catalog = new SqlCommand($"IF NOT EXISTS(SELECT 1 FROM sys.fulltext_catalogs WHERE name=N'{EscapeLiteral(FullTextCatalog)}') CREATE FULLTEXT CATALOG {QI(FullTextCatalog)};", connection))
            await catalog.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using (var fullText = new SqlCommand($"""
            IF NOT EXISTS(SELECT 1 FROM sys.fulltext_indexes WHERE object_id=OBJECT_ID(N'{qualifiedLiteral}'))
            BEGIN
                CREATE FULLTEXT INDEX ON {Q(LexicalTable)}(text_value LANGUAGE 1033)
                KEY INDEX {QI(PrimaryKeyName)}
                ON {QI(FullTextCatalog)}
                WITH CHANGE_TRACKING AUTO;
            END
            """, connection))
            await fullText.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return missing;
    }

    public async Task UpsertSourcesAsync(Guid itemId, SemanticEntityKind kind, IReadOnlyList<LexicalSourceRecord> sources, CancellationToken cancellationToken = default)
    {
        if (!_lexicalSearchAvailable) return;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var delete = new SqlCommand($"DELETE FROM {Q(LexicalTable)} WHERE item_id=@item AND entity_kind=@kind", connection, transaction))
        {
            delete.Parameters.AddWithValue("@item", itemId); delete.Parameters.AddWithValue("@kind", (int)kind);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        foreach (var source in sources)
        {
            await using var insert = new SqlCommand($"INSERT INTO {Q(LexicalTable)}(source_id,item_id,entity_kind,knowledge_base_id,collection_id,document_id,field_id,field_key,text_value) VALUES(@source,@item,@kind,@kb,@collection,@document,@fieldId,@fieldKey,@text)", connection, transaction);
            insert.Parameters.AddWithValue("@source", source.Id); insert.Parameters.AddWithValue("@item", source.ItemId); insert.Parameters.AddWithValue("@kind", (int)source.EntityKind); insert.Parameters.AddWithValue("@kb", source.KnowledgeBaseId); insert.Parameters.AddWithValue("@collection", source.CollectionId); insert.Parameters.AddWithValue("@document", (object?)source.DocumentId ?? DBNull.Value); insert.Parameters.AddWithValue("@fieldId", source.FieldId); insert.Parameters.AddWithValue("@fieldKey", source.FieldKey); insert.Parameters.AddWithValue("@text", source.Text);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteSourcesAsync(Guid itemId, SemanticEntityKind kind, CancellationToken cancellationToken = default)
    {
        if (!_lexicalSearchAvailable) return;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand($"DELETE FROM {Q(LexicalTable)} WHERE item_id=@item AND entity_kind=@kind", connection);
        command.Parameters.AddWithValue("@item", itemId); command.Parameters.AddWithValue("@kind", (int)kind);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        if (!_lexicalSearchAvailable) return;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var qualifiedLiteral = EscapeLiteral($"{options.Schema}.{LexicalTable}");
        await using var command = new SqlCommand($"""
            IF EXISTS(SELECT 1 FROM sys.fulltext_indexes WHERE object_id=OBJECT_ID(N'{qualifiedLiteral}'))
                DROP FULLTEXT INDEX ON {Q(LexicalTable)};
            DROP TABLE IF EXISTS {Q(LexicalTable)};
            """, connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    private async Task<SearchStageRanking<Guid>> SearchSemanticStageAsync(SqlConnection connection, KnowledgeSearchQuery query, KnowledgeRetrievalStage stage, IReadOnlyList<Guid> collectionIds, QueryEmbedding semanticQuery, Metadata metadata, CancellationToken cancellationToken)
    {
        var filter = SqlServerFilterCompiler.Compile(KnowledgeFilters.CombineAnd(query.Filter, stage.Filter), options.Schema);
        var scope = BuildScopeSql(collectionIds, out var scopeParameters);
        var where = $"t.item_kind='document' AND t.knowledge_base_id=@sk_kb AND t.item_id IN (SELECT d.id FROM {Q("sk_documents")} d WHERE d.knowledge_base_id=@sk_kb AND ({filter.Sql}){scope})";
        var selected = stage.Fields.Where(field => field.Weight > 0).ToArray();
        var candidate = new SqlServerCandidateQuery
        {
            Table = Qualified(metadata.VectorTable), ItemKeyColumn = "item_id", FieldNameColumn = "field_name", FingerprintColumn = "fingerprint", VectorColumn = "embedding", RecordJsonColumn = "record_json", FieldWeightColumn = "field_weight", AdditionalWhereSql = where, VectorDimensions = metadata.Dimensions, SearchMode = SqlServerVectorSearchMode.Exact,
            IncludeFields = selected.Length == 0 ? null : selected.Select(field => field.FieldKey).ToArray(),
            QueryFieldWeights = selected.Length == 0 ? null : selected.ToDictionary(field => field.FieldKey, field => field.Weight, StringComparer.OrdinalIgnoreCase)
        };
        var stageCount = query.ResolveCandidateCount(stage);
        var result = await semanticSearch.SearchAsync<Guid>(connection, semanticQuery, candidate, new DatabaseSemanticSearchOptions { Top = stageCount, CandidateCount = (int)Math.Min(int.MaxValue, Math.Max(100L, (long)stageCount * 10L)) }, command =>
        {
            command.Parameters.AddWithValue("@sk_kb", query.KnowledgeBaseId); foreach (var parameter in filter.Parameters) command.Parameters.Add(Clone(parameter)); foreach (var parameter in scopeParameters) command.Parameters.Add(Clone(parameter));
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new SearchStageRanking<Guid> { StageName = stage.Name, Kind = SearchRetrievalKind.Semantic, Weight = stage.Weight, Candidates = result.Results.Where(item => item.Score > 0).Select(item => new SearchStageCandidate<Guid> { Item = item.Item, RawScore = item.Score, BestSemanticMatch = item.BestMatch, SemanticFields = item.Fields }).ToArray() };
    }

    private async Task<SearchStageRanking<Guid>> SearchLexicalStageAsync(SqlConnection connection, KnowledgeSearchQuery query, KnowledgeRetrievalStage stage, IReadOnlyList<Guid> collectionIds, SemanticEntityKind kind, CancellationToken cancellationToken)
    {
        if (!_lexicalSearchAvailable)
            throw new NotSupportedException("SQL Server Full-Text Search is not installed for this instance. Semantic search remains available; install the SQL Server Full-Text component to use lexical or hybrid retrieval.");

        var fields = stage.Fields.Where(field => field.Weight > 0).ToArray();
        if (fields.Length == 0) fields = (await GetFieldKeysAsync(connection, query.KnowledgeBaseId, kind, cancellationToken).ConfigureAwait(false)).Select(key => KnowledgeSearchField.Create(key)).ToArray();
        var fieldRankings = new List<SearchStageRanking<Guid>>(fields.Length);
        var filter = kind == SemanticEntityKind.Document ? SqlServerFilterCompiler.Compile(KnowledgeFilters.CombineAnd(query.Filter, stage.Filter), options.Schema) : new SqlServerCompiledFilter("1=1", Array.Empty<SqlParameter>());
        IReadOnlyList<SqlParameter> scopeParameters = Array.Empty<SqlParameter>();
        var scope = kind == SemanticEntityKind.Document ? BuildScopeSql(collectionIds, out scopeParameters) : string.Empty;
        var stageCount = query.ResolveCandidateCount(stage);
        foreach (var field in fields)
        {
            var where = new List<string> { "t.entity_kind=@sk_kind", "t.knowledge_base_id=@sk_kb", "t.field_key=@sk_field" };
            if (kind == SemanticEntityKind.Document) where.Add($"t.item_id IN (SELECT d.id FROM {Q("sk_documents")} d WHERE d.knowledge_base_id=@sk_kb AND ({filter.Sql}){scope})");
            var mapping = new SqlServerLexicalQuery
            {
                Table = Qualified(LexicalTable), ItemKeyColumn = "item_id", FullTextKeyColumn = "lexical_id", Fields = [new SqlServerFullTextField("text", "text_value")], QueryMode = stage.LexicalMode == KnowledgeLexicalQueryMode.NativeSyntax ? SqlServerFullTextQueryMode.Contains : SqlServerFullTextQueryMode.FreeText, AdditionalWhereSql = string.Join(" AND ", where), FieldFusionRankConstant = query.FusionRankConstant
            };
            var result = await lexicalSearch.SearchAsync<Guid>(connection, query.Text, mapping, [SearchFieldWeight.Create("text")], new DatabaseLexicalSearchOptions { Top = stageCount }, command =>
            {
                command.Parameters.AddWithValue("@sk_kind", (int)kind); command.Parameters.AddWithValue("@sk_kb", query.KnowledgeBaseId); command.Parameters.AddWithValue("@sk_field", field.FieldKey); foreach (var parameter in filter.Parameters) command.Parameters.Add(Clone(parameter)); foreach (var parameter in scopeParameters) command.Parameters.Add(Clone(parameter));
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
            fieldRankings.Add(new SearchStageRanking<Guid> { StageName = field.FieldKey, Kind = SearchRetrievalKind.Lexical, Weight = field.Weight, Candidates = result.Results.Select(item => new SearchStageCandidate<Guid> { Item = item.Item, RawScore = item.Score, LexicalFields = [new LexicalFieldMatch { Name = field.FieldKey, Weight = field.Weight, Score = item.Score }] }).ToArray() });
        }
        if (fieldRankings.Count == 0) return new SearchStageRanking<Guid> { StageName = stage.Name, Kind = SearchRetrievalKind.Lexical, Weight = stage.Weight, Candidates = Array.Empty<SearchStageCandidate<Guid>>() };
        var fused = SearchRankFusion.Fuse(fieldRankings, new SearchFusionOptions { RankConstant = query.FusionRankConstant });
        return new SearchStageRanking<Guid> { StageName = stage.Name, Kind = SearchRetrievalKind.Lexical, Weight = stage.Weight, Candidates = fused.Select(item => new SearchStageCandidate<Guid> { Item = item.Item, RawScore = item.Score, LexicalFields = item.Contributions.SelectMany(contribution => contribution.LexicalFields ?? Array.Empty<LexicalFieldMatch>()).ToArray() }).ToArray() };
    }

    private async Task<IReadOnlyList<Guid>> ResolveCollectionsAsync(SqlConnection connection, KnowledgeSearchQuery query, QueryEmbedding? semanticQuery, Metadata metadata, CancellationToken cancellationToken)
    {
        if (query.Mode == KnowledgeSearchMode.Global) return Array.Empty<Guid>();
        if (query.Mode == KnowledgeSearchMode.Scoped)
            return query.IncludeDescendants ? await ExpandCollectionsAsync(connection, query.CollectionIds, cancellationToken).ConfigureAwait(false) : query.CollectionIds;

        var ids = new HashSet<Guid>();
        if (semanticQuery is not null)
        {
            var route = await semanticSearch.SearchAsync<Guid>(connection, semanticQuery, new SqlServerCandidateQuery { Table = Qualified(metadata.VectorTable), ItemKeyColumn = "item_id", FieldNameColumn = "field_name", FingerprintColumn = "fingerprint", VectorColumn = "embedding", RecordJsonColumn = "record_json", FieldWeightColumn = "field_weight", VectorDimensions = metadata.Dimensions, SearchMode = SqlServerVectorSearchMode.Exact, AdditionalWhereSql = "t.item_kind='collection' AND t.knowledge_base_id=@sk_kb" }, new DatabaseSemanticSearchOptions { Top = 8, CandidateCount = 100 }, command => command.Parameters.AddWithValue("@sk_kb", query.KnowledgeBaseId), cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var item in route.Results.Where(item => item.Score > 0)) ids.Add(item.Item);
        }
        if (query.Retrievals.Any(stage => stage.Kind == KnowledgeRetrievalKind.Lexical && stage.Weight > 0))
        {
            var stage = KnowledgeRetrievalStage.Lexical("collection-route", KnowledgeSearchField.Title(3), KnowledgeSearchField.Tags(2), KnowledgeSearchField.Description()).Candidates(8);
            var lexical = await SearchLexicalStageAsync(connection, query, stage, Array.Empty<Guid>(), SemanticEntityKind.Collection, cancellationToken).ConfigureAwait(false); foreach (var item in lexical.Candidates) ids.Add(item.Item);
        }
        var result = ids.ToArray();
        return query.IncludeDescendants && result.Length > 0 ? await ExpandCollectionsAsync(connection, result, cancellationToken).ConfigureAwait(false) : result;
    }

    private static KnowledgeAdvancedSearchCandidate ToCandidate(SearchResult<Guid> result, KnowledgeResultInclude include)
    {
        var matches = result.Contributions.Where(item => item.Kind == SearchRetrievalKind.Semantic).SelectMany(item => item.SemanticFields ?? Array.Empty<SemanticFieldMatch>()).SelectMany(field => field.Matches.Select(match => new KnowledgeMatchedChunk { FieldKey = field.Name, RawSimilarity = match.RawSimilarity, AdjustedSimilarity = match.AdjustedSimilarity, TokenCount = match.Embedding.Source.TokenCount, CharacterRange = match.Embedding.Source.CharacterRange, Text = include == KnowledgeResultInclude.MetadataOnly ? null : match.Embedding.Text })).GroupBy(match => (match.FieldKey, match.CharacterRange.Start, match.CharacterRange.Length)).Select(group => group.OrderByDescending(match => match.AdjustedSimilarity).First()).ToArray();
        var contributions = result.Contributions.Select(item => new KnowledgeSearchContribution { StageName = item.StageName, Kind = item.Kind == SearchRetrievalKind.Semantic ? KnowledgeRetrievalKind.Semantic : KnowledgeRetrievalKind.Lexical, Rank = item.Rank, RawScore = item.RawScore, FusionContribution = item.FusionContribution, LexicalFields = (item.LexicalFields ?? Array.Empty<LexicalFieldMatch>()).Select(field => new KnowledgeLexicalFieldMatch { FieldKey = field.Name, Weight = field.Weight, Score = field.Score }).ToArray() }).ToArray();
        return new KnowledgeAdvancedSearchCandidate { DocumentId = result.Item, Score = result.Score, Matches = matches, Contributions = contributions };
    }

    private async Task<IReadOnlyList<string>> GetFieldKeysAsync(SqlConnection connection, Guid knowledgeBaseId, SemanticEntityKind kind, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand($"SELECT DISTINCT field_key FROM {Q(LexicalTable)} WHERE knowledge_base_id=@kb AND entity_kind=@kind ORDER BY field_key", connection); command.Parameters.AddWithValue("@kb", knowledgeBaseId); command.Parameters.AddWithValue("@kind", (int)kind);
        var fields = new List<string>(); await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) fields.Add(reader.GetString(0)); return fields;
    }

    private async Task<Guid[]> ExpandCollectionsAsync(SqlConnection connection, IReadOnlyList<Guid> seeds, CancellationToken cancellationToken)
    {
        if (seeds.Count == 0) return [];
        var names = new string[seeds.Count]; await using var command = new SqlCommand(); command.Connection = connection;
        for (var i = 0; i < seeds.Count; i++) { names[i] = $"@c{i}"; command.Parameters.AddWithValue(names[i], seeds[i]); }
        command.CommandText = $"WITH sk_scope AS (SELECT id FROM {Q("sk_collections")} WHERE id IN ({string.Join(',', names)}) UNION ALL SELECT c.id FROM {Q("sk_collections")} c JOIN sk_scope s ON c.parent_collection_id=s.id) SELECT DISTINCT id FROM sk_scope OPTION (MAXRECURSION 32767)";
        var result = new List<Guid>(); await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(reader.GetGuid(0)); return result.ToArray();
    }

    private static string BuildScopeSql(IReadOnlyList<Guid> collections, out IReadOnlyList<SqlParameter> parameters)
    {
        var ps = new List<SqlParameter>(); parameters = ps; if (collections.Count == 0) return string.Empty; var names = new string[collections.Count];
        for (var i = 0; i < collections.Count; i++) { names[i] = $"@sk_c{i}"; ps.Add(new SqlParameter(names[i], collections[i])); }
        return $" AND d.collection_id IN ({string.Join(',', names)})";
    }

    private async Task<Metadata> ReadMetadataAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand($"SELECT active_vector_table,active_dimensions FROM {Q("sk_store_metadata")} WHERE singleton=1", connection); await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("SemanticKnowledge SQL Server metadata is missing."); return new Metadata(reader.GetString(0), reader.GetInt32(1));
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(options.ConnectionString); await connection.OpenAsync(cancellationToken).ConfigureAwait(false); return connection;
    }

    private string Q(string table) => $"{QI(options.Schema)}.{QI(table)}";
    private string Qualified(string table) => $"{options.Schema}.{table}";
    private static string QI(string value) => $"[{value.Replace("]", "]]", StringComparison.Ordinal)}]";
    private static string EscapeLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);
    private static SqlParameter Clone(SqlParameter parameter) => new(parameter.ParameterName, parameter.Value);
    private sealed record Metadata(string VectorTable, int Dimensions);
}
