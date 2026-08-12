using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;

namespace SemanticKnowledge.SqlServer;

internal sealed class SqlServerArchiveStorage(SemanticKnowledgeSqlServerOptions options) : IKnowledgeArchiveStorage
{
    public async Task<KnowledgeBaseRecord?> GetKnowledgeBaseAsync(Guid knowledgeBaseId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false); await using var command = new SqlCommand($"SELECT external_id,title,description FROM {Q("sk_knowledge_bases")} WHERE id=@id", connection); command.Parameters.AddWithValue("@id", knowledgeBaseId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return new KnowledgeBaseRecord { Id = knowledgeBaseId, ExternalId = reader.IsDBNull(0) ? null : reader.GetString(0), Title = reader.GetString(1), Description = reader.GetString(2) };
    }

    public async Task UpsertKnowledgeBaseAsync(KnowledgeBaseRecord knowledgeBase, CancellationToken cancellationToken = default)
    {
        ValidateKnowledgeBase(knowledgeBase); await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand($"UPDATE {Q("sk_knowledge_bases")} SET external_id=@external,title=@title,description=@description WHERE id=@id; IF @@ROWCOUNT=0 INSERT INTO {Q("sk_knowledge_bases")}(id,external_id,title,description) VALUES(@id,@external,@title,@description);", connection);
        command.Parameters.AddWithValue("@id", knowledgeBase.Id); command.Parameters.AddWithValue("@external", (object?)knowledgeBase.ExternalId ?? DBNull.Value); command.Parameters.AddWithValue("@title", knowledgeBase.Title); command.Parameters.AddWithValue("@description", knowledgeBase.Description); await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpsertCollectionAsync(KnowledgeCollectionRecord collection, CancellationToken cancellationToken = default)
    {
        ValidateCollection(collection); await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false); await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = new SqlCommand($"UPDATE {Q("sk_collections")} SET knowledge_base_id=@kb,parent_collection_id=@parent,default_schema_id=@schema,external_id=@external,title=@title,description=@description WHERE id=@id; IF @@ROWCOUNT=0 INSERT INTO {Q("sk_collections")}(id,knowledge_base_id,parent_collection_id,default_schema_id,external_id,title,description) VALUES(@id,@kb,@parent,@schema,@external,@title,@description);", connection, transaction))
        {
            command.Parameters.AddWithValue("@id", collection.Id); command.Parameters.AddWithValue("@kb", collection.KnowledgeBaseId); command.Parameters.AddWithValue("@parent", (object?)collection.ParentCollectionId ?? DBNull.Value); command.Parameters.AddWithValue("@schema", (object?)collection.DefaultSchemaId ?? DBNull.Value); command.Parameters.AddWithValue("@external", (object?)collection.ExternalId ?? DBNull.Value); command.Parameters.AddWithValue("@title", collection.Title); command.Parameters.AddWithValue("@description", collection.Description); await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var delete = new SqlCommand($"DELETE FROM {Q("sk_collection_tags")} WHERE collection_id=@id", connection, transaction)) { delete.Parameters.AddWithValue("@id", collection.Id); await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
        foreach (var tag in collection.Tags) { await using var command = new SqlCommand($"INSERT INTO {Q("sk_collection_tags")}(collection_id,tag) VALUES(@id,@tag)", connection, transaction); command.Parameters.AddWithValue("@id", collection.Id); command.Parameters.AddWithValue("@tag", tag); await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<Guid> StreamDocumentIdsAsync(Guid knowledgeBaseId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false); await using var command = new SqlCommand($"SELECT id FROM {Q("sk_documents")} WHERE knowledge_base_id=@kb ORDER BY id", connection); command.Parameters.AddWithValue("@kb", knowledgeBaseId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) yield return reader.GetGuid(0);
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken) { var connection = new SqlConnection(options.ConnectionString); await connection.OpenAsync(cancellationToken).ConfigureAwait(false); return connection; }
    private string Q(string table) => $"{QI(options.Schema)}.{QI(table)}";
    private static string QI(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";
    private static void ValidateKnowledgeBase(KnowledgeBaseRecord value) { if (value.Id == Guid.Empty) throw new InvalidDataException("KnowledgeBase ID cannot be empty."); ArgumentException.ThrowIfNullOrWhiteSpace(value.Title); }
    private static void ValidateCollection(KnowledgeCollectionRecord value) { if (value.Id == Guid.Empty || value.KnowledgeBaseId == Guid.Empty) throw new InvalidDataException("Collection and KnowledgeBase IDs cannot be empty."); ArgumentException.ThrowIfNullOrWhiteSpace(value.Title); }
}
