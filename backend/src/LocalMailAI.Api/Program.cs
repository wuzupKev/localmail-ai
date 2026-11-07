using System.Reflection;
using System.Text.Json.Serialization;
using Dapper;
using LocalMailAI.Api.Accounts;
using LocalMailAI.Api.Classification;
using LocalMailAI.Api.Data;
using LocalMailAI.Api.Embeddings;
using LocalMailAI.Api.Mail;
using LocalMailAI.Api.Model;
using LocalMailAI.Api.Security;
using MimeKit;

// Note on language: comments and identifiers are English. Error CODES and the Spanish
// strings in error messages stay as they are - they are contract values the frontend
// matches on, like the bucket names.

var builder = WebApplication.CreateBuilder(args);

// Fixed port 5174 and loopback ONLY (BACKEND.md §3 and rule 1 of §1: no network
// beyond IMAP and localhost). Listening on 0.0.0.0 would expose the user's mail to
// their local network.
builder.WebHost.UseUrls("http://127.0.0.1:5174");

builder.Services.AddSingleton(sp => new Database(
    Database.ResolveDefaultPath(sp.GetRequiredService<IConfiguration>())));
builder.Services.AddSingleton<Migrator>();
builder.Services.AddSingleton<EmailRepository>();
builder.Services.AddSingleton<EmailIngestor>();
builder.Services.AddSingleton<CredentialStore>();
builder.Services.AddSingleton<EmbeddingProvider>();
builder.Services.AddSingleton<CorrectionsRepository>();
builder.Services.AddSingleton<ModelStatusService>();
builder.Services.AddSingleton<ImapSync>();
builder.Services.AddSingleton<LabelWriter>();
builder.Services.AddSingleton<OllamaClient>();
builder.Services.AddSingleton<SummaryService>();

// In TypeScript "extracted?: {...}" is "X | undefined", not "X | null": an optional
// property that does not exist is omitted, not serialised as null.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull);

// The Next.js frontend runs on another port, so it needs explicit CORS.
// Closed allowlist: no AllowAnyOrigin.
const string CorsFront = "front-local";
builder.Services.AddCors(o => o.AddPolicy(CorsFront, p => p
    .WithOrigins("http://localhost:3000", "http://127.0.0.1:3000")
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();

// Migrate on startup: if the schema cannot be brought up to date it is better to
// serve no requests at all than to serve them against a half-migrated database.
{
    var db = app.Services.GetRequiredService<Database>();
    var migrator = app.Services.GetRequiredService<Migrator>();
    using var cn = db.Open();
    var schemaVersion = migrator.Migrate(cn);
    app.Logger.LogInformation("Database {Path} ready, schema v{Version}", db.Path, schemaVersion);
}

// --add-account registers an IMAP account and exits. A command and not an endpoint on
// purpose: the password must not travel over HTTP or pass through the browser.
if (args.Contains("--add-account"))
{
    return await AccountSetup.RunAsync(app);
}

// --seed <folder> ingests the .eml files in a folder and exits. It exists so the inbox
// can be populated without an IMAP account; the product ships no sample data of its
// own, it only knows how to read files it is given.
var seedIndex = Array.IndexOf(args, "--seed");
if (seedIndex >= 0)
{
    if (seedIndex + 1 >= args.Length)
    {
        app.Logger.LogError("--seed needs the path of a folder containing .eml files");
        return 2;
    }

    return await SeedAsync(app, args[seedIndex + 1]);
}

app.UseCors(CorsFront);

var version = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
    .InformationalVersion.Split('+')[0] ?? "0.0.0";

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", version }));

app.MapGet("/api/emails", async (EmailRepository repo, string? bucket, int? limit, string? cursor) =>
{
    if (bucket is not null && !IsValidBucket(bucket))
    {
        return Error(StatusCodes.Status400BadRequest, "bucket_invalido",
            $"bucket must be reply, fyi, automated, noise or unsure; got '{bucket}'");
    }

    var page = await repo.ListAsync(bucket, limit ?? 50, cursor, DateTimeOffset.Now);
    return Results.Ok(page);
});

app.MapGet("/api/emails/{id}", async (EmailRepository repo, string id) =>
{
    var email = await repo.GetAsync(id, DateTimeOffset.Now);
    return email is null
        ? Error(StatusCodes.Status404NotFound, "email_no_encontrado", $"no message with id '{id}'")
        : Results.Ok(email);
});

// The five tiles on the main screen are the frontend's navigation and are painted on
// every load: a counts endpoint avoids fetching five whole buckets to show five
// numbers.
app.MapGet("/api/counts", async (EmailRepository repo) => Results.Ok(await repo.CountsAsync()));

// Mark read. The frontend calls this ONLY on an explicit open (click, Enter, the sheet
// on mobile) and never while navigating with j/k: marking read on keyboard navigation
// would leave the whole inbox read without the user having read anything.
app.MapPatch("/api/emails/{id}", async (EmailRepository repo, string id, UnreadPatch body) =>
{
    if (body?.Unread is null)
    {
        return Error(StatusCodes.Status400BadRequest, "cuerpo_invalido",
            "se esperaba {\"unread\": true|false}");
    }

    var email = await repo.SetUnreadAsync(id, body.Unread.Value, DateTimeOffset.Now);
    return email is null
        ? Error(StatusCodes.Status404NotFound, "email_no_encontrado", $"no message with id '{id}'")
        : Results.Ok(email);
});

// Frontend sidebar: examples, precision, indexed messages and whether Ollama is there.
// The breakdown by origin lets the UI distinguish "I don't know you yet" (few
// examples, and the ones there came from the bootstrap) from "I can't classify this".
// --- Search (§8, BM25 half) --------------------------------------------------------
// The vector half needs the embedding model; without it this degrades to full text
// rather than failing, which is the same rule the rest of the service follows.
app.MapGet("/api/search", async (EmailRepository repo, string? q, int? limit) =>
{
    if (string.IsNullOrWhiteSpace(q))
    {
        return Error(400, "missing_query", "q is required");
    }

    var items = await repo.SearchAsync(q, Math.Clamp(limit ?? 20, 1, 100), DateTimeOffset.Now);
    return Results.Ok(new { items });
});

// --- IMAP sync (§2) ----------------------------------------------------------------
// Fire and forget: a mailbox takes minutes, far longer than an HTTP request should be
// held open. The caller polls /api/sync/status, which is what the header indicator in
// the interface already does.
app.MapPost("/api/sync", (ImapSync sync, ILogger<Program> logger) =>
{
    if (sync.Status().State == "syncing")
    {
        return Error(409, "sync_running", "a sync is already running");
    }

    _ = Task.Run(async () =>
    {
        try
        {
            await sync.SyncAsync();
        }
        catch (Exception ex)
        {
            // Already recorded in the status the client polls; this is for the console.
            logger.LogError(ex, "Background sync failed");
        }
    });

    return Results.Accepted("/api/sync/status", new { state = "syncing" });
});

app.MapGet("/api/sync/status", (ImapSync sync) => Results.Ok(sync.Status()));

// --- IMAP label write-back (§10) ---------------------------------------------------
app.MapGet("/api/labels/pending", async (LabelWriter writer) => Results.Ok(await writer.PendingAsync()));

// The ONLY route that writes to the mail server, and only on an explicit call. Rule 3.
app.MapPost("/api/labels/apply", async (LabelWriter writer) =>
{
    try
    {
        return Results.Ok(await writer.ApplyAsync());
    }
    catch (Exception ex)
    {
        return Error(502, "imap_write_failed", ex.Message);
    }
});

app.MapGet("/api/model/status", async (ModelStatusService status) => Results.Ok(await status.GetAsync()));

// The core of the product: the user corrects and the system learns. The response
// carries the example total because the frontend shows it in a toast immediately.
app.MapPost("/api/emails/{id}/classification",
    async (CorrectionsRepository corrections, string id, ClassificationRequest body) =>
{
    if (body?.Bucket is null)
    {
        return Error(StatusCodes.Status400BadRequest, "cuerpo_invalido",
            "se esperaba {\"bucket\": \"responder|informativo|automatizado|ruido\", \"source\": \"user\"}");
    }

    // 'unsure' is not correctable: it is a bucket derived from the threshold, not a
    // destination. And the user cannot declare "not sure" on the system's behalf.
    if (!IsValidBucket(body.Bucket) || body.Bucket == "unsure")
    {
        return Error(StatusCodes.Status400BadRequest, "bucket_invalido",
            $"bucket must be reply, fyi, automated or noise; got '{body.Bucket}'");
    }

    // Only 'user' is accepted. The other origins ('bootstrap_sent', 'rule') are set by
    // the system, and letting them in through the API would ruin the measurement that
    // distinguishes them from the person's real corrections.
    if (body.Source is not null and not "user")
    {
        return Error(StatusCodes.Status400BadRequest, "source_invalido",
            $"source can only be 'user' from the API; got '{body.Source}'");
    }

    var r = await corrections.ApplyAsync(id, BucketNames.Parse(body.Bucket), DateTimeOffset.Now);

    return r is null
        ? Error(StatusCodes.Status404NotFound, "email_no_encontrado", $"no message with id '{id}'")
        : Results.Ok(r);
});

// Undo (the frontend's Ctrl+Z). It does not delete the correction: it inserts the
// inverse event. With nothing to undo it returns 200 with a null email, so the
// frontend does not have to track whether a stack remains.
app.MapPost("/api/corrections/undo", async (CorrectionsRepository corrections) =>
{
    return Results.Ok(await corrections.UndoAsync(DateTimeOffset.Now));
});

// --- Thread summary (§5) -----------------------------------------------------------
// On demand only, never on sync: generation costs seconds of CPU and most threads are
// never opened. ?force=true regenerates instead of serving the cache.
//
// Unlike the summary carried by GET /api/emails, which §5 limits to threads of three
// messages or more, this route summarises whatever it is asked to: the limit there is
// about not cluttering a list, and here the user clicked the button.
app.MapPost("/api/emails/{id}/summary", async (SummaryService summaries, string id, bool? force, CancellationToken ct) =>
{
    try
    {
        var summary = await summaries.ForEmailAsync(id, force ?? false, ct);

        return summary is null
            ? Error(StatusCodes.Status404NotFound, "email_no_encontrado", $"no message with id '{id}'")
            : Results.Ok(new { summary, model = OllamaClient.Model });
    }
    catch (OllamaUnavailableException ex)
    {
        // 503 and not 500: the service is fine, the optional dependency is not, and
        // the frontend shows the message as-is so the user knows what to install.
        return Error(StatusCodes.Status503ServiceUnavailable, "ollama_unavailable", ex.Message);
    }
});

app.Run();
return 0;

// --- Helpers -----------------------------------------------------------------

static bool IsValidBucket(string bucket) =>
    bucket is "reply" or "fyi" or "automated" or "noise" or "unsure";

/// <summary>
/// Resolves a folder given on the command line.
///
/// `dotnet run --project X -- --seed fixtures` sets the working directory to the
/// PROJECT's, not to where the command was typed, so a reasonable relative path is not
/// found. It is tried as given, then relative to the backend root.
/// </summary>
static string? ResolveFolder(string folder)
{
    if (Directory.Exists(folder))
    {
        return folder;
    }

    if (Path.IsPathRooted(folder))
    {
        return null;
    }

    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        // The backend root is the directory holding the solution file. It is NOT
        // identified by models/: that directory holds the 470 MB of weights, is not in
        // git, and is absent from a fresh clone -- which is exactly when someone runs
        // --seed to look at the fixtures without downloading anything.
        if (File.Exists(Path.Combine(dir.FullName, "LocalMailAI.slnx")))
        {
            var candidate = Path.Combine(dir.FullName, folder);
            return Directory.Exists(candidate) ? candidate : null;
        }
        dir = dir.Parent;
    }

    return null;
}

/// <summary>The §3 error envelope: <c>{ "error": { "code", "message" } }</c>.</summary>
static IResult Error(int status, string code, string message) =>
    Results.Json(new { error = new { code, message } }, statusCode: status);

static async Task<int> SeedAsync(WebApplication app, string folder)
{
    var resolved = ResolveFolder(folder);

    if (resolved is null)
    {
        app.Logger.LogError(
            "cannot find folder {Folder}. Tried: as given, relative to {Current}, and relative to the backend root",
            folder, Directory.GetCurrentDirectory());
        return 2;
    }

    folder = resolved;

    var files = Directory.GetFiles(folder, "*.eml", SearchOption.AllDirectories);
    if (files.Length == 0)
    {
        app.Logger.LogError("no .eml files in {Folder}", folder);
        return 2;
    }

    var db = app.Services.GetRequiredService<Database>();
    var ingestor = app.Services.GetRequiredService<EmailIngestor>();
    using var cn = db.Open();

    // A local seeding account: the signals need to know which address is the user's
    // own in order to tell To: from Cc:, and where the organisation domain comes from.
    const string accountId = "seed";
    var accountEmail = app.Configuration["Seed:Account"] ?? "kevin@sagetasolutions.com";

    await cn.ExecuteAsync(
        """
        INSERT INTO accounts (id, email, imap_host, imap_port, credential_key, created_at)
        VALUES (@accountId, @accountEmail, 'localhost', 0, 'none-local-seed', @now)
        ON CONFLICT(id) DO UPDATE SET email = @accountEmail
        """,
        new { accountId, accountEmail, now = DateTimeOffset.UtcNow.ToString("O") });

    var added = 0;
    var duplicates = 0;
    var failed = 0;
    var byBucket = new Dictionary<Bucket, int>();

    // In name order, which is chronological for the fixtures: thread state (do you
    // participate, unanswered messages) depends on the order messages arrive in, just
    // as it does in a real sync.
    foreach (var file in files.OrderBy(f => f, StringComparer.Ordinal))
    {
        try
        {
            var message = await MimeMessage.LoadAsync(file);
            var info = new FileInfo(file);
            var r = await ingestor.IngestAsync(cn, accountId, message, 0, "INBOX", info.Length);

            if (r.Inserted)
            {
                added++;
                byBucket[r.Bucket] = byBucket.GetValueOrDefault(r.Bucket) + 1;
            }
            else
            {
                duplicates++;
            }
        }
        catch (Exception ex)
        {
            // §11: broken mail exists. Record it, do not fail.
            failed++;
            app.Logger.LogWarning("could not ingest {File}: {Error}", file, ex.Message);
        }
    }

    app.Logger.LogInformation(
        "Seeding finished: {Added} added, {Duplicates} already present, {Failed} failed",
        added, duplicates, failed);

    foreach (var (bucket, n) in byBucket.OrderByDescending(p => p.Value))
    {
        app.Logger.LogInformation("  {Bucket}: {N}", bucket.ToWire(), n);
    }

    return failed > 0 && added == 0 ? 1 : 0;
}

/// <summary>
/// Body of PATCH /api/emails/{id}. Nullable on purpose, so "unread: false" is
/// distinguishable from "you did not send me the field".
/// </summary>
internal sealed record UnreadPatch(bool? Unread);

/// <summary>
/// Body of POST /api/emails/{id}/classification. `source` is optional and only accepts
/// "user": see the comment on the endpoint.
/// </summary>
internal sealed record ClassificationRequest(string? Bucket, string? Source);
