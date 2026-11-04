using Dapper;
using LocalMailAI.Api.Classification;
using LocalMailAI.Api.Data;
using LocalMailAI.Api.Security;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;

namespace LocalMailAI.Api.Mail;

/// <summary>What <c>GET /api/labels/pending</c> returns.</summary>
public sealed record PendingLabelsDto(int Count, string[] EmailIds);

/// <summary>One message's outcome from a write. Failures are per message, not per batch.</summary>
public sealed record LabelResultDto(string EmailId, bool Ok, string? Folder, string? Error);

/// <summary>What <c>POST /api/labels/apply</c> returns.</summary>
public sealed record ApplyLabelsDto(int Applied, int Failed, LabelResultDto[] Results);

/// <summary>
/// Milestone 10: copies messages into <c>LocalMail/&lt;Bucket&gt;</c> folders on the IMAP
/// server, so the triage is visible in Gmail, Apple Mail or on a phone.
///
/// Three rules this class exists to keep:
///
/// Nothing moves on its own. This runs only from <c>POST /api/labels/apply</c>, which
/// is the user pressing a button after a dialog. Rule 3 of §1.
///
/// It copies, it never moves. A message that left the inbox because triage thought it
/// was noise would be a mail client losing your mail. The original stays where it is.
///
/// A failure is reported per message. Writing to IMAP routinely dies halfway through a
/// batch -- a dropped connection, a server limit, a folder that cannot be created --
/// and the user has to know what was written and what was not in order to decide
/// whether to retry.
/// </summary>
public sealed class LabelWriter(
    Database db,
    CredentialStore credentials,
    ILogger<LabelWriter> log)
{
    // Init properties rather than positional records, for the same reason as the sync:
    // Dapper matches a record constructor by exact parameter types.
    private sealed class PendingRow
    {
        public string Id { get; init; } = string.Empty;
        public string AccountId { get; init; } = string.Empty;
        public string Bucket { get; init; } = string.Empty;
        public long Uid { get; init; }
        public string Folder { get; init; } = string.Empty;
    }

    private sealed class AccountRow
    {
        public string Id { get; init; } = string.Empty;
        public string Email { get; init; } = string.Empty;
        public string ImapHost { get; init; } = string.Empty;
        public int ImapPort { get; init; }
        public string CredentialKey { get; init; } = string.Empty;
    }

    /// <summary>The IMAP folder a bucket is written to.</summary>
    public static string FolderFor(Bucket bucket) => "LocalMail/" + bucket switch
    {
        Bucket.Reply => "Reply",
        Bucket.Fyi => "FYI",
        Bucket.Automated => "Automated",
        Bucket.Noise => "Noise",
        _ => throw new ArgumentOutOfRangeException(nameof(bucket), bucket, null),
    };

    private const string PendingSql =
        """
        SELECT e.id AS Id, e.account_id AS AccountId, c.bucket AS Bucket,
               e.uid AS Uid, e.folder AS Folder
        FROM emails e
        JOIN classifications c ON c.email_id = e.id
        LEFT JOIN label_writes w ON w.email_id = e.id
        WHERE c.confidence >= @threshold
          AND (w.email_id IS NULL OR w.bucket <> c.bucket)
        ORDER BY e.date_utc DESC
        """;

    /// <summary>
    /// Pending = classified above the threshold, and either never written or written
    /// under a bucket that is no longer the current one. Below-threshold mail is never
    /// pending: the product does not write a label it is not sure about.
    /// </summary>
    public async Task<PendingLabelsDto> PendingAsync()
    {
        using var cn = db.Open();
        var rows = await cn.QueryAsync<PendingRow>(PendingSql, new { threshold = SignalBaseline.ConfidenceThreshold });
        var ids = rows.Select(r => r.Id).ToArray();
        return new PendingLabelsDto(ids.Length, ids);
    }

    public async Task<ApplyLabelsDto> ApplyAsync(CancellationToken ct = default)
    {
        using var cn = db.Open();

        var pending = (await cn.QueryAsync<PendingRow>(
            PendingSql, new { threshold = SignalBaseline.ConfidenceThreshold })).ToList();

        if (pending.Count == 0)
        {
            return new ApplyLabelsDto(0, 0, []);
        }

        var accounts = (await cn.QueryAsync<AccountRow>(
            """
            SELECT id AS Id, email AS Email, imap_host AS ImapHost,
                   imap_port AS ImapPort, credential_key AS CredentialKey
            FROM accounts
            """)).ToDictionary(a => a.Id);

        var results = new List<LabelResultDto>();

        foreach (var group in pending.GroupBy(p => p.AccountId))
        {
            if (!accounts.TryGetValue(group.Key, out var account))
            {
                results.AddRange(group.Select(p =>
                    new LabelResultDto(p.Id, false, null, "the account no longer exists")));
                continue;
            }

            try
            {
                await ApplyForAccountAsync(cn, account, group.ToList(), results, ct);
            }
            catch (Exception ex)
            {
                // The connection itself failed: every message in this group is unwritten,
                // and each one says so rather than the batch reporting a single error.
                log.LogError(ex, "Could not write labels for {Email}", account.Email);
                var already = results.Select(r => r.EmailId).ToHashSet();
                results.AddRange(group
                    .Where(p => !already.Contains(p.Id))
                    .Select(p => new LabelResultDto(p.Id, false, null, ex.Message)));
            }
        }

        var applied = results.Count(r => r.Ok);
        return new ApplyLabelsDto(applied, results.Count - applied, results.ToArray());
    }

    private async Task ApplyForAccountAsync(
        Microsoft.Data.Sqlite.SqliteConnection cn,
        AccountRow account,
        List<PendingRow> rows,
        List<LabelResultDto> results,
        CancellationToken ct)
    {
        var password = credentials.TryRead(account.CredentialKey)
            ?? throw new InvalidOperationException($"no stored password for {account.Email}");

        using var client = new ImapClient();
        var options = account.ImapPort == 143 ? SecureSocketOptions.StartTls : SecureSocketOptions.SslOnConnect;
        await client.ConnectAsync(account.ImapHost, account.ImapPort, options, ct);
        await client.AuthenticateAsync(account.Email, password, ct);

        var destinations = new Dictionary<string, IMailFolder>(StringComparer.Ordinal);

        foreach (var sourceGroup in rows.GroupBy(r => r.Folder))
        {
            var source = await client.GetFolderAsync(sourceGroup.Key, ct);
            await source.OpenAsync(FolderAccess.ReadOnly, ct);

            foreach (var row in sourceGroup)
            {
                ct.ThrowIfCancellationRequested();

                var bucket = BucketNames.Parse(row.Bucket);
                var path = FolderFor(bucket);

                try
                {
                    if (!destinations.TryGetValue(path, out var destination))
                    {
                        destination = await EnsureFolderAsync(client, path, ct);
                        destinations[path] = destination;
                    }

                    // Copy, never move: the message stays in the inbox.
                    await source.CopyToAsync(new UniqueId((uint)row.Uid), destination, ct);

                    await cn.ExecuteAsync(
                        """
                        INSERT INTO label_writes (email_id, folder, bucket, written_at)
                        VALUES (@id, @folder, @bucket, @now)
                        ON CONFLICT(email_id) DO UPDATE SET
                            folder = @folder, bucket = @bucket, written_at = @now
                        """,
                        new { id = row.Id, folder = path, bucket = row.Bucket, now = DateTimeOffset.UtcNow.ToString("O") });

                    results.Add(new LabelResultDto(row.Id, true, path, null));
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Could not write {Id} to {Folder}", row.Id, path);
                    results.Add(new LabelResultDto(row.Id, false, path, ex.Message));
                }
            }
        }

        await client.DisconnectAsync(true, ct);
    }

    /// <summary>
    /// Finds or creates <c>LocalMail/&lt;Bucket&gt;</c>, using the server's own hierarchy
    /// separator rather than assuming '/'.
    /// </summary>
    private static async Task<IMailFolder> EnsureFolderAsync(ImapClient client, string path, CancellationToken ct)
    {
        var parts = path.Split('/');
        IMailFolder parent = client.GetFolder(client.PersonalNamespaces[0])
            ?? throw new InvalidOperationException("the server exposes no personal namespace");

        foreach (var name in parts)
        {
            IMailFolder? child;
            try
            {
                child = await parent.GetSubfolderAsync(name, ct);
            }
            catch (FolderNotFoundException)
            {
                // Expected on the first write: the folder is ours to create.
                child = null;
            }

            // Both halves are annotated as nullable by MailKit, so the result is pinned
            // non-null here rather than carried forward as a maybe through the loop.
            parent = (child ?? await parent.CreateAsync(name, isMessageFolder: name == parts[^1], ct))
                ?? throw new InvalidOperationException($"could not open or create the folder {name}");
        }

        return parent;
    }
}
