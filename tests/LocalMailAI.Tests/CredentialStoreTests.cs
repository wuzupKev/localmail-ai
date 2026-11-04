using LocalMailAI.Api.Security;
using Microsoft.Extensions.Configuration;

namespace LocalMailAI.Tests;

/// <summary>
/// Credential storage (BACKEND.md §1, rule 2): never in plain text, never in the
/// database, never in appsettings.
/// </summary>
public sealed class CredentialStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"localmail-cred-{Guid.NewGuid():N}");

    private readonly CredentialStore _store;

    public CredentialStoreTests()
    {
        _store = new CredentialStore(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Credentials:Path"] = _directory })
            .Build());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void The_password_round_trips()
    {
        var key = CredentialStore.KeyFor("kevin@sagetasolutions.com");

        _store.Save(key, "abcd efgh ijkl mnop");

        Assert.Equal("abcd efgh ijkl mnop", _store.TryRead(key));
    }

    [Fact]
    public void The_password_is_not_on_disk_in_plain_text()
    {
        // The check that actually matters: the file must not contain the secret.
        const string secret = "a-very-recognisable-password-12345";
        var key = CredentialStore.KeyFor("kevin@sagetasolutions.com");

        _store.Save(key, secret);

        var files = Directory.GetFiles(_directory);
        Assert.Single(files);

        var bytes = File.ReadAllBytes(files[0]);
        var asUtf8 = System.Text.Encoding.UTF8.GetString(bytes);
        var asUtf16 = System.Text.Encoding.Unicode.GetString(bytes);

        Assert.DoesNotContain(secret, asUtf8, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, asUtf16, StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_that_does_not_exist_returns_null_instead_of_failing()
    {
        Assert.Null(_store.TryRead(CredentialStore.KeyFor("nobody@example.com")));
    }

    [Fact]
    public void Two_accounts_get_different_keys()
    {
        var a = CredentialStore.KeyFor("kevin@sagetasolutions.com");
        var b = CredentialStore.KeyFor("kevin@personal.com");

        Assert.NotEqual(a, b);

        _store.Save(a, "secret-a");
        _store.Save(b, "secret-b");

        Assert.Equal("secret-a", _store.TryRead(a));
        Assert.Equal("secret-b", _store.TryRead(b));
    }

    [Fact]
    public void The_key_ignores_case_and_surrounding_whitespace()
    {
        Assert.Equal(
            CredentialStore.KeyFor("kevin@sagetasolutions.com"),
            CredentialStore.KeyFor("  Kevin@SagetaSolutions.COM  "));
    }

    [Fact]
    public void The_key_does_not_contain_the_address()
    {
        // The file name ends up on disk and can show up in a screenshot or a backup.
        var key = CredentialStore.KeyFor("kevin@sagetasolutions.com");

        Assert.DoesNotContain("kevin", key, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sagetasolutions", key, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_blob_belonging_to_another_key_cannot_be_decrypted()
    {
        // The extra entropy is bound to the key: renaming the file achieves nothing.
        var a = CredentialStore.KeyFor("kevin@sagetasolutions.com");
        var b = CredentialStore.KeyFor("other@example.com");

        _store.Save(a, "secret-of-a");

        var source = Path.Combine(_directory, a + ".bin");
        var destination = Path.Combine(_directory, b + ".bin");
        File.Copy(source, destination);

        Assert.Null(_store.TryRead(b));
    }

    [Fact]
    public void Deleting_removes_the_credential()
    {
        var key = CredentialStore.KeyFor("kevin@sagetasolutions.com");
        _store.Save(key, "secret");

        Assert.True(_store.Delete(key));
        Assert.Null(_store.TryRead(key));
        Assert.False(_store.Delete(key));
    }
}
