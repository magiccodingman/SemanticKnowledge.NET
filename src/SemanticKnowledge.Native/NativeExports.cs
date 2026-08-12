using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SemanticKnowledge.Native;

public static unsafe class NativeExports
{
    public const uint AbiVersion = 1;

    [UnmanagedCallersOnly(EntryPoint = "sk_abi_version", CallConvs = [typeof(CallConvCdecl)])]
    public static uint AbiVersionExport() => AbiVersion;

    [UnmanagedCallersOnly(EntryPoint = "sk_version", CallConvs = [typeof(CallConvCdecl)])]
    public static int Version(byte* buffer, nuint capacity)
    {
        const string value = "0.1";
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        if (buffer is null || capacity <= (nuint)bytes.Length) return bytes.Length + 1;
        bytes.CopyTo(new Span<byte>(buffer, bytes.Length));
        buffer[bytes.Length] = 0;
        return bytes.Length;
    }
}
