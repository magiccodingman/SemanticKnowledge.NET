using Microsoft.Extensions.DependencyInjection;
using OnnxTextEmbeddings.SqlServer;

namespace SemanticKnowledge.SqlServer;

public sealed class SemanticKnowledgeSqlServerOptions
{
    public required string ConnectionString { get; set; }
    public string Schema { get; set; } = "SemanticKnowledge";
}

public static class SemanticKnowledgeSqlServerExtensions
{
    public static SemanticKnowledgeBuilder UseSqlServer(this SemanticKnowledgeBuilder builder, string connectionString)
        => builder.UseSqlServer(options => options.ConnectionString = connectionString);

    public static SemanticKnowledgeBuilder UseSqlServer(this SemanticKnowledgeBuilder builder, Action<SemanticKnowledgeSqlServerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder); ArgumentNullException.ThrowIfNull(configure);
        var options = new SemanticKnowledgeSqlServerOptions { ConnectionString = "Server=localhost" };
        configure(options);
        if (string.IsNullOrWhiteSpace(options.ConnectionString)) throw new InvalidOperationException("SQL Server ConnectionString is required.");
        if (string.IsNullOrWhiteSpace(options.Schema)) throw new InvalidOperationException("SQL Server Schema is required.");
        builder.Services.AddSingleton(options);
        builder.Services.AddOnnxTextEmbeddingsSqlServer();
        builder.Services.AddSingleton<IKnowledgeStorageProvider, SqlServerKnowledgeStorageProvider>();
        builder.Services.AddSingleton<IKnowledgeAdvancedSearchProvider, SqlServerAdvancedKnowledgeSearchProvider>();
        builder.Services.AddSingleton<IKnowledgeCollectionSnapshotProvider, SqlServerCollectionSnapshotProvider>();
        builder.Services.AddSingleton<SqlServerCollectionSnapshotResetter>();
        builder.Services.AddSingleton<IKnowledgeCollectionSnapshotResetter>(services => services.GetRequiredService<SqlServerCollectionSnapshotResetter>());
        builder.Services.AddSingleton<IKnowledgeCollectionSnapshotRecoveryProvider>(services => services.GetRequiredService<SqlServerCollectionSnapshotResetter>());
        builder.Services.AddSingleton<IKnowledgeLogicalVersionAccessor, SqlServerLogicalVersionAccessor>();
        builder.Services.AddSingleton<IKnowledgeArchiveStorage, SqlServerArchiveStorage>();
        return builder;
    }
}
