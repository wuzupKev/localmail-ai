using Dapper;
using LocalMailAI.Api.Data;
using LocalMailAI.Api.Security;
using MailKit.Net.Imap;
using MailKit.Security;

namespace LocalMailAI.Api.Accounts;

/// <summary>
/// Interactive IMAP account registration (<c>--add-account</c>).
///
/// It is a command and not an endpoint on purpose: the password must not travel over
/// HTTP, appear in a request log, or pass through the browser. It is typed into the
/// console without echo, verified against the server, and stored encrypted with DPAPI.
///
/// Order matters: **the credentials are checked first and only then stored**. Storing
/// first would leave the user with a registered account that fails on every sync
/// without telling them why.
/// </summary>
public static class AccountSetup
{
    /// <summary>Known servers, so the user does not have to look up the host.</summary>
    private static readonly Dictionary<string, (string Host, int Port)> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gmail.com"] = ("imap.gmail.com", 993),
        ["googlemail.com"] = ("imap.gmail.com", 993),
        ["outlook.com"] = ("outlook.office365.com", 993),
        ["hotmail.com"] = ("outlook.office365.com", 993),
        ["live.com"] = ("outlook.office365.com", 993),
        ["office365.com"] = ("outlook.office365.com", 993),
        ["yahoo.com"] = ("imap.mail.yahoo.com", 993),
        ["icloud.com"] = ("imap.mail.me.com", 993),
        ["me.com"] = ("imap.mail.me.com", 993),
        ["fastmail.com"] = ("imap.fastmail.com", 993),
        ["zoho.com"] = ("imap.zoho.com", 993),
    };

    public static async Task<int> RunAsync(WebApplication app)
    {
        var db = app.Services.GetRequiredService<Database>();
        var credentials = app.Services.GetRequiredService<CredentialStore>();

        Console.WriteLine();
        Console.WriteLine("  Add a mail account to LocalMail AI");
        Console.WriteLine("  ----------------------------------");
        Console.WriteLine();
        Console.WriteLine("  You need an APP PASSWORD, not your account password.");
        Console.WriteLine("  Gmail: Google Account > Security > 2-Step Verification >");
        Console.WriteLine("  App passwords. It is 16 letters.");
        Console.WriteLine();
        Console.WriteLine("  The password is not shown as you type, is not written to any");
        Console.WriteLine("  text file, and does not go through the API. It is encrypted with");
        Console.WriteLine("  DPAPI and only your Windows user on this machine can decrypt it.");
        Console.WriteLine();

        var email = Ask("  Email address");
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
        {
            Console.Error.WriteLine("  That does not look like an email address.");
            return 2;
        }

        email = email.Trim().ToLowerInvariant();
        var domain = email.Split('@')[1];

        var (suggestedHost, suggestedPort) = Known.TryGetValue(domain, out var known)
            ? known
            : ("imap." + domain, 993);

        var host = Ask($"  IMAP server [{suggestedHost}]");
        host = string.IsNullOrWhiteSpace(host) ? suggestedHost : host.Trim();

        var portText = Ask($"  Port [{suggestedPort}]");
        var port = string.IsNullOrWhiteSpace(portText) ? suggestedPort : int.Parse(portText.Trim());

        var password = AskWithoutEcho("  App password");
        if (string.IsNullOrWhiteSpace(password))
        {
            Console.Error.WriteLine("  Nothing entered.");
            return 2;
        }

        // Gmail displays app passwords in groups of four. Many people copy them with
        // the spaces and the server rejects them without explaining why.
        var stripped = password.Replace(" ", string.Empty);
        if (stripped.Length != password.Length)
        {
            Console.WriteLine("  (removed the spaces from the app password)");
            password = stripped;
        }

        // --- Verify BEFORE storing ------------------------------------------------

        Console.WriteLine();
        Console.WriteLine($"  Connecting to {host}:{port}...");

        string[] folders;
        try
        {
            folders = await VerifyAsync(host, port, email, password);
        }
        catch (AuthenticationException)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("  The server rejected the credentials.");
            Console.Error.WriteLine("  Common causes, most frequent first:");
            Console.Error.WriteLine("    - You used your normal password instead of an app password.");
            Console.Error.WriteLine("    - Two-step verification is not enabled (Gmail requires it");
            Console.Error.WriteLine("      before app passwords can be created).");
            Console.Error.WriteLine("    - IMAP access is turned off in your mail settings.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("  Nothing was stored.");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"  Could not connect: {ex.Message}");
            Console.Error.WriteLine("  Check the server, the port and your connection. Nothing was stored.");
            return 1;
        }

        Console.WriteLine($"  Connected. {folders.Length} folders visible.");

        var sent = folders.FirstOrDefault(f =>
            f.Contains("sent", StringComparison.OrdinalIgnoreCase) ||
            f.Contains("enviado", StringComparison.OrdinalIgnoreCase));

        // The Sent folder is the source of the cold start (§7): without it the system
        // begins with no examples at all, and three of the five buckets take far
        // longer to mean anything.
        Console.WriteLine(sent is not null
            ? $"  Sent folder detected: {sent}"
            : "  WARNING: no sent folder found. The cold start will be slower.");

        // --- Store ----------------------------------------------------------------

        var key = CredentialStore.KeyFor(email);
        credentials.Save(key, password);

        var id = Guid.NewGuid().ToString("n")[..12];

        using var cn = db.Open();
        app.Services.GetRequiredService<Migrator>().Migrate(cn);

        await cn.ExecuteAsync(
            """
            INSERT INTO accounts (id, email, imap_host, imap_port, credential_key, created_at)
            VALUES (@id, @email, @host, @port, @key, @now)
            ON CONFLICT(email) DO UPDATE SET
                imap_host = @host,
                imap_port = @port,
                credential_key = @key
            """,
            new { id, email, host, port, key, now = DateTimeOffset.UtcNow.ToString("O") });

        Console.WriteLine();
        Console.WriteLine($"  Account {email} registered.");
        Console.WriteLine($"  Password encrypted in {credentials.Directory}");
        Console.WriteLine($"  Database              {db.Path}");
        Console.WriteLine();
        Console.WriteLine("  Next: start the backend and sync.");
        Console.WriteLine();

        return 0;
    }

    /// <summary>
    /// Connects, authenticates and lists folders. This is the minimum proof that the
    /// account can do what the product needs: read.
    /// </summary>
    private static async Task<string[]> VerifyAsync(string host, int port, string email, string password)
    {
        using var client = new ImapClient();

        // SslOnConnect for 993 (IMAPS); StartTls for 143. It never connects without
        // encryption: that would put the password on the wire in the clear.
        var options = port == 143 ? SecureSocketOptions.StartTls : SecureSocketOptions.SslOnConnect;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await client.ConnectAsync(host, port, options, timeout.Token);
        await client.AuthenticateAsync(email, password, timeout.Token);

        var folders = (await client.GetFoldersAsync(client.PersonalNamespaces[0], cancellationToken: timeout.Token))
            .Select(f => f.FullName)
            .ToArray();

        await client.DisconnectAsync(true, timeout.Token);

        return folders;
    }

    private static string Ask(string label)
    {
        Console.Write($"{label}: ");
        return Console.ReadLine() ?? string.Empty;
    }

    /// <summary>Reads without echoing, so the password never appears on screen.</summary>
    private static string AskWithoutEcho(string label)
    {
        Console.Write($"{label}: ");

        var sb = new System.Text.StringBuilder();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return sb.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0)
                {
                    sb.Length--;
                }
                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                sb.Append(key.KeyChar);
            }
        }
    }
}
