using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using OnnxTextEmbeddings;

namespace SemanticKnowledge.Http;

public sealed class SemanticKnowledgeHttpEmbeddingOptions
{
    public required Uri Endpoint { get; set; }
    public required string ModelId { get; set; }
    public required string SpaceId { get; set; }
    public required int Dimensions { get; set; }
    public string SourceRevision { get; set; } = "remote";
    public Uri? TokenCountEndpoint { get; set; }
    public string? BearerToken { get; set; }
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);
    public int MaxRetries { get; set; } = 3;
    public int ApproximateChunkCharacters { get; set; } = 4_000;

    internal void Validate()
    {
        if (Endpoint is null || !Endpoint.IsAbsoluteUri) throw new InvalidOperationException("A valid absolute embedding Endpoint is required.");
        if (string.IsNullOrWhiteSpace(ModelId) || string.IsNullOrWhiteSpace(SpaceId)) throw new InvalidOperationException("ModelId and SpaceId are required for embedding-space safety.");
        if (Dimensions <= 0) throw new InvalidOperationException("Dimensions must be greater than zero.");
        if (MaxRetries < 0 || ApproximateChunkCharacters <= 0) throw new InvalidOperationException("HTTP retry/chunk options are invalid.");
    }
}

public static class SemanticKnowledgeHttpExtensions
{
    public static SemanticKnowledgeBuilder UseHttpEmbeddings(this SemanticKnowledgeBuilder builder, Action<SemanticKnowledgeHttpEmbeddingOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder); ArgumentNullException.ThrowIfNull(configure);
        var options = new SemanticKnowledgeHttpEmbeddingOptions { Endpoint = new Uri("http://localhost"), ModelId = "unset", SpaceId = "unset", Dimensions = 1 };
        configure(options); options.Validate();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(new HttpClient { Timeout = options.Timeout });
        builder.Services.AddSingleton<IKnowledgeEmbeddingProvider, HttpKnowledgeEmbeddingProvider>();
        return builder;
    }
}

internal sealed class HttpKnowledgeEmbeddingProvider(HttpClient http, SemanticKnowledgeHttpEmbeddingOptions remote, SemanticKnowledgeOptions knowledgeOptions) : IKnowledgeEmbeddingProvider
{
    public Task<KnowledgeEmbeddingProviderInfo> GetInfoAsync(CancellationToken cancellationToken = default)
    {
        var output = ResolveOutputDimensions();
        var fingerprint = output == remote.Dimensions ? remote.SpaceId : DeriveReducedSpaceId(output);
        return Task.FromResult(new KnowledgeEmbeddingProviderInfo { Provider = "HTTP", ModelId = remote.ModelId, SourceRevision = remote.SourceRevision, EmbeddingSpaceFingerprint = fingerprint, NativeDimensions = remote.Dimensions, OutputDimensions = output, SupportsTokenCounting = remote.TokenCountEndpoint is not null, SupportsChunkedDocuments = true });
    }

    public async Task<QueryEmbedding> EmbedQueryAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var vector = await EmbedRawAsync(text, cancellationToken).ConfigureAwait(false);
        var count = await CountOrEstimateAsync(text, cancellationToken).ConfigureAwait(false);
        var query = new QueryEmbedding { Vector = vector, Identity = BaseIdentity(), SourceTokenCount = count, InputTokenCount = count };
        var output = ResolveOutputDimensions();
        return output == remote.Dimensions ? query : query.ReduceDimensions(output, EmbeddingVectorFormat.Float32);
    }

    public async Task<IReadOnlyList<TextEmbedding>> EmbedDocumentAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        var chunks = Chunk(text, remote.ApproximateChunkCharacters);
        var result = new List<TextEmbedding>(chunks.Count);
        var totalTokens = await CountOrEstimateAsync(text, cancellationToken).ConfigureAwait(false);
        for (var index = 0; index < chunks.Count; index++)
        {
            var chunk = chunks[index];
            var tokenCount = await CountOrEstimateAsync(chunk.Text, cancellationToken).ConfigureAwait(false);
            var embedding = new TextEmbedding
            {
                Vector = await EmbedRawAsync(chunk.Text, cancellationToken).ConfigureAwait(false),
                Identity = BaseIdentity(),
                Source = new EmbeddingSource { DocumentTokenCount = totalTokens, CharacterRange = new Utf16TextRange(chunk.Start, chunk.Text.Length), TokenRange = new TokenRange(0, tokenCount), TokenCount = tokenCount, TokenCapacity = tokenCount },
                Chunk = new EmbeddingChunkInfo { Index = index, Count = chunks.Count, BoundaryKind = chunks.Count == 1 ? ChunkBoundaryKind.WholeDocument : ChunkBoundaryKind.ParagraphGroup, InputTokenCount = tokenCount },
                Text = chunk.Text
            };
            var output = ResolveOutputDimensions();
            if (output != remote.Dimensions) embedding = embedding.ReduceDimensions(output, EmbeddingVectorFormat.Float32);
            embedding = embedding with { Vector = embedding.Vector.ConvertTo(knowledgeOptions.Embeddings.PersistedRecordFormat) };
            result.Add(embedding);
        }
        return result;
    }

    public async Task<int?> TryCountTokensAsync(string text, CancellationToken cancellationToken = default)
    {
        if (remote.TokenCountEndpoint is null) return null;
        return await CountTokensAsync(text, cancellationToken).ConfigureAwait(false);
    }

    private async Task<EmbeddingVector> EmbedRawAsync(string text, CancellationToken cancellationToken)
    {
        using var response = await SendWithRetryAsync(remote.Endpoint, new HttpEmbeddingRequest(remote.ModelId, text), cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadFromJsonAsync(HttpEmbeddingJsonContext.Default.HttpEmbeddingResponse, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Embedding API returned an empty response.");
        var values = payload.Embedding ?? payload.Data?.FirstOrDefault()?.Embedding ?? throw new InvalidOperationException("Embedding API response did not contain an embedding vector.");
        if (values.Length != remote.Dimensions) throw new InvalidOperationException($"Embedding API returned {values.Length} dimensions; configured dimensions are {remote.Dimensions}.");
        return EmbeddingVector.FromFloat32(values, EmbeddingVectorFormat.Float32);
    }

    private async Task<int> CountOrEstimateAsync(string text, CancellationToken cancellationToken) => remote.TokenCountEndpoint is null ? Math.Max(1, (text.Length + 3) / 4) : await CountTokensAsync(text, cancellationToken).ConfigureAwait(false);

    private async Task<int> CountTokensAsync(string text, CancellationToken cancellationToken)
    {
        using var response = await SendWithRetryAsync(remote.TokenCountEndpoint!, new HttpTokenRequest(remote.ModelId, text), cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadFromJsonAsync(HttpEmbeddingJsonContext.Default.HttpTokenResponse, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Tokenizer API returned an empty response.");
        if (payload.Tokens < 0) throw new InvalidOperationException("Tokenizer API returned a negative token count.");
        return payload.Tokens;
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(Uri uri, object payload, CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var attempt = 0; attempt <= remote.MaxRetries; attempt++)
        {
            try
            {
                HttpContent content = payload switch
                {
                    HttpEmbeddingRequest embedding => JsonContent.Create(embedding, HttpEmbeddingJsonContext.Default.HttpEmbeddingRequest),
                    HttpTokenRequest token => JsonContent.Create(token, HttpEmbeddingJsonContext.Default.HttpTokenRequest),
                    _ => throw new NotSupportedException($"Unsupported HTTP payload type {payload.GetType().Name}.")
                };
                using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = content };
                if (!string.IsNullOrWhiteSpace(remote.BearerToken)) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", remote.BearerToken);
                var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return response;
                if ((int)response.StatusCode < 500 && response.StatusCode != System.Net.HttpStatusCode.TooManyRequests) response.EnsureSuccessStatusCode();
                last = new HttpRequestException($"Embedding API returned {(int)response.StatusCode} {response.ReasonPhrase}."); response.Dispose();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested) { last = ex; }
            if (attempt < remote.MaxRetries) await Task.Delay(TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt)), cancellationToken).ConfigureAwait(false);
        }
        throw new HttpRequestException("Embedding API request failed after retries.", last);
    }

    private EmbeddingIdentity BaseIdentity() => new() { ModelId = remote.ModelId, SourceRevision = remote.SourceRevision, EmbeddingSpaceFingerprint = remote.SpaceId, IsNormalized = false };
    private int ResolveOutputDimensions() { var output = knowledgeOptions.Embeddings.OutputDimensions ?? remote.Dimensions; if (output > remote.Dimensions) throw new InvalidOperationException("SemanticKnowledge cannot expand embedding dimensionality."); return output; }

    private string DeriveReducedSpaceId(int output)
    {
        var values = new float[remote.Dimensions];
        if (values.Length > 0) values[0] = 1f;
        var query = new QueryEmbedding { Vector = EmbeddingVector.FromFloat32(values), Identity = BaseIdentity(), SourceTokenCount = 0, InputTokenCount = 0 };
        return query.ReduceDimensions(output, EmbeddingVectorFormat.Float32).Identity.EmbeddingSpaceFingerprint;
    }

    private static List<TextChunk> Chunk(string text, int maxCharacters)
    {
        if (text.Length <= maxCharacters) return [new TextChunk(0, text)];
        var result = new List<TextChunk>(); var start = 0;
        while (start < text.Length)
        {
            var length = Math.Min(maxCharacters, text.Length - start); var end = start + length;
            if (end < text.Length)
            {
                var paragraph = text.LastIndexOf("\n\n", end - 1, length, StringComparison.Ordinal);
                if (paragraph > start + maxCharacters / 2) end = paragraph + 2;
            }
            result.Add(new TextChunk(start, text[start..end])); start = end;
        }
        return result;
    }

    private sealed record TextChunk(int Start, string Text);
}

public sealed record HttpEmbeddingRequest(string Model, string Input);
public sealed record HttpEmbeddingDatum(float[] Embedding);
public sealed record HttpEmbeddingResponse(float[]? Embedding, HttpEmbeddingDatum[]? Data);
public sealed record HttpTokenRequest(string Model, string Input);
public sealed record HttpTokenResponse(int Tokens);

[JsonSerializable(typeof(HttpEmbeddingRequest))]
[JsonSerializable(typeof(HttpEmbeddingResponse))]
[JsonSerializable(typeof(HttpTokenRequest))]
[JsonSerializable(typeof(HttpTokenResponse))]
internal partial class HttpEmbeddingJsonContext : JsonSerializerContext;
