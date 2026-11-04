using Dapper;
using LocalMailAI.Api.Data;
using LocalMailAI.Api.Security;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace LocalMailAI.Api.Mail;

/// <summary>What <c>GET /api/sync/status</c> reports.</summary>
public sealed record SyncStatus(string State, int Fetched, int Total, string? LastSync, string? Error);

/// <summary>
/// Milestone 2: fetches mail over IMAP and hands each message to the ingestor.
///
/// This class only moves bytes. Everything done with a message -- parsing,
/// normalisation, signals, classification -- belongs to <see cref="EmailIngestor"/>,
/// which is why that can be tested with .eml files and no network at all.
///
/// The sync is incremental and resumable. <c>accounts.last_uid</c> records how far the
/// last pass got, and UIDVALIDITY guards it: an IMAP server is allowed to renumber
/// every UID in a folder, and when it does the stored watermark means nothing. On that
/// change the watermark is dropped and the folder is read again. Nothing is duplicated
/// when that happens, because identity is the hash of Message-ID, never the UID.
/// </summary>
public sealed class ImapSync(
    Database db,
    CredentialStore credentials,
    EmailIngestor ingestor,
    IConfiguration config,
    ILogger<ImapSync> log)
{
    /// <summary>
    /// One registered account, as the sync needs it.
    ///
    /// Init properties and not a positional record: Dapper matches a record's
    /// constructor by exact parameter types, and the two nullable columns make that
    /// match fail at runtime rather than at compile time.
    /// </summary>
    private sealed class AccountRow
    {
        public string Id { get; init; } = string.Empty;
        public string Email { get; init; } = string.Empty;
        public string ImapHost { get; init; } = string.Empty;
        public int ImapPort { get; init; }
        public string CredentialKey { get; init; } = string.Empty;
        public long? LastUid { get; init; }
        public long? UidValidity { get; init; }
    }

    private readonly SemaphoreSlim _gate = new(1, 1);

    private volatile string _state = "idle";
    private volatile int _fetched;
    private volatile int _total;
    private volatile string? _lastSync;
    private volatile string? _error;

    /// <summary>
    /// How many of the most recent messages a first sync reads. A mailbox of tens of
    /// thousands would otherwise make the first run look like a hang, and triage has
    /// nothing useful to say about mail from four years ago.
    /// </summary>
    private int FirstPassLimit => config.GetValue("Imap:FirstPassLimit", 500);

    public SyncStatus Status() => new(_state, _fetched, _total, _lastSync, _error);

    /// <summary>
    /// Runs one pass. Returns the number of messages newly ingested.
    ///
    /// Only one pass runs at a time: a second caller is refused rather than queued,
    /// because two passes over the same folder would do the same work twice and race
    /// on the same watermark.
    /// </summary>
    public async Task<int> SyncAsync(CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct))
        {
            throw new InvalidOperationException("a sync is already running");
        }

        _state = "syncing";
        _fetched = 0;
        _total = 0;
        _error = null;

        try
        {
            using var cn = db.Open();
            var accounts = (await cn.QueryAsync<AccountRow>(
                """
                SELECT id AS Id, email AS Email, imap_host AS ImapHost, imap_port AS ImapPort,
                       credential_key AS CredentialKey, last_uid AS LastUid, uid_validity AS UidValidity
                FROM accounts
                ORDER BY created_at
                """)).ToList();

            if (accounts.Count == 0)
            {
                throw new InvalidOperationException(
                    "no account registered. Run --add-account first");
            }

            var ingested = 0;
            foreach (var account in accounts)
            {
                ct.ThrowIfCancellationRequested();
                ingested += await SyncAccountAsync(cn, account, ct);
            }

            _lastSync = DateTimeOffset.UtcNow.ToString("O");
            return ingested;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            log.LogError(ex, "Sync failed");
            throw;
        }
        finally
        {
            _state = "idle";
            _gate.Release();
        }
    }

    private async Task<int> SyncAccountAsync(
        Microsoft.Data.Sqlite.SqliteConnection cn,
        AccountRow account,
        CancellationToken ct)
    {
        var password = credentials.TryRead(account.CredentialKey)
            ?? throw new InvalidOperationException(
                $"no stored password for {account.Email}. Run --add-account again");

        using var client = new ImapClient();

        // SslOnConnect for 993 (IMAPS), StartTls for 143. Never an unencrypted channel.
        var options = account.ImapPort == 143 ? SecureSocketOptions.StartTls : SecureSocketOptions.SslOnConnect;

        log.LogInformation("Connecting to {Host}:{Port} as {Email}",
            account.ImapHost, account.ImapPort, account.Email);

        await client.ConnectAsync(account.ImapHost, account.ImapPort, options, ct);
        await client.AuthenticateAsync(account.Email, password, ct);

        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly, ct);

        // A changed UIDVALIDITY invalidates every stored UID for this folder.
        var validity = inbox.UidValidity;
        var lastUid = account.UidValidity == validity ? account.LastUid : null;

        if (account.UidValidity is not null && account.UidValidity != validity)
        {
            log.LogWarning(
                "UIDVALIDITY changed for {Email} ({Old} -> {New}); reading the folder again",
                account.Email, account.UidValidity, validity);
        }

        IList<UniqueId> uids;
        if (lastUid is null)
        {
            // First pass: the most recent slice, newest last so the watermark advances
            // monotonically even if the pass is interrupted.
            var all = await inbox.SearchAsync(SearchQuery.All, ct);
            uids = all.Skip(Math.Max(0, all.Count - FirstPassLimit)).ToList();
            log.LogInformation("First sync for {Email}: {Taken} of {All} messages",
                account.Email, uids.Count, all.Count);
        }
        else
        {
            var range = new UniqueIdRange(new UniqueId((uint)(lastUid.Value + 1)), UniqueId.MaxValue);
            uids = await inbox.SearchAsync(range, SearchQuery.All, ct);
            log.LogInformation("Incremental sync for {Email}: {Count} new since UID {Uid}",
                account.Email, uids.Count, lastUid);
        }

        _total += uids.Count;

        var ingested = 0;
        var highest = lastUid ?? 0;

        foreach (var uid in uids)
        {
            ct.ThrowIfCancellationRequested();

            MimeMessage message;
            try
            {
                message = await inbox.GetMessageAsync(uid, ct);
            }
            catch (Exception ex)
            {
                // One unreadable message must not end the pass. §11: log it, do not fail.
                log.LogWarning(ex, "Could not read UID {Uid} for {Email}", uid.Id, account.Email);
                _fetched++;
                continue;
            }

            try
            {
                var result = await ingestor.IngestAsync(cn, account.Id, message, uid.Id, inbox.FullName, 0);
                if (result.Inserted)
                {
                    ingested++;
                }
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Could not ingest UID {Uid} for {Email}", uid.Id, account.Email);
            }

            _fetched++;
            highest = Math.Max(highest, uid.Id);

            // The watermark is written as the pass goes, not at the end: an interrupted
            // sync must resume where it stopped rather than start over.
            if (_fetched % 25 == 0)
            {
                await SaveWatermarkAsync(cn, account.Id, highest, validity);
            }
        }

        await SaveWatermarkAsync(cn, account.Id, highest, validity);
        await client.DisconnectAsync(true, ct);

        log.LogInformation("{Email}: {Ingested} new of {Seen} read", account.Email, ingested, uids.Count);
        return ingested;
    }

    private static Task SaveWatermarkAsync(
        Microsoft.Data.Sqlite.SqliteConnection cn,
        string accountId,
        long lastUid,
        uint validity) =>
        cn.ExecuteAsync(
            "UPDATE accounts SET last_uid = @lastUid, uid_validity = @validity WHERE id = @accountId",
            new { lastUid, validity = (long)validity, accountId });
}
