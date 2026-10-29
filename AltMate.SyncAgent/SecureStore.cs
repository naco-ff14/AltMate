using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal sealed class SecureStore(string directory)
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AltMate.WebSync.v1");
    internal string DirectoryPath => directory;
    internal bool Exists(string name) => File.Exists(Path.Combine(directory, name + ".bin"));
    internal void Save<T>(string name, T value)
    {
        Directory.CreateDirectory(directory);
        var plain = JsonSerializer.SerializeToUtf8Bytes(value);
        try
        {
            var encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            var path = Path.Combine(directory, name + ".bin");
            var temp = path + ".tmp";
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                file.Write(encrypted);
                file.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    internal T Load<T>(string name)
    {
        var encrypted = File.ReadAllBytes(Path.Combine(directory, name + ".bin"));
        var plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<T>(plain) ?? throw new InvalidDataException("Stored sync data is invalid."); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    internal void Remove(string name) => File.Delete(Path.Combine(directory, name + ".bin"));
}

internal sealed record DeviceIdentity(string ProjectUrl, Guid DeviceId, string Token, string HmacKey);
internal sealed record PairingResponse(Guid DeviceId, string Token, string HmacKey);
