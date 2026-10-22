// Release signing for Developer Island's one-click updates (ECDSA P-256 over SHA-256, DER signature).
// Runs with the .NET 10 SDK, no project needed:
//   dotnet run tools/release-signing.cs -- new-key <private-key.pem>   once: creates the key pair
//   dotnet run tools/release-signing.cs -- sign <private-key.pem> <file> writes <file>.sig
//   dotnet run tools/release-signing.cs -- verify <file>                 checks <file>.sig against the app's public key
// new-key writes the private key to the given path (it must be outside the repository) and the public
// key to src/DeveloperIsland.Core/Updates/release-signing-public-key.pem, which the app embeds.
// The signature is also checkable with OpenSSL:
//   openssl dgst -sha256 -verify release-signing-public-key.pem -signature <file>.sig <file>
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

var repo = Path.GetFullPath(Path.Combine(ScriptDirectory(), ".."));
var publicKeyPath = Path.Combine(repo, "src", "DeveloperIsland.Core", "Updates", "release-signing-public-key.pem");

try
{
    return args switch
    {
        ["new-key", var privatePath] => NewKey(Path.GetFullPath(privatePath)),
        ["sign", var privatePath, var file] => Sign(Path.GetFullPath(privatePath), Path.GetFullPath(file)),
        ["verify", var file] => Verify(Path.GetFullPath(file)),
        _ => Usage(),
    };
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
{
    Console.Error.WriteLine($"release-signing: {ex.Message}");
    return 1;
}

int NewKey(string privatePath)
{
    if (IsInside(privatePath, repo))
    {
        Console.Error.WriteLine("The private key must not live inside the repository. Choose a path outside it, for example in your user profile.");
        return 1;
    }

    if (File.Exists(privatePath) || File.Exists(publicKeyPath))
    {
        Console.Error.WriteLine("A key already exists. Apps in the wild trust the current public key; replacing it means they can no longer verify updates.");
        Console.Error.WriteLine($"Delete {(File.Exists(privatePath) ? privatePath : publicKeyPath)} first if you really mean to start over.");
        return 1;
    }

    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    Directory.CreateDirectory(Path.GetDirectoryName(privatePath)!);
    File.WriteAllText(privatePath, key.ExportPkcs8PrivateKeyPem() + "\n");
    File.WriteAllText(publicKeyPath, key.ExportSubjectPublicKeyInfoPem() + "\n");
    Console.WriteLine($"Private key: {privatePath}  (keep it secret and backed up; never commit it)");
    Console.WriteLine($"Public key:  {publicKeyPath}  (commit it; the app embeds it)");
    return 0;
}

int Sign(string privatePath, string file)
{
    if (IsInside(privatePath, repo))
    {
        Console.Error.WriteLine("Refusing to use a private key inside the repository.");
        return 1;
    }

    using var key = ECDsa.Create();
    key.ImportFromPem(File.ReadAllText(privatePath));
    if (key.ExportParameters(false).Curve.Oid?.Value != "1.2.840.10045.3.1.7")
    {
        Console.Error.WriteLine("The key is not an ECDSA P-256 key.");
        return 1;
    }

    var signature = key.SignData(File.ReadAllBytes(file), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    File.WriteAllBytes(file + ".sig", signature);
    Console.WriteLine($"Signature: {file}.sig");
    return Verify(file);
}

int Verify(string file)
{
    if (!File.Exists(publicKeyPath))
    {
        Console.Error.WriteLine($"No public key at {publicKeyPath}. Create the key pair with new-key first.");
        return 1;
    }

    using var key = ECDsa.Create();
    key.ImportFromPem(File.ReadAllText(publicKeyPath));
    var valid = key.VerifyData(File.ReadAllBytes(file), File.ReadAllBytes(file + ".sig"), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    Console.WriteLine(valid ? "Signature verifies with the app's public key." : "Signature does NOT verify with the app's public key.");
    return valid ? 0 : 1;
}

int Usage()
{
    Console.Error.WriteLine("Usage: dotnet run tools/release-signing.cs -- new-key <private-key.pem> | sign <private-key.pem> <file> | verify <file>");
    return 2;
}

static bool IsInside(string path, string folder) =>
    path.StartsWith(folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

static string ScriptDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
