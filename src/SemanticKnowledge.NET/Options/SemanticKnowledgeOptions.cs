using OnnxTextEmbeddings;

namespace SemanticKnowledge;

public enum KnowledgePersistenceMode { Authoritative = 1, Rebuildable = 2 }
public enum VectorStoragePreference { Compact = 1, MaximumPrecision = 2, ProviderDefault = 3 }

public sealed class SemanticKnowledgeOptions
{
    public int DatabaseVersion { get; set; } = 1;
    public KnowledgePersistenceMode PersistenceMode { get; set; } = KnowledgePersistenceMode.Rebuildable;
    public SemanticKnowledgeEmbeddingOptions Embeddings { get; } = new();

    public void Validate()
    {
        if (DatabaseVersion <= 0) throw new InvalidOperationException("DatabaseVersion must be greater than zero.");
        Embeddings.Validate();
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
