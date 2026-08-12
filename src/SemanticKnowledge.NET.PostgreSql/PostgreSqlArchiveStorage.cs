using System.Runtime.CompilerServices;
using Npgsql;

namespace SemanticKnowledge.PostgreSql;

internal sealed class PostgreSqlArchiveStorage(NpgsqlDataSource dataSource, SemanticKnowledgePostgreSqlOptions options) : IKnowledgeArchiveStorage
{
    public async Task<KnowledgeBaseRecord?> GetKnowledgeBaseAsync(Guid knowledgeBaseId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        string? externalId; string title; string description;
        await using (var command = new NpgsqlCommand($"SELECT external_id,title,description FROM {Q("sk_knowledge_bases")} WHERE id=@id", connection))
        {
            command.Parameters.AddWithValue("id", knowledgeBaseId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
            externalId = reader.IsDBNull(0) ? null : reader.GetString(0); title = reader.GetString(1); description = reader.GetString(2);
        }
        var tags = new List<string>();
        await using (var command = new NpgsqlCommand($"SELECT tag FROM {Q("sk_knowledge_base_tags")} WHERE knowledge_base_id=@id ORDER BY tag", connection))
        {
            command.Parameters.AddWithValue("id", knowledgeBaseId); await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) tags.Add(reader.GetString(0));
        }
        return new KnowledgeBaseRecord { Id = knowledgeBaseId, ExternalId = externalId, Title = title, Description = description, Tags = tags };
    }

    public async Task UpsertKnowledgeBaseAsync(KnowledgeBaseRecord knowledgeBase, CancellationToken cancellationToken = default)
    {
        ValidateKnowledgeBase(knowledgeBase); await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false); await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = new NpgsqlCommand($"INSERT INTO {Q("sk_knowledge_bases")}(id,external_id,title,description) VALUES(@id,@external,@title,@description) ON CONFLICT(id) DO UPDATE SET external_id=EXCLUDED.external_id,title=EXCLUDED.title,description=EXCLUDED.description", connection, transaction))
        { command.Parameters.AddWithValue("id", knowledgeBase.Id); command.Parameters.AddWithValue("external", (object?)knowledgeBase.ExternalId ?? DBNull.Value); command.Parameters.AddWithValue("title", knowledgeBase.Title); command.Parameters.AddWithValue("description", knowledgeBase.Description); await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
        await using (var delete = new NpgsqlCommand($"DELETE FROM {Q("sk_knowledge_base_tags")} WHERE knowledge_base_id=@id", connection, transaction)) { delete.Parameters.AddWithValue("id", knowledgeBase.Id); await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
        foreach (var tag in NormalizeTags(knowledgeBase.Tags)) { await using var command = new NpgsqlCommand($"INSERT INTO {Q("sk_knowledge_base_tags")}(knowledge_base_id,tag) VALUES(@id,@tag)", connection, transaction); command.Parameters.AddWithValue("id", knowledgeBase.Id); command.Parameters.AddWithValue("tag", tag); await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpsertCollectionAsync(KnowledgeCollectionRecord collection, CancellationToken cancellationToken = default)
    {
        ValidateCollection(collection); await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false); await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = new NpgsqlCommand($"INSERT INTO {Q("sk_collections")}(id,knowledge_base_id,parent_collection_id,default_schema_id,external_id,title,description) VALUES(@id,@kb,@parent,@schema,@external,@title,@description) ON CONFLICT(id) DO UPDATE SET knowledge_base_id=EXCLUDED.knowledge_base_id,parent_collection_id=EXCLUDED.parent_collection_id,default_schema_id=EXCLUDED.default_schema_id,external_id=EXCLUDED.external_id,title=EXCLUDED.title,description=EXCLUDED.description", connection, transaction))
        { command.Parameters.AddWithValue("id", collection.Id); command.Parameters.AddWithValue("kb", collection.KnowledgeBaseId); command.Parameters.AddWithValue("parent", (object?)collection.ParentCollectionId ?? DBNull.Value); command.Parameters.AddWithValue("schema", (object?)collection.DefaultSchemaId ?? DBNull.Value); command.Parameters.AddWithValue("external", (object?)collection.ExternalId ?? DBNull.Value); command.Parameters.AddWithValue("title", collection.Title); command.Parameters.AddWithValue("description", collection.Description); await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
        await using (var delete = new NpgsqlCommand($"DELETE FROM {Q("sk_collection_tags")} WHERE collection_id=@id", connection, transaction)) { delete.Parameters.AddWithValue("id", collection.Id); await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
        foreach (var tag in collection.Tags) { await using var command = new NpgsqlCommand($"INSERT INTO {Q("sk_collection_tags")}(collection_id,tag) VALUES(@id,@tag)", connection, transaction); command.Parameters.AddWithValue("id", collection.Id); command.Parameters.AddWithValue("tag", tag); await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<Guid> StreamDocumentIdsAsync(Guid knowledgeBaseId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false); await using var command = new NpgsqlCommand($"SELECT id FROM {Q("sk_documents")} WHERE knowledge_base_id=@kb ORDER BY id", connection); command.Parameters.AddWithValue("kb", knowledgeBaseId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) yield return reader.GetGuid(0);
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = new NpgsqlCommand($"SET search_path TO {QI(options.Schema)}, public", connection)) await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = new NpgsqlCommand($"CREATE TABLE IF NOT EXISTS {Q("sk_knowledge_base_tags")}(knowledge_base_id UUID NOT NULL,tag TEXT NOT NULL,PRIMARY KEY(knowledge_base_id,tag)); DELETE FROM {Q("sk_knowledge_base_tags")} t WHERE NOT EXISTS (SELECT 1 FROM {Q("sk_knowledge_bases")} kb WHERE kb.id=t.knowledge_base_id);", connection)) await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
    private string Q(string table) => $"{QI(options.Schema)}.{QI(table)}";
    private static string QI(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    private static IReadOnlyList<string> NormalizeTags(IEnumerable<string> tags) => tags.Where(tag => !string.IsNullOrWhiteSpace(tag)).Select(tag => tag.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private static void ValidateKnowledgeBase(KnowledgeBaseRecord value) { if (value.Id == Guid.Empty) throw new InvalidDataException("KnowledgeBase ID cannot be empty."); ArgumentException.ThrowIfNullOrWhiteSpace(value.Title); }
    private static void ValidateCollection(KnowledgeCollectionRecord value) { if (value.Id == Guid.Empty || value.KnowledgeBaseId == Guid.Empty) throw new InvalidDataException("Collection and KnowledgeBase IDs cannot be empty."); ArgumentException.ThrowIfNullOrWhiteSpace(value.Title); }
}
