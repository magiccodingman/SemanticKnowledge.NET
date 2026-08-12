using OnnxTextEmbeddings;

namespace SemanticKnowledge;

public enum KnowledgePersistenceMode { Authoritative = 1, Rebuildable = 2 }
public enum VectorStoragePreference { Compact = 1, MaximumPrecision = 2, ProviderDefault = 3 }

public sealed class SemanticKnowledgeOptions
{
    public int DatabaseVersion { get; set; } = 1;
    public KnowledgePersistenceMode PersistenceMode { get; set; } = KnowledgePersistenceMode.Rebuildable;
    public SemanticKnowledgeEmbeddingOptions Embeddings { get; } = new();
    public SemanticKnowledgeIngestionOptions Ingestion { get; } = new();
    public SemanticKnowledgeSearchDefaults Search { get; } = new();

    public void Validate()
    {
        if (DatabaseVersion <= 0) throw new InvalidOperationException("DatabaseVersion must be greater than zero.");
        Embeddings.Validate(); Ingestion.Validate(); Search.Validate();
    }
}

public sealed class SemanticKnowledgeEmbeddingOptions
{
    /// <summary>Null means use the embedding provider's native dimensions.</summary>
    public int? OutputDimensions { get; set; }
    public VectorStoragePreference Storage { get; set; } = VectorStoragePreference.Compact;
    /// <summary>Portable direct TextEmbedding records are compact by default after any dimensional reduction.</summary>
    public EmbeddingVectorFormat PersistedRecordFormat { get; set; } = EmbeddingVectorFormat.Int8;

    internal void Validate()
    {
        if (OutputDimensions is <= 0) throw new InvalidOperationException("Embeddings.OutputDimensions must be greater than zero when specified.");
        if (PersistedRecordFormat == EmbeddingVectorFormat.Unspecified) throw new InvalidOperationException("Embeddings.PersistedRecordFormat cannot be Unspecified.");
    }
}

public sealed class SemanticKnowledgeIngestionOptions
{
    public int MaxInFlightDocuments { get; set; } = 8;
    public long MaxInFlightSourceBytes { get; set; } = 32L * 1024 * 1024;
    internal void Validate()
    {
        if (MaxInFlightDocuments <= 0) throw new InvalidOperationException("Ingestion.MaxInFlightDocuments must be greater than zero.");
        if (MaxInFlightSourceBytes <= 0) throw new InvalidOperationException("Ingestion.MaxInFlightSourceBytes must be greater than zero.");
    }
}

public sealed class SemanticKnowledgeSearchDefaults
{
    public int Top { get; set; } = 10;
    public int CandidateMultiplier { get; set; } = 10;
    public int MinimumCandidateCount { get; set; } = 100;
    internal void Validate()
    {
        if (Top <= 0 || CandidateMultiplier <= 0 || MinimumCandidateCount <= 0) throw new InvalidOperationException("Search defaults must be greater than zero.");
    }
}
