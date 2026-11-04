using System.Security.Cryptography;
using System.Text;

namespace LocalMailAI.Api.Security;

/// <summary>
/// Password storage encrypted with DPAPI (BACKEND.md §1, rule 2).
///
/// The rule is literal: "Credentials in the operating system store. Never in
/// appsettings.json, never in the database, never in plain text."
///
/// DPAPI at user scope means the encrypted blob can only be decrypted from THAT
/// person's Windows session on THAT machine. Copying the file to another computer, or
/// opening it from another account on the same computer, gets you nothing. What the
/// database stores is only the KEY used to locate the blob
/// (<c>accounts.credential_key</c>), never the password itself.
///
/// Consequence worth knowing: if the user loses their Windows profile, the blob can no
/// longer be decrypted. That is not a bug, it is the price of not keeping a master key
/// somewhere; the correct fix is to register the account again, not to store the
/// password somewhere less safe.
/// </summary>
public sealed class CredentialStore
{
    private readonly string _directory;

    public CredentialStore(IConfiguration config)
    {
        var configured = config["Credentials:Path"];

        _directory = !string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(configured)
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LocalMailAI",
                "credentials");
    }

    public string Directory => _directory;

    /// <summary>
    /// Stable key for an account. Contains no password, and does not contain the
    /// address either: the file name ends up on disk and can show up in a screenshot
    /// or a backup.
    /// </summary>
    public static string KeyFor(string email) =>
        "imap-" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant())))[..16];

    public void Save(string key, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(secret);

        System.IO.Directory.CreateDirectory(_directory);

        if (!OperatingSystem.IsWindows())
        {
            // There is no DPAPI outside Windows. Rather than quietly writing the
            // password in the clear, fail: the user is entitled to know their
            // credential would not be protected.
            throw new PlatformNotSupportedException(
                "the credential store uses DPAPI and only works on Windows. " +
                "On other systems their keyring must be integrated before storing anything.");
        }

        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(secret),
            optionalEntropy: EntropyFor(key),
            scope: DataProtectionScope.CurrentUser);

        var path = PathFor(key);
        File.WriteAllBytes(path, encrypted);

        try
        {
            var info = new FileInfo(path);
            info.Attributes |= FileAttributes.NotContentIndexed;
        }
        catch (IOException)
        {
            // An attribute that cannot be set is no reason to abort the registration.
        }
    }

    /// <summary>Returns the password, or null if it is not stored.</summary>
    public string? TryRead(string key)
    {
        var path = PathFor(key);

        if (!File.Exists(path))
        {
            return null;
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("the credential store uses DPAPI and only works on Windows");
        }

        try
        {
            var plain = ProtectedData.Unprotect(
                File.ReadAllBytes(path),
                optionalEntropy: EntropyFor(key),
                scope: DataProtectionScope.CurrentUser);

            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            // A blob from another user or another machine. It is not recoverable, and
            // saying so clearly beats returning an empty password that would later
            // surface as "authentication rejected".
            return null;
        }
    }

    public bool Delete(string key)
    {
        var path = PathFor(key);

        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    private string PathFor(string key) => Path.Combine(_directory, key + ".bin");

    /// <summary>
    /// Extra entropy bound to the key, so two different accounts produce blobs that
    /// are not interchangeable even if someone renames the files.
    /// </summary>
    private static byte[] EntropyFor(string key) =>
        SHA256.HashData(Encoding.UTF8.GetBytes("LocalMailAI/" + key));
}
