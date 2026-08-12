using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using OnnxTextEmbeddings.SqliteVec;

namespace SemanticKnowledge.Sqlite;

internal sealed class SqliteArchiveStorage(SemanticKnowledgeSqliteOptions options) : IKnowledgeArchiveStorage
{
    public async Task<KnowledgeBaseRecord?> GetKnowledgeBaseAsync(Guid knowledgeBaseId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        string? externalId;
        string title;
        string description;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT external_id,title,description FROM sk_knowledge_bases WHERE id=$id";
            command.Parameters.AddWithValue("$id", knowledgeBaseId.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
            externalId = reader.IsDBNull(0) ? null : reader.GetString(0);
            title = reader.GetString(1);
            description = reader.GetString(2);
        }
        var tags = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT tag FROM sk_knowledge_base_tags WHERE knowledge_base_id=$id ORDER BY tag";
            command.Parameters.AddWithValue("$id", knowledgeBaseId.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) tags.Add(reader.GetString(0));
        }
        return new KnowledgeBaseRecord { Id = knowledgeBaseId, ExternalId = externalId, Title = title, Description = description, Tags = tags };
    }

    public async Task UpsertKnowledgeBaseAsync(KnowledgeBaseRecord knowledgeBase, CancellationToken cancellationToken = default)
    {
        ValidateKnowledgeBase(knowledgeBase);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO sk_knowledge_bases(id,external_id,title,description) VALUES($id,$external,$title,$description) ON CONFLICT(id) DO UPDATE SET external_id=excluded.external_id,title=excluded.title,description=excluded.description";
            command.Parameters.AddWithValue("$id", knowledgeBase.Id.ToString("D"));
            command.Parameters.AddWithValue("$external", (object?)knowledgeBase.ExternalId ?? DBNull.Value);
            command.Parameters.AddWithValue("$title", knowledgeBase.Title);
            command.Parameters.AddWithValue("$description", knowledgeBase.Description);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var delete = connection.CreateCommand()) { delete.Transaction = transaction; delete.CommandText = "DELETE FROM sk_knowledge_base_tags WHERE knowledge_base_id=$id"; delete.Parameters.AddWithValue("$id", knowledgeBase.Id.ToString("D")); await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
        foreach (var tag in NormalizeTags(knowledgeBase.Tags))
        {
            await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "INSERT INTO sk_knowledge_base_tags(knowledge_base_id,tag) VALUES($id,$tag)"; command.Parameters.AddWithValue("$id", knowledgeBase.Id.ToString("D")); command.Parameters.AddWithValue("$tag", tag); await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpsertCollectionAsync(KnowledgeCollectionRecord collection, CancellationToken cancellationToken = default)
    {
        ValidateCollection(collection);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO sk_collections(id,knowledge_base_id,parent_collection_id,default_schema_id,external_id,title,description) VALUES($id,$kb,$parent,$schema,$external,$title,$description) ON CONFLICT(id) DO UPDATE SET knowledge_base_id=excluded.knowledge_base_id,parent_collection_id=excluded.parent_collection_id,default_schema_id=excluded.default_schema_id,external_id=excluded.external_id,title=excluded.title,description=excluded.description";
            command.Parameters.AddWithValue("$id", collection.Id.ToString("D"));
            command.Parameters.AddWithValue("$kb", collection.KnowledgeBaseId.ToString("D"));
            command.Parameters.AddWithValue("$parent", collection.ParentCollectionId is { } parent ? parent.ToString("D") : DBNull.Value);
            command.Parameters.AddWithValue("$schema", collection.DefaultSchemaId is { } schema ? schema.ToString("D") : DBNull.Value);
            command.Parameters.AddWithValue("$external", (object?)collection.ExternalId ?? DBNull.Value);
            command.Parameters.AddWithValue("$title", collection.Title);
            command.Parameters.AddWithValue("$description", collection.Description);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var delete = connection.CreateCommand()) { delete.Transaction = transaction; delete.CommandText = "DELETE FROM sk_collection_tags WHERE collection_id=$id"; delete.Parameters.AddWithValue("$id", collection.Id.ToString("D")); await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
        foreach (var tag in collection.Tags)
        {
            await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "INSERT INTO sk_collection_tags(collection_id,tag) VALUES($id,$tag)"; command.Parameters.AddWithValue("$id", collection.Id.ToString("D")); command.Parameters.AddWithValue("$tag", tag); await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<Guid> StreamDocumentIdsAsync(Guid knowledgeBaseId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT id FROM sk_documents WHERE knowledge_base_id=$kb ORDER BY id"; command.Parameters.AddWithValue("$kb", knowledgeBaseId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) yield return Guid.Parse(reader.GetString(0));
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(options.ConnectionString);
        connection.LoadOnnxTextEmbeddingsSqliteVec();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (options.ForeignKeys) { await using var command = connection.CreateCommand(); command.CommandText = "PRAGMA foreign_keys=ON"; await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE IF NOT EXISTS sk_knowledge_base_tags(knowledge_base_id TEXT NOT NULL,tag TEXT NOT NULL,PRIMARY KEY(knowledge_base_id,tag)); DELETE FROM sk_knowledge_base_tags WHERE knowledge_base_id NOT IN (SELECT id FROM sk_knowledge_bases);";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        return connection;
    }

    private static IReadOnlyList<string> NormalizeTags(IEnumerable<string> tags) => tags.Where(tag => !string.IsNullOrWhiteSpace(tag)).Select(tag => tag.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private static void ValidateKnowledgeBase(KnowledgeBaseRecord value) { if (value.Id == Guid.Empty) throw new InvalidDataException("KnowledgeBase ID cannot be empty."); ArgumentException.ThrowIfNullOrWhiteSpace(value.Title); }
    private static void ValidateCollection(KnowledgeCollectionRecord value) { if (value.Id == Guid.Empty || value.KnowledgeBaseId == Guid.Empty) throw new InvalidDataException("Collection and KnowledgeBase IDs cannot be empty."); ArgumentException.ThrowIfNullOrWhiteSpace(value.Title); }
}
