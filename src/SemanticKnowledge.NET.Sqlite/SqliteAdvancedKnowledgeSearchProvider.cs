using Microsoft.Data.Sqlite;
using OnnxTextEmbeddings;
using OnnxTextEmbeddings.SqliteVec;

namespace SemanticKnowledge.Sqlite;

internal sealed class SqliteAdvancedKnowledgeSearchProvider(
    SemanticKnowledgeSqliteOptions options,
    SqliteVecSemanticSearch semanticSearch,
    SqliteFts5LexicalSearch lexicalSearch) : IKnowledgeAdvancedSearchProvider
{
    private const string LexicalTable = "sk_lexical_sources_fts";
    public string ProviderName => "SQLite FTS5 BM25";

    public async Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name";
        exists.Parameters.AddWithValue("$name", LexicalTable);
        var missing = Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 0;
        if (missing)
        {
            await using var create = connection.CreateCommand();
            create.CommandText = $"""
                CREATE VIRTUAL TABLE {LexicalTable} USING fts5(
                    item_id UNINDEXED,
                    entity_kind UNINDEXED,
                    knowledge_base_id UNINDEXED,
                    collection_id UNINDEXED,
                    document_id UNINDEXED,
                    field_key UNINDEXED,
                    text_value,
                    tokenize='unicode61'
                )
                """;
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        return missing;
    }

    public async Task UpsertSourcesAsync(Guid itemId, SemanticEntityKind kind, IReadOnlyList<LexicalSourceRecord> sources, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = $"DELETE FROM {LexicalTable} WHERE item_id=$item AND entity_kind=$kind";
            delete.Parameters.AddWithValue("$item", itemId.ToString("D"));
            delete.Parameters.AddWithValue("$kind", (int)kind);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        foreach (var source in sources)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = $"INSERT INTO {LexicalTable}(item_id,entity_kind,knowledge_base_id,collection_id,document_id,field_key,text_value) VALUES($item,$kind,$kb,$collection,$document,$field,$text)";
            insert.Parameters.AddWithValue("$item", source.ItemId.ToString("D"));
            insert.Parameters.AddWithValue("$kind", (int)source.EntityKind);
            insert.Parameters.AddWithValue("$kb", source.KnowledgeBaseId.ToString("D"));
            insert.Parameters.AddWithValue("$collection", source.CollectionId.ToString("D"));
            insert.Parameters.AddWithValue("$document", (object?)source.DocumentId?.ToString("D") ?? DBNull.Value);
            insert.Parameters.AddWithValue("$field", source.FieldKey);
            insert.Parameters.AddWithValue("$text", source.Text);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteSourcesAsync(Guid itemId, SemanticEntityKind kind, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {LexicalTable} WHERE item_id=$item AND entity_kind=$kind";
        command.Parameters.AddWithValue("$item", itemId.ToString("D")); command.Parameters.AddWithValue("$kind", (int)kind);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand(); command.CommandText = $"DROP TABLE IF EXISTS {LexicalTable}";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<KnowledgeAdvancedSearchCandidate>> SearchAsync(KnowledgeSearchQuery query, QueryEmbedding? semanticQuery, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var metadata = await ReadMetadataAsync(connection, cancellationToken).ConfigureAwait(false);
        var collectionIds = await ResolveCollectionsAsync(connection, query, semanticQuery, metadata, cancellationToken).ConfigureAwait(false);
        var rankings = new List<SearchStageRanking<Guid>>();

        foreach (var stage in query.Retrievals.Where(stage => stage.Weight > 0))
        {
            if (stage.Kind == KnowledgeRetrievalKind.Semantic)
            {
                if (semanticQuery is null) throw new InvalidOperationException("A semantic retrieval stage requires a query embedding.");
                rankings.Add(await SearchSemanticStageAsync(connection, query, stage, collectionIds, semanticQuery, metadata, cancellationToken).ConfigureAwait(false));
            }
            else
            {
                rankings.Add(await SearchLexicalStageAsync(connection, query, stage, collectionIds, SemanticEntityKind.Document, cancellationToken).ConfigureAwait(false));
            }
        }

        var fused = SearchRankFusion.Fuse(rankings, new SearchFusionOptions { RankConstant = query.FusionRankConstant });
        return fused.Select(result => ToCandidate(result, query.Include)).ToArray();
    }

    private async Task<SearchStageRanking<Guid>> SearchSemanticStageAsync(SqliteConnection connection, KnowledgeSearchQuery query, KnowledgeRetrievalStage stage, IReadOnlyList<Guid> collectionIds, QueryEmbedding semanticQuery, Metadata metadata, CancellationToken cancellationToken)
    {
        var filter = SqliteFilterCompiler.Compile(KnowledgeFilters.CombineAnd(query.Filter, stage.Filter));
        var scope = BuildScopeSql(collectionIds, query.IncludeDescendants, out var scopeParameters);
        var where = $"item_kind='document' AND knowledge_base_id=$sk_kb AND item_id IN (SELECT d.id FROM sk_documents d WHERE d.knowledge_base_id=$sk_kb AND ({filter.Sql}){scope})";
        var selected = stage.Fields.Where(field => field.Weight > 0).ToArray();
        var candidateQuery = new SqliteVecCandidateQuery
        {
            Table = metadata.VectorTable,
            ItemKeyColumn = "item_id",
            FieldNameColumn = "field_name",
            FingerprintColumn = "fingerprint",
            VectorColumn = "embedding",
            RecordJsonColumn = "record_json",
            FieldWeightColumn = "field_weight",
            AdditionalWhereSql = where,
            StorageKind = metadata.StorageKind,
            IncludeFields = selected.Length == 0 ? null : selected.Select(field => field.FieldKey).ToArray(),
            QueryFieldWeights = selected.Length == 0 ? null : selected.ToDictionary(field => field.FieldKey, field => field.Weight, StringComparer.OrdinalIgnoreCase)
        };
        var stageCount = query.ResolveCandidateCount(stage);
        var result = await semanticSearch.SearchAsync<Guid>(connection, semanticQuery, candidateQuery,
            new DatabaseSemanticSearchOptions { Top = stageCount, CandidateCount = (int)Math.Min(int.MaxValue, Math.Max(100L, (long)stageCount * 10L)) },
            command =>
            {
                command.Parameters.AddWithValue("$sk_kb", query.KnowledgeBaseId.ToString("D"));
                foreach (var parameter in filter.Parameters) command.Parameters.Add(Clone(parameter));
                foreach (var parameter in scopeParameters) command.Parameters.Add(Clone(parameter));
            }, cancellationToken).ConfigureAwait(false);
        return new SearchStageRanking<Guid>
        {
            StageName = stage.Name,
            Kind = SearchRetrievalKind.Semantic,
            Weight = stage.Weight,
            Candidates = result.Results.Where(item => item.Score > 0).Select(item => new SearchStageCandidate<Guid>
            {
                Item = item.Item, RawScore = item.Score, BestSemanticMatch = item.BestMatch, SemanticFields = item.Fields
            }).ToArray()
        };
    }

    private async Task<SearchStageRanking<Guid>> SearchLexicalStageAsync(SqliteConnection connection, KnowledgeSearchQuery query, KnowledgeRetrievalStage stage, IReadOnlyList<Guid> collectionIds, SemanticEntityKind kind, CancellationToken cancellationToken)
    {
        var fields = stage.Fields.Where(field => field.Weight > 0).ToArray();
        if (fields.Length == 0)
            fields = (await GetFieldKeysAsync(connection, query.KnowledgeBaseId, kind, cancellationToken).ConfigureAwait(false)).Select(key => KnowledgeSearchField.Create(key)).ToArray();
        var fieldRankings = new List<SearchStageRanking<Guid>>(fields.Length);
        var filter = kind == SemanticEntityKind.Document ? SqliteFilterCompiler.Compile(KnowledgeFilters.CombineAnd(query.Filter, stage.Filter)) : new SqliteCompiledFilter("1=1", Array.Empty<SqliteParameter>());
        var scope = kind == SemanticEntityKind.Document ? BuildScopeSql(collectionIds, query.IncludeDescendants, out var scopeParams) : string.Empty;
        if (kind != SemanticEntityKind.Document) scopeParams = Array.Empty<SqliteParameter>();
        var stageCount = query.ResolveCandidateCount(stage);

        foreach (var field in fields)
        {
            var where = new List<string> { "entity_kind=$sk_kind", "knowledge_base_id=$sk_kb", "field_key=$sk_field" };
            if (kind == SemanticEntityKind.Document) where.Add($"item_id IN (SELECT d.id FROM sk_documents d WHERE d.knowledge_base_id=$sk_kb AND ({filter.Sql}){scope})");
            var mapping = new SqliteFts5Query
            {
                Table = LexicalTable,
                ItemKeyColumn = "item_id",
                ColumnOrder = ["item_id", "entity_kind", "knowledge_base_id", "collection_id", "document_id", "field_key", "text_value"],
                Fields = [new SqliteFts5Field("text", "text_value")],
                QueryMode = stage.LexicalMode == KnowledgeLexicalQueryMode.NativeSyntax ? SqliteFts5QueryMode.NativeSyntax : SqliteFts5QueryMode.Plain,
                AdditionalWhereSql = string.Join(" AND ", where)
            };
            var result = await lexicalSearch.SearchAsync<Guid>(connection, query.Text, mapping, [SearchFieldWeight.Create("text")], new DatabaseLexicalSearchOptions { Top = stageCount }, command =>
            {
                command.Parameters.AddWithValue("$sk_kind", (int)kind);
                command.Parameters.AddWithValue("$sk_kb", query.KnowledgeBaseId.ToString("D"));
                command.Parameters.AddWithValue("$sk_field", field.FieldKey);
                foreach (var parameter in filter.Parameters) command.Parameters.Add(Clone(parameter));
                foreach (var parameter in scopeParams) command.Parameters.Add(Clone(parameter));
            }, cancellationToken).ConfigureAwait(false);
            fieldRankings.Add(new SearchStageRanking<Guid>
            {
                StageName = field.FieldKey,
                Kind = SearchRetrievalKind.Lexical,
                Weight = field.Weight,
                Candidates = result.Results.Select(item => new SearchStageCandidate<Guid>
                {
                    Item = item.Item,
                    RawScore = item.Score,
                    LexicalFields = [new LexicalFieldMatch { Name = field.FieldKey, Weight = field.Weight, Score = item.Score }]
                }).ToArray()
            });
        }

        if (fieldRankings.Count == 0) return new SearchStageRanking<Guid> { StageName = stage.Name, Kind = SearchRetrievalKind.Lexical, Weight = stage.Weight, Candidates = Array.Empty<SearchStageCandidate<Guid>>() };
        var fieldFused = SearchRankFusion.Fuse(fieldRankings, new SearchFusionOptions { RankConstant = query.FusionRankConstant });
        return new SearchStageRanking<Guid>
        {
            StageName = stage.Name,
            Kind = SearchRetrievalKind.Lexical,
            Weight = stage.Weight,
            Candidates = fieldFused.Select(item => new SearchStageCandidate<Guid>
            {
                Item = item.Item,
                RawScore = item.Score,
                LexicalFields = item.Contributions.SelectMany(contribution => contribution.LexicalFields ?? Array.Empty<LexicalFieldMatch>()).ToArray()
            }).ToArray()
        };
    }

    private async Task<IReadOnlyList<Guid>> ResolveCollectionsAsync(SqliteConnection connection, KnowledgeSearchQuery query, QueryEmbedding? semanticQuery, Metadata metadata, CancellationToken cancellationToken)
    {
        if (query.Mode == KnowledgeSearchMode.Global) return Array.Empty<Guid>();
        if (query.Mode == KnowledgeSearchMode.Scoped) return query.CollectionIds;
        var ids = new HashSet<Guid>();
        if (semanticQuery is not null)
        {
            var route = await semanticSearch.SearchAsync<Guid>(connection, semanticQuery, new SqliteVecCandidateQuery
            {
                Table = metadata.VectorTable, ItemKeyColumn = "item_id", FieldNameColumn = "field_name", FingerprintColumn = "fingerprint", VectorColumn = "embedding", RecordJsonColumn = "record_json", FieldWeightColumn = "field_weight", StorageKind = metadata.StorageKind,
                AdditionalWhereSql = "item_kind='collection' AND knowledge_base_id=$sk_kb"
            }, new DatabaseSemanticSearchOptions { Top = 8, CandidateCount = 100 }, command => command.Parameters.AddWithValue("$sk_kb", query.KnowledgeBaseId.ToString("D")), cancellationToken).ConfigureAwait(false);
            foreach (var item in route.Results.Where(item => item.Score > 0)) ids.Add(item.Item);
        }
        if (query.Retrievals.Any(stage => stage.Kind == KnowledgeRetrievalKind.Lexical && stage.Weight > 0))
        {
            var routeStage = KnowledgeRetrievalStage.Lexical("collection-route", KnowledgeSearchField.Title(3), KnowledgeSearchField.Tags(2), KnowledgeSearchField.Description()).Candidates(8);
            var lexical = await SearchLexicalStageAsync(connection, query, routeStage, Array.Empty<Guid>(), SemanticEntityKind.Collection, cancellationToken).ConfigureAwait(false);
            foreach (var item in lexical.Candidates) ids.Add(item.Item);
        }
        return ids.ToArray();
    }

    private static KnowledgeAdvancedSearchCandidate ToCandidate(SearchResult<Guid> result, KnowledgeResultInclude include)
    {
        var matches = result.Contributions.Where(item => item.Kind == SearchRetrievalKind.Semantic)
            .SelectMany(item => item.SemanticFields ?? Array.Empty<SemanticFieldMatch>())
            .SelectMany(field => field.Matches.Select(match => new KnowledgeMatchedChunk
            {
                FieldKey = field.Name, RawSimilarity = match.RawSimilarity, AdjustedSimilarity = match.AdjustedSimilarity,
                TokenCount = match.Embedding.Source.TokenCount, CharacterRange = match.Embedding.Source.CharacterRange,
                Text = include == KnowledgeResultInclude.MetadataOnly ? null : match.Embedding.Text
            }))
            .GroupBy(match => (match.FieldKey, match.CharacterRange.Start, match.CharacterRange.Length))
            .Select(group => group.OrderByDescending(match => match.AdjustedSimilarity).First()).ToArray();
        var contributions = result.Contributions.Select(item => new KnowledgeSearchContribution
        {
            StageName = item.StageName,
            Kind = item.Kind == SearchRetrievalKind.Semantic ? KnowledgeRetrievalKind.Semantic : KnowledgeRetrievalKind.Lexical,
            Rank = item.Rank,
            RawScore = item.RawScore,
            FusionContribution = item.FusionContribution,
            LexicalFields = (item.LexicalFields ?? Array.Empty<LexicalFieldMatch>()).Select(field => new KnowledgeLexicalFieldMatch { FieldKey = field.Name, Weight = field.Weight, Score = field.Score }).ToArray()
        }).ToArray();
        return new KnowledgeAdvancedSearchCandidate { DocumentId = result.Item, Score = result.Score, Matches = matches, Contributions = contributions };
    }

    private async Task<IReadOnlyList<string>> GetFieldKeysAsync(SqliteConnection connection, Guid knowledgeBaseId, SemanticEntityKind kind, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT DISTINCT field_key FROM {LexicalTable} WHERE knowledge_base_id=$kb AND entity_kind=$kind ORDER BY field_key";
        command.Parameters.AddWithValue("$kb", knowledgeBaseId.ToString("D")); command.Parameters.AddWithValue("$kind", (int)kind);
        var fields = new List<string>(); await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) fields.Add(reader.GetString(0)); return fields;
    }

    private static string BuildScopeSql(IReadOnlyList<Guid> collections, bool descendants, out IReadOnlyList<SqliteParameter> parameters)
    {
        var ps = new List<SqliteParameter>(); parameters = ps; if (collections.Count == 0) return string.Empty;
        var names = new List<string>(); for (var i = 0; i < collections.Count; i++) { var name = $"$sk_c{i}"; names.Add(name); ps.Add(new SqliteParameter(name, collections[i].ToString("D"))); }
        if (!descendants) return $" AND d.collection_id IN ({string.Join(',', names)})";
        var seeds = string.Join(" UNION ALL ", names.Select(name => $"SELECT {name}"));
        return $" AND d.collection_id IN (WITH RECURSIVE sk_scope(id) AS ({seeds} UNION ALL SELECT c.id FROM sk_collections c JOIN sk_scope s ON c.parent_collection_id=s.id) SELECT id FROM sk_scope)";
    }

    private async Task<Metadata> ReadMetadataAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT active_vector_table,active_storage_kind FROM sk_store_metadata WHERE singleton=1";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("SemanticKnowledge SQLite metadata is missing.");
        return new Metadata(reader.GetString(0), (SqliteVecStorageKind)reader.GetInt32(1));
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(options.ConnectionString); connection.LoadOnnxTextEmbeddingsSqliteVec(); await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (options.ForeignKeys) { await using var command = connection.CreateCommand(); command.CommandText = "PRAGMA foreign_keys=ON"; await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
        return connection;
    }

    private static SqliteParameter Clone(SqliteParameter parameter) => new(parameter.ParameterName, parameter.Value);
    private sealed record Metadata(string VectorTable, SqliteVecStorageKind StorageKind);
}
