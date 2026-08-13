namespace SemanticKnowledge;

/// <summary>
/// Administrative/recovery surface for durable Collection snapshot staging. Normal synchronization failures
/// abort themselves; this service is for staging left behind by a terminated process or abandoned job.
/// </summary>
public interface IKnowledgeCollectionSnapshotManager
{
    Task<KnowledgeCollectionSnapshotState?> GetStateAsync(Guid collectionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Discards only unpublished staging. The active published Collection/revision is never changed.
    /// Pass the staging SnapshotId previously observed from GetStateAsync to protect against discarding
    /// a newer concurrent staging operation.
    /// </summary>
    Task DiscardStagedAsync(Guid collectionId, Guid? expectedSnapshotId = null, CancellationToken cancellationToken = default);
}

internal sealed class KnowledgeCollectionSnapshotManager(
    ISemanticKnowledgeStore store,
    IEnumerable<IKnowledgeCollectionSnapshotProvider> snapshotProviders,
    IEnumerable<IKnowledgeCollectionSnapshotRecoveryProvider> recoveryProviders) : IKnowledgeCollectionSnapshotManager
{
    private readonly IKnowledgeCollectionSnapshotProvider? _snapshotProvider = snapshotProviders.SingleOrDefault();
    private readonly IKnowledgeCollectionSnapshotRecoveryProvider? _recoveryProvider = recoveryProviders.SingleOrDefault();

    public async Task<KnowledgeCollectionSnapshotState?> GetStateAsync(Guid collectionId, CancellationToken cancellationToken = default)
    {
        if (collectionId == Guid.Empty) throw new ArgumentException("CollectionId cannot be empty.", nameof(collectionId));
        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var provider = _snapshotProvider ?? throw new NotSupportedException("The configured SemanticKnowledge storage provider does not support atomic Collection snapshots.");
        return await provider.GetCollectionSnapshotStateAsync(collectionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task DiscardStagedAsync(Guid collectionId, Guid? expectedSnapshotId = null, CancellationToken cancellationToken = default)
    {
        if (collectionId == Guid.Empty) throw new ArgumentException("CollectionId cannot be empty.", nameof(collectionId));
        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var recovery = _recoveryProvider ?? throw new NotSupportedException("The configured SemanticKnowledge storage provider does not support Collection snapshot recovery.");
        await recovery.DiscardStagedCollectionSnapshotAsync(collectionId, expectedSnapshotId, cancellationToken).ConfigureAwait(false);
    }
}
