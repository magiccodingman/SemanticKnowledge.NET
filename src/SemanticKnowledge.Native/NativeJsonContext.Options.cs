using System.Text.Json.Serialization;

namespace SemanticKnowledge.Native;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
internal partial class NativeJsonContext
{
}
