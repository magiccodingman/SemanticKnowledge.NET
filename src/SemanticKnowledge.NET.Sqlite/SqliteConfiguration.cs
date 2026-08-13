using Microsoft.Extensions.DependencyInjection;
using OnnxTextEmbeddings.SqliteVec;

namespace SemanticKnowledge.Sqlite;

public sealed class SemanticKnowledgeSqliteOptions
{
    public required string ConnectionString { get; set; }
    public bool ForeignKeys { get; set; } = true;
}

public static class SemanticKnowledgeSqliteExtensions
{
    public static SemanticKnowledgeBuilder UseSqlite(this SemanticKnowledgeBuilder builder, string databasePath)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        return builder.UseSqlite(options => options.ConnectionString = $"Data Source={databasePath}");
    }

    public static SemanticKnowledgeBuilder UseSqlite(this SemanticKnowledgeBuilder builder, Action<SemanticKnowledgeSqliteOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder); ArgumentNullException.ThrowIfNull(configure);
        var options = new SemanticKnowledgeSqliteOptions { ConnectionString = "Data Source=semantic-knowledge.db" };
        configure(options);
        if (string.IsNullOrWhiteSpace(options.ConnectionString)) throw new InvalidOperationException("SQLite ConnectionString is required.");
        builder.Services.AddSingleton(options);
        builder.Services.AddOnnxTextEmbeddingsSqliteVec();
        builder.Services.AddSingleton<IKnowledgeStorageProvider, SqliteKnowledgeStorageProvider>();
        builder.Services.AddSingleton<IKnowledgeAdvancedSearchProvider, SqliteAdvancedKnowledgeSearchProvider>();
        builder.Services.AddSingleton<IKnowledgeCollectionSnapshotProvider, SqliteCollectionSnapshotProvider>();
        builder.Services.AddSingleton<SqliteCollectionSnapshotResetter>();
        builder.Services.AddSingleton<IKnowledgeCollectionSnapshotResetter>(services => services.GetRequiredService<SqliteCollectionSnapshotResetter>());
        builder.Services.AddSingleton<IKnowledgeCollectionSnapshotRecoveryProvider>(services => services.GetRequiredService<SqliteCollectionSnapshotResetter>());
        builder.Services.AddSingleton<IKnowledgeLogicalVersionAccessor, SqliteLogicalVersionAccessor>();
        builder.Services.AddSingleton<IKnowledgeArchiveStorage, SqliteArchiveStorage>();
        return builder;
    }
}
