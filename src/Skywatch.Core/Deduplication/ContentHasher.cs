using System.Security.Cryptography;
using System.Text;
using Skywatch.Core.Normalization;

namespace Skywatch.Core.Deduplication;

/// <summary>AC-F6.3: lowercase hex SHA-256 of the UTF-8 bytes of <see cref="NormalizedText.Lower"/>.</summary>
public static class ContentHasher
{
    public static string Hash(NormalizedText text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.Lower)));
}
