using Microsoft.Extensions.DependencyInjection;
using OnnxTextEmbeddings.PgVector;
using Pgvector;

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
        var dataSourceBuilder = new Npgsql.NpgsqlDataSourceBuilder(options.ConnectionString);
        dataSourceBuilder.UseVector();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(dataSourceBuilder.Build());
        builder.Services.AddOnnxTextEmbeddingsPgVector();
        builder.Services.AddSingleton<IKnowledgeStorageProvider, PostgreSqlKnowledgeStorageProvider>();
        return builder;
    }
}
