// -----------------------------------------------------------------------
// <copyright file="ChatDpapiSecretStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Common.Chat;

using System.Security.Cryptography;
using System.Text;

/// <summary>Connection credential references backed by atomic, per-user Windows DPAPI blobs.</summary>
public sealed class ChatDpapiSecretStore(string directory) : IChatSecretStore
{
    private readonly string root = Path.GetFullPath(directory);
    public string Id => "dpapi";
    public ChatSecretStoreStatus Status => new(OperatingSystem.IsWindows(), false, OperatingSystem.IsWindows() ? null : "windows-secret-store-required");

    public ValueTask<string?> ReadAsync(ChatSecretReference reference, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (reference.Store != this.Id) return ValueTask.FromResult<string?>(null);
        return ValueTask.FromResult(ReadBlob(this.PathFor(reference.Key)));
    }
    public async ValueTask<ChatSecretReference> WriteAsync(string key, string value, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) throw new NotSupportedException("windows-secret-store-required");
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8192) throw new ArgumentException("invalid-secret-value");
        var path = this.PathFor(key); Directory.CreateDirectory(this.root);
        var plaintext = Encoding.UTF8.GetBytes(value);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var encrypted = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
            await File.WriteAllBytesAsync(temp, encrypted, ct); ct.ThrowIfCancellationRequested();
            File.Move(temp, path, overwrite: true); return new(this.Id, key);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); if (File.Exists(temp)) File.Delete(temp); }
    }
    public ValueTask DeleteAsync(ChatSecretReference reference, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (reference.Store == this.Id) File.Delete(this.PathFor(reference.Key));
        return ValueTask.CompletedTask;
    }
    private string PathFor(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 256 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw new ArgumentException("invalid-secret-reference");
        return Path.Combine(this.root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".bin");
    }
    /// <summary>Legacy blobs are read-only migration inputs. No directory scanning or automatic import.</summary>
    internal static string? ReadBlob(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path)) return null;
        try
        {
            if (new FileInfo(path).Length > 65536) return null;
            var plaintext = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
            try { var value = Encoding.UTF8.GetString(plaintext).Trim(); return value.Length == 0 ? null : value; }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException) { return null; }
    }
}