using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace DeveloperIsland.Core.Updates;

/// <summary>
/// The release checksums are signed (ECDSA P-256 over SHA-256, DER-encoded, as
/// <c>tools/release-signing.cs</c> writes it): the setup is not code-signed and its checksum file
/// comes from the same release page, so only the signature ties a download to the project's key.
/// </summary>
public static class UpdateSignature
{
    private const string P256Oid = "1.2.840.10045.3.1.7";

    /// <summary>The public key built into this app (PEM), or null in a build made without one.</summary>
    public static string? EmbeddedPublicKey { get; } = ReadEmbeddedKey();

    /// <summary>True only for a valid signature of <paramref name="data"/> by the P-256 key in <paramref name="publicKeyPem"/>.</summary>
    public static bool Verify(byte[] data, byte[] signature, string publicKeyPem)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            return key.ExportParameters(false).Curve.Oid?.Value == P256Oid
                && key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    private static string? ReadEmbeddedKey()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("release-signing-public-key.pem");
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream, Encoding.ASCII);
        var pem = reader.ReadToEnd();
        return pem.Contains("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal) ? pem : null;
    }
}

/// <summary>Reads a <c>SHA256SUMS</c> file ("hash  name" per line, as sha256sum and build-release.ps1 write it).</summary>
public static class ChecksumFile
{
    /// <summary>File name to lower-case hex hash; malformed lines are skipped.</summary>
    public static IReadOnlyDictionary<string, string> Parse(string text)
    {
        var sums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var space = line.IndexOf(' ');
            if (space != 64)
            {
                continue;
            }

            var hash = line[..64];
            var name = line[64..].TrimStart(' ', '*');
            if (name.Length > 0 && hash.All(char.IsAsciiHexDigit))
            {
                sums[name] = hash.ToLowerInvariant();
            }
        }

        return sums;
    }
}
