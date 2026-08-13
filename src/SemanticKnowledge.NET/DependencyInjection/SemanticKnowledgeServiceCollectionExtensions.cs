using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OnnxTextEmbeddings;

namespace SemanticKnowledge;

public sealed class SemanticKnowledgeBuilder(IServiceCollection services, SemanticKnowledgeOptions options)
{
    public IServiceCollection Services { get; } = services;
    public SemanticKnowledgeOptions Options { get; } = options;
}

public static class SemanticKnowledgeServiceCollectionExtensions
{
    public static SemanticKnowledgeBuilder AddSemanticKnowledge(this IServiceCollection services, Action<SemanticKnowledgeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new SemanticKnowledgeOptions();
        configure?.Invoke(options);
        options.Validate();
        services.AddSingleton(options);
        services.AddSingleton<SemanticKnowledgeStore>();
        services.AddSingleton<ISemanticKnowledgeStore, SnapshotAwareSemanticKnowledgeStore>();
        services.AddSingleton<IKnowledgeSynchronizationService, KnowledgeSynchronizationService>();
        services.AddSingleton<IKnowledgeCollectionSnapshotManager, KnowledgeCollectionSnapshotManager>();
        services.AddSingleton<IKnowledgeEmbeddingSpaceCatalog, KnowledgeEmbeddingSpaceCatalog>();
        services.AddSingleton<IKnowledgeContentSearch, KnowledgeContentSearch>();
        services.AddSingleton<IKnowledgeArchiveService, KnowledgeArchiveService>();
        services.AddSingleton<IKnowledgeCatalog, KnowledgeCatalog>();
        services.TryAddSingleton<ISemanticCandidateReranker, UpstreamCandidateRerankerAdapter>();
        return new SemanticKnowledgeBuilder(services, options);
    }

    public static SemanticKnowledgeBuilder UseOnnxEmbeddings(this SemanticKnowledgeBuilder builder, Action<OnnxTextEmbeddingsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddLogging();
        if (!builder.Services.Any(descriptor => descriptor.ServiceType == typeof(ITextEmbeddingService)))
            builder.Services.AddSingleton<ITextEmbeddingService>(_ => new OwnedOnnxTextEmbeddingService(configure));
        builder.Services.AddSingleton<IKnowledgeEmbeddingProvider, OnnxKnowledgeEmbeddingProvider>();
        return builder;
    }
}
