using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace RhinoTrunk.Contracts;

/// <summary>
/// Content addresses used everywhere in RhinoTrunk: <c>sha256:</c> followed by 64 lowercase hex digits,
/// computed over the exact bytes stored (blobs) or over canonical JSON (manifests, trees, commits).
/// </summary>
public static class ContentHash
{
    public const string Prefix = "sha256:";
    public const int Length = 71; // "sha256:" + 64 hex digits

    public static string Compute(ReadOnlySpan<byte> data)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(data, digest);
        return Prefix + Convert.ToHexString(digest).ToLowerInvariant();
    }

    public static bool IsValid([NotNullWhen(true)] string? hash)
    {
        if (hash is null || hash.Length != Length || !hash.StartsWith(Prefix, StringComparison.Ordinal))
            return false;
        foreach (var c in hash.AsSpan(Prefix.Length))
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                return false;
        }
        return true;
    }

    /// <summary>The 64 hex digits without the algorithm prefix.</summary>
    public static string Hex(string hash) => hash.Substring(Prefix.Length);
}
