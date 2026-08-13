using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OnnxTextEmbeddings.PgVector;

namespace SemanticKnowledge.PostgreSql;

public sealed class SemanticKnowledgePostgreSqlOptions
{
    public required string ConnectionString { get; set; }
    public string Schema { get; set; } = "semantic_knowledge";
}

public static class SemanticKnowledgePostgreSqlExtensions
{
    public static SemanticKnowledgeBuilder UsePostgreSql(this SemanticKnowledgeBuilder builder, string connectionString)
        => builder.UsePostgreSql(options => options.ConnectionString = connectionString);

    public static SemanticKnowledgeBuilder UsePostgreSql(this SemanticKnowledgeBuilder builder, Action<SemanticKnowledgePostgreSqlOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder); ArgumentNullException.ThrowIfNull(configure);
        var options = new SemanticKnowledgePostgreSqlOptions { ConnectionString = "Host=localhost" };
        configure(options);
        if (string.IsNullOrWhiteSpace(options.ConnectionString)) throw new InvalidOperationException("PostgreSQL ConnectionString is required.");
        if (string.IsNullOrWhiteSpace(options.Schema)) throw new InvalidOperationException("PostgreSQL Schema is required.");
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(options.ConnectionString);
        dataSourceBuilder.UseVector();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(dataSourceBuilder.Build());
        builder.Services.AddOnnxTextEmbeddingsPgVector();
        builder.Services.AddSingleton<IKnowledgeStorageProvider, PostgreSqlKnowledgeStorageProvider>();
        builder.Services.AddSingleton<IKnowledgeAdvancedSearchProvider, PostgreSqlAdvancedKnowledgeSearchProvider>();
        builder.Services.AddSingleton<IKnowledgeCollectionSnapshotProvider, PostgreSqlCollectionSnapshotProvider>();
        builder.Services.AddSingleton<PostgreSqlCollectionSnapshotResetter>();
        builder.Services.AddSingleton<IKnowledgeCollectionSnapshotResetter>(services => services.GetRequiredService<PostgreSqlCollectionSnapshotResetter>());
        builder.Services.AddSingleton<IKnowledgeCollectionSnapshotRecoveryProvider>(services => services.GetRequiredService<PostgreSqlCollectionSnapshotResetter>());
        builder.Services.AddSingleton<IKnowledgeLogicalVersionAccessor, PostgreSqlLogicalVersionAccessor>();
        builder.Services.AddSingleton<IKnowledgeArchiveStorage, PostgreSqlArchiveStorage>();
        return builder;
    }
}
