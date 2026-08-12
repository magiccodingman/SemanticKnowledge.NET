using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using SemanticKnowledge.Sqlite;

namespace SemanticKnowledge.Native;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct SkBuffer
{
    public byte* Data;
    public nuint Length;
}

public static unsafe class NativeExports
{
    public const uint AbiVersion = 1;
    private static long _nextHandle;
    private static readonly ConcurrentDictionary<nint, NativeStoreState> Stores = new();
    [ThreadStatic] private static string? _lastError;

    [UnmanagedCallersOnly(EntryPoint = "sk_abi_version", CallConvs = [typeof(CallConvCdecl)])]
    public static uint AbiVersionExport() => AbiVersion;

    [UnmanagedCallersOnly(EntryPoint = "sk_version", CallConvs = [typeof(CallConvCdecl)])]
    public static int Version(byte* buffer, nuint capacity) => WriteUtf8("0.1", buffer, capacity);

    [UnmanagedCallersOnly(EntryPoint = "sk_get_last_error", CallConvs = [typeof(CallConvCdecl)])]
    public static int GetLastError(byte* buffer, nuint capacity) => WriteUtf8(_lastError ?? string.Empty, buffer, capacity);

    [UnmanagedCallersOnly(EntryPoint = "sk_buffer_free", CallConvs = [typeof(CallConvCdecl)])]
    public static void BufferFree(SkBuffer* buffer)
    {
        if (buffer is null) return;
        if (buffer->Data is not null) NativeMemory.Free(buffer->Data);
        buffer->Data = null;
        buffer->Length = 0;
    }

    [UnmanagedCallersOnly(EntryPoint = "sk_store_open_sqlite_json", CallConvs = [typeof(CallConvCdecl)])]
    public static int StoreOpenSqlite(byte* json, nuint length, nint* handle)
    {
        if (handle is null) return Fail("The output store handle pointer is null.");
        *handle = 0;
        try
        {
            var request = Deserialize(json, length, NativeJsonContext.Default.NativeSqliteStoreOptions);
            if (string.IsNullOrWhiteSpace(request.DatabasePath)) return Fail("databasePath is required.");

            var services = new ServiceCollection();
            var builder = services.AddSemanticKnowledge(options =>
            {
                options.DatabaseVersion = request.DatabaseVersion <= 0 ? 1 : request.DatabaseVersion;
                options.PersistenceMode = request.Rebuildable ? KnowledgePersistenceMode.Rebuildable : KnowledgePersistenceMode.Authoritative;
                options.Embeddings.OutputDimensions = request.OutputDimensions;
                options.Embeddings.Storage = request.MaximumPrecision ? VectorStoragePreference.MaximumPrecision : VectorStoragePreference.Compact;
            });
            builder.UseSqlite(request.DatabasePath).UseOnnxEmbeddings();

            var provider = services.BuildServiceProvider();
            var state = new NativeStoreState(provider, provider.GetRequiredService<ISemanticKnowledgeStore>());
            var id = (nint)Interlocked.Increment(ref _nextHandle);
            if (!Stores.TryAdd(id, state))
            {
                provider.Dispose();
                return Fail("Unable to allocate a native store handle.");
            }
            *handle = id;
            ClearError();
            return 0;
        }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "sk_store_close", CallConvs = [typeof(CallConvCdecl)])]
    public static int StoreClose(nint handle)
    {
        try
        {
            if (!Stores.TryRemove(handle, out var state)) return Fail("Unknown SemanticKnowledge store handle.");
            state.Provider.Dispose();
            ClearError();
            return 0;
        }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "sk_store_initialize_json", CallConvs = [typeof(CallConvCdecl)])]
    public static int StoreInitialize(nint handle, SkBuffer* output)
        => ExecuteJson(handle, output, state =>
        {
            var capabilities = state.Store.InitializeAsync().GetAwaiter().GetResult();
            return new NativeCapabilities(capabilities.Provider, capabilities.MaxDimensions, capabilities.PhysicalVectorStorage, capabilities.ExactVectorSearch, capabilities.ApproximateVectorSearch, capabilities.NativeAotSupported);
        }, NativeJsonContext.Default.NativeCapabilities);

    [UnmanagedCallersOnly(EntryPoint = "sk_knowledge_base_get_or_create_json", CallConvs = [typeof(CallConvCdecl)])]
    public static int KnowledgeBaseGetOrCreate(nint handle, byte* json, nuint length, SkBuffer* output)
        => ExecuteJson(handle, output, state =>
        {
            var request = Deserialize(json, length, NativeJsonContext.Default.NativeNamedResourceRequest);
            var record = state.Store.GetOrCreateKnowledgeBaseAsync(request.Title, request.ExternalId).GetAwaiter().GetResult();
            return NativeKnowledgeBase.From(record);
        }, NativeJsonContext.Default.NativeKnowledgeBase);

    [UnmanagedCallersOnly(EntryPoint = "sk_collection_get_or_create_json", CallConvs = [typeof(CallConvCdecl)])]
    public static int CollectionGetOrCreate(nint handle, byte* json, nuint length, SkBuffer* output)
        => ExecuteJson(handle, output, state =>
        {
            var request = Deserialize(json, length, NativeJsonContext.Default.NativeCollectionRequest);
            var record = state.Store.GetOrCreateCollectionAsync(
                ParseGuid(request.KnowledgeBaseId, "knowledgeBaseId"),
                request.Title,
                ParseNullableGuid(request.ParentCollectionId, "parentCollectionId"),
                ParseNullableGuid(request.DefaultSchemaId, "defaultSchemaId"),
                request.ExternalId).GetAwaiter().GetResult();
            return NativeCollection.From(record);
        }, NativeJsonContext.Default.NativeCollection);

    [UnmanagedCallersOnly(EntryPoint = "sk_schema_ensure_json", CallConvs = [typeof(CallConvCdecl)])]
    public static int SchemaEnsure(nint handle, byte* json, nuint length, SkBuffer* output)
        => ExecuteJson(handle, output, state =>
        {
            var request = Deserialize(json, length, NativeJsonContext.Default.NativeSchemaRequest);
            var schema = BuildSchema(request);
            state.Store.EnsureSchemaAsync(schema).GetAwaiter().GetResult();
            return NativeSchema.From(schema);
        }, NativeJsonContext.Default.NativeSchema);

    [UnmanagedCallersOnly(EntryPoint = "sk_document_upsert_json", CallConvs = [typeof(CallConvCdecl)])]
    public static int DocumentUpsert(nint handle, byte* json, nuint length, SkBuffer* output)
        => ExecuteJson(handle, output, state =>
        {
            var request = Deserialize(json, length, NativeJsonContext.Default.NativeDocumentRequest);
            var values = new Dictionary<string, KnowledgeValue>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in request.Values ?? []) values[value.Key] = value.ToKnowledgeValue();
            var id = state.Store.UpsertDocumentAsync(new KnowledgeDocumentInput
            {
                Id = ParseNullableGuid(request.Id, "id"),
                ExternalId = request.ExternalId,
                KnowledgeBaseId = ParseGuid(request.KnowledgeBaseId, "knowledgeBaseId"),
                CollectionId = ParseGuid(request.CollectionId, "collectionId"),
                SchemaId = ParseGuid(request.SchemaId, "schemaId"),
                Title = request.Title,
                Description = request.Description ?? string.Empty,
                Tags = request.Tags ?? [],
                Values = values
            }).GetAwaiter().GetResult();
            return new NativeIdResult(id.ToString("D"));
        }, NativeJsonContext.Default.NativeIdResult);

    [UnmanagedCallersOnly(EntryPoint = "sk_document_get_json", CallConvs = [typeof(CallConvCdecl)])]
    public static int DocumentGet(nint handle, byte* json, nuint length, SkBuffer* output)
        => ExecuteJson(handle, output, state =>
        {
            var request = Deserialize(json, length, NativeJsonContext.Default.NativeIdRequest);
            var document = state.Store.GetDocumentAsync(ParseGuid(request.Id, "id")).GetAwaiter().GetResult();
            return document is null ? null : NativeDocument.From(document);
        }, NativeJsonContext.Default.NativeDocument);

    [UnmanagedCallersOnly(EntryPoint = "sk_search_json", CallConvs = [typeof(CallConvCdecl)])]
    public static int Search(nint handle, byte* json, nuint length, SkBuffer* output)
        => ExecuteJson(handle, output, state =>
        {
            var request = Deserialize(json, length, NativeJsonContext.Default.NativeSearchRequest);
            var filter = request.Filter is null ? null : request.Filter.ToKnowledgeFilter();
            var hits = state.Store.SearchAsync(request.Query, new KnowledgeSearchRequest
            {
                KnowledgeBaseId = ParseGuid(request.KnowledgeBaseId, "knowledgeBaseId"),
                Mode = request.Mode,
                CollectionIds = (request.CollectionIds ?? []).Select(id => ParseGuid(id, "collectionIds")).ToArray(),
                IncludeDescendants = request.IncludeDescendants,
                Filter = filter,
                Top = request.Top <= 0 ? 10 : request.Top,
                CandidateCount = request.CandidateCount,
                Include = request.Include
            }).GetAwaiter().GetResult();
            return new NativeSearchResponse(hits.Select(NativeSearchHit.From).ToArray());
        }, NativeJsonContext.Default.NativeSearchResponse);

    [UnmanagedCallersOnly(EntryPoint = "sk_store_reset", CallConvs = [typeof(CallConvCdecl)])]
    public static int StoreReset(nint handle)
    {
        try
        {
            var state = GetStore(handle);
            state.Store.ResetAsync().GetAwaiter().GetResult();
            ClearError();
            return 0;
        }
        catch (Exception ex) { return Fail(ex); }
    }

    private static int ExecuteJson<T>(nint handle, SkBuffer* output, Func<NativeStoreState, T> operation, JsonTypeInfo<T> typeInfo)
    {
        if (output is null) return Fail("The output buffer pointer is null.");
        output->Data = null;
        output->Length = 0;
        try
        {
            var result = operation(GetStore(handle));
            var bytes = JsonSerializer.SerializeToUtf8Bytes(result, typeInfo);
            output->Data = (byte*)NativeMemory.Alloc((nuint)bytes.Length);
            if (output->Data is null) return Fail("Unable to allocate the native output buffer.");
            bytes.CopyTo(new Span<byte>(output->Data, bytes.Length));
            output->Length = (nuint)bytes.Length;
            ClearError();
            return 0;
        }
        catch (Exception ex) { return Fail(ex); }
    }

    private static T Deserialize<T>(byte* data, nuint length, JsonTypeInfo<T> typeInfo)
    {
        if (data is null && length != 0) throw new ArgumentException("JSON pointer is null while length is non-zero.");
        var span = new ReadOnlySpan<byte>(data, checked((int)length));
        return JsonSerializer.Deserialize(span, typeInfo) ?? throw new InvalidOperationException("JSON request deserialized to null.");
    }

    private static NativeStoreState GetStore(nint handle) => Stores.TryGetValue(handle, out var state) ? state : throw new InvalidOperationException("Unknown SemanticKnowledge store handle.");

    private static KnowledgeSchemaDefinition BuildSchema(NativeSchemaRequest request)
    {
        var builder = new KnowledgeSchemaBuilder(request.Key, request.DisplayName, ParseNullableGuid(request.Id, "id"));
        foreach (var field in request.Fields ?? [])
        {
            var key = NormalizeFieldKey(field.Key);
            if (key is KnowledgeSystemFields.Title or KnowledgeSystemFields.Description or KnowledgeSystemFields.Tags)
            {
                builder.SetSemanticWeight(key, field.SemanticWeightPercent);
                continue;
            }
            switch (field.Type)
            {
                case KnowledgeFieldType.Text: builder.Text(key, field.SemanticWeightPercent, field.SemanticMode, field.Required, field.Filterable, field.DisplayName); break;
                case KnowledgeFieldType.Int64: builder.Int64(key, field.Required, field.Filterable, field.DisplayName); break;
                case KnowledgeFieldType.Decimal: builder.Decimal(key, field.Required, field.Filterable, field.DisplayName); break;
                case KnowledgeFieldType.Boolean: builder.Boolean(key, field.Required, field.Filterable, field.DisplayName); break;
                case KnowledgeFieldType.DateTimeOffset: builder.DateTimeOffset(key, field.Required, field.Filterable, field.DisplayName); break;
                case KnowledgeFieldType.Guid: builder.GuidField(key, field.Required, field.Filterable, field.DisplayName); break;
                default: throw new NotSupportedException($"Unsupported schema field type {field.Type}.");
            }
        }
        return builder.Build(request.Revision <= 0 ? 1 : request.Revision);
    }

    private static string NormalizeFieldKey(string key) => string.IsNullOrWhiteSpace(key)
        ? throw new ArgumentException("Field key is required.", nameof(key))
        : key.Trim().ToLowerInvariant();

    private static Guid ParseGuid(string value, string name) => Guid.TryParse(value, out var parsed) && parsed != Guid.Empty ? parsed : throw new ArgumentException($"{name} must be a non-empty GUID.");
    private static Guid? ParseNullableGuid(string? value, string name) => string.IsNullOrWhiteSpace(value) ? null : ParseGuid(value, name);

    private static int WriteUtf8(string value, byte* buffer, nuint capacity)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        var required = checked(byteCount + 1);
        if (buffer is null || capacity < (nuint)required) return required;
        var destination = new Span<byte>(buffer, byteCount);
        Encoding.UTF8.GetBytes(value, destination);
        buffer[byteCount] = 0;
        return byteCount;
    }

    private static int Fail(Exception exception) => Fail(exception.Message);
    private static int Fail(string message) { _lastError = message; return 1; }
    private static void ClearError() => _lastError = null;

    private sealed record NativeStoreState(ServiceProvider Provider, ISemanticKnowledgeStore Store);
}

public sealed record NativeSqliteStoreOptions(string DatabasePath, int DatabaseVersion = 1, bool Rebuildable = true, int? OutputDimensions = null, bool MaximumPrecision = false);
public sealed record NativeCapabilities(string Provider, int MaxDimensions, string PhysicalVectorStorage, bool ExactVectorSearch, bool ApproximateVectorSearch, bool NativeAotSupported);
public sealed record NativeNamedResourceRequest(string Title, string? ExternalId = null);
public sealed record NativeKnowledgeBase(string Id, string? ExternalId, string Title, string Description, string[] Tags)
{
    public static NativeKnowledgeBase From(KnowledgeBaseRecord value) => new(value.Id.ToString("D"), value.ExternalId, value.Title, value.Description, value.Tags.ToArray());
}
public sealed record NativeCollectionRequest(string KnowledgeBaseId, string Title, string? ParentCollectionId = null, string? DefaultSchemaId = null, string? ExternalId = null);
public sealed record NativeCollection(string Id, string KnowledgeBaseId, string? ParentCollectionId, string? DefaultSchemaId, string? ExternalId, string Title, string Description, string[] Tags)
{
    public static NativeCollection From(KnowledgeCollectionRecord value) => new(value.Id.ToString("D"), value.KnowledgeBaseId.ToString("D"), value.ParentCollectionId?.ToString("D"), value.DefaultSchemaId?.ToString("D"), value.ExternalId, value.Title, value.Description, value.Tags.ToArray());
}
public sealed record NativeSchemaRequest(string Key, string? DisplayName, string? Id, int Revision, NativeSchemaField[]? Fields);
public sealed record NativeSchemaField(string Key, KnowledgeFieldType Type, SemanticMode SemanticMode = SemanticMode.None, int SemanticWeightPercent = 0, bool Required = false, bool Filterable = false, string? DisplayName = null);
public sealed record NativeSchema(string Id, string Key, string DisplayName, int Revision, NativeSchemaField[] Fields)
{
    public static NativeSchema From(KnowledgeSchemaDefinition schema) => new(schema.Id.ToString("D"), schema.Key, schema.DisplayName, schema.Revision, schema.Fields.Select(field => new NativeSchemaField(field.Key, field.Type, field.SemanticMode, field.SemanticWeightPercent, field.Required, field.Filterable, field.DisplayName)).ToArray());
}
public sealed record NativeDocumentRequest(string? Id, string? ExternalId, string KnowledgeBaseId, string CollectionId, string SchemaId, string Title, string? Description, string[]? Tags, NativeValue[]? Values);
public sealed record NativeValue(string Key, KnowledgeFieldType Type, string? Text = null, long? Int64 = null, decimal? Decimal = null, bool? Boolean = null, DateTimeOffset? DateTimeOffset = null, string? Guid = null)
{
    public KnowledgeValue ToKnowledgeValue() => Type switch
    {
        KnowledgeFieldType.Text => KnowledgeValue.From(Text),
        KnowledgeFieldType.Int64 => KnowledgeValue.From(Int64 ?? throw new InvalidOperationException($"Value '{Key}' requires int64.")),
        KnowledgeFieldType.Decimal => KnowledgeValue.From(Decimal ?? throw new InvalidOperationException($"Value '{Key}' requires decimal.")),
        KnowledgeFieldType.Boolean => KnowledgeValue.From(Boolean ?? throw new InvalidOperationException($"Value '{Key}' requires boolean.")),
        KnowledgeFieldType.DateTimeOffset => KnowledgeValue.From(DateTimeOffset ?? throw new InvalidOperationException($"Value '{Key}' requires dateTimeOffset.")),
        KnowledgeFieldType.Guid => KnowledgeValue.From(System.Guid.Parse(Guid ?? throw new InvalidOperationException($"Value '{Key}' requires guid."))),
        _ => throw new NotSupportedException($"Unsupported knowledge value type {Type}.")
    };
}
public sealed record NativeIdRequest(string Id);
public sealed record NativeIdResult(string Id);
public sealed record NativeDocument(string Id, string? ExternalId, string KnowledgeBaseId, string CollectionId, string SchemaId, string Title, string Description, string[] Tags, NativeValue[] Values)
{
    public static NativeDocument From(KnowledgeDocumentRecord value) => new(value.Id.ToString("D"), value.ExternalId, value.KnowledgeBaseId.ToString("D"), value.CollectionId.ToString("D"), value.SchemaId.ToString("D"), value.Title, value.Description, value.Tags.ToArray(), value.Values.Select(pair => NativeValueFactory.From(pair.Key, pair.Value)).ToArray());
}
public sealed record NativeSearchRequest(string Query, string KnowledgeBaseId, KnowledgeSearchMode Mode = KnowledgeSearchMode.Global, string[]? CollectionIds = null, bool IncludeDescendants = true, NativeFilterNode? Filter = null, int Top = 10, int? CandidateCount = null, KnowledgeResultInclude Include = KnowledgeResultInclude.MetadataOnly);
public sealed record NativeFilterNode(string? Field, KnowledgeFilterOperator? Operator, NativeValue? Value, NativeValue[]? Values, NativeFilterNode[]? And, NativeFilterNode[]? Or, NativeFilterNode? Not)
{
    public KnowledgeFilter ToKnowledgeFilter()
    {
        if (And is { Length: > 0 }) return KnowledgeFilters.And(And.Select(x => x.ToKnowledgeFilter()).ToArray());
        if (Or is { Length: > 0 }) return KnowledgeFilters.Or(Or.Select(x => x.ToKnowledgeFilter()).ToArray());
        if (Not is not null) return KnowledgeFilters.Not(Not.ToKnowledgeFilter());
        if (string.IsNullOrWhiteSpace(Field) || Operator is null) throw new InvalidOperationException("A native filter leaf requires field and operator.");
        return Operator.Value switch
        {
            KnowledgeFilterOperator.Equal => KnowledgeFilters.Eq(Field, RequiredValue()),
            KnowledgeFilterOperator.NotEqual => KnowledgeFilters.Ne(Field, RequiredValue()),
            KnowledgeFilterOperator.LessThan => KnowledgeFilters.Lt(Field, RequiredValue()),
            KnowledgeFilterOperator.LessThanOrEqual => KnowledgeFilters.Lte(Field, RequiredValue()),
            KnowledgeFilterOperator.GreaterThan => KnowledgeFilters.Gt(Field, RequiredValue()),
            KnowledgeFilterOperator.GreaterThanOrEqual => KnowledgeFilters.Gte(Field, RequiredValue()),
            KnowledgeFilterOperator.In => KnowledgeFilters.In(Field, (Values ?? []).Select(x => x.ToKnowledgeValue()).ToArray()),
            KnowledgeFilterOperator.IsNull => KnowledgeFilters.IsNull(Field),
            KnowledgeFilterOperator.IsNotNull => KnowledgeFilters.IsNotNull(Field),
            KnowledgeFilterOperator.TagContains => KnowledgeFilters.HasTag(Value?.Text ?? throw new InvalidOperationException("TagContains requires a text value.")),
            _ => throw new NotSupportedException($"Unsupported native filter operator {Operator}.")
        };
    }
    private KnowledgeValue RequiredValue() => Value?.ToKnowledgeValue() ?? throw new InvalidOperationException($"Filter operator {Operator} requires a value.");
}
public sealed record NativeSearchResponse(NativeSearchHit[] Hits);
public sealed record NativeSearchHit(string DocumentId, string CollectionId, string SchemaId, float Score, string Title, string Description, string[] Tags, NativeMatch[] Matches)
{
    public static NativeSearchHit From(KnowledgeSearchHit hit) => new(hit.DocumentId.ToString("D"), hit.CollectionId.ToString("D"), hit.SchemaId.ToString("D"), hit.Score, hit.Title, hit.Description, hit.Tags.ToArray(), hit.Matches.Select(match => new NativeMatch(match.FieldKey, match.RawSimilarity, match.AdjustedSimilarity, match.TokenCount, match.CharacterRange.Start, match.CharacterRange.Length, match.Text)).ToArray());
}
public sealed record NativeMatch(string FieldKey, float RawSimilarity, float AdjustedSimilarity, int TokenCount, int CharacterStart, int CharacterLength, string? Text);

internal static class NativeValueFactory
{
    public static NativeValue From(string key, KnowledgeValue value) => value.Type switch
    {
        KnowledgeFieldType.Text => new NativeValue(key, value.Type, Text: value.Text),
        KnowledgeFieldType.Int64 => new NativeValue(key, value.Type, Int64: value.Int64),
        KnowledgeFieldType.Decimal => new NativeValue(key, value.Type, Decimal: value.Decimal),
        KnowledgeFieldType.Boolean => new NativeValue(key, value.Type, Boolean: value.Boolean),
        KnowledgeFieldType.DateTimeOffset => new NativeValue(key, value.Type, DateTimeOffset: value.DateTimeOffset),
        KnowledgeFieldType.Guid => new NativeValue(key, value.Type, Guid: value.Guid?.ToString("D")),
        _ => throw new NotSupportedException($"Unsupported value type {value.Type}.")
    };
}

[JsonSerializable(typeof(NativeSqliteStoreOptions))]
[JsonSerializable(typeof(NativeCapabilities))]
[JsonSerializable(typeof(NativeNamedResourceRequest))]
[JsonSerializable(typeof(NativeKnowledgeBase))]
[JsonSerializable(typeof(NativeCollectionRequest))]
[JsonSerializable(typeof(NativeCollection))]
[JsonSerializable(typeof(NativeSchemaRequest))]
[JsonSerializable(typeof(NativeSchema))]
[JsonSerializable(typeof(NativeDocumentRequest))]
[JsonSerializable(typeof(NativeIdRequest))]
[JsonSerializable(typeof(NativeIdResult))]
[JsonSerializable(typeof(NativeDocument))]
[JsonSerializable(typeof(NativeSearchRequest))]
[JsonSerializable(typeof(NativeSearchResponse))]
internal partial class NativeJsonContext : JsonSerializerContext;
