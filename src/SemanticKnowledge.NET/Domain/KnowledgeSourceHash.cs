using System.Security.Cryptography;
using System.Text;

namespace SemanticKnowledge;

public static class KnowledgeSourceHash
{
    public static string Compute(KnowledgeDocumentInput document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var builder = new StringBuilder()
            .Append(document.Title).Append('\n')
            .Append(document.Description).Append('\n');

        foreach (var tag in document.Tags.Order(StringComparer.OrdinalIgnoreCase))
            builder.Append("tag:").Append(tag).Append('\n');

        foreach (var value in document.Values.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            builder.Append(value.Key).Append('=').Append(value.Value.ToObject()).Append('\n');

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
