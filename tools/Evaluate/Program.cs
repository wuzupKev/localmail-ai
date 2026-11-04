// Milestone 5 of BACKEND.md §9: evaluation set and per-bucket metrics.
//
// "Milestone 5 is neither optional nor later: without an evaluation set you cannot
// know whether the LLM adds anything over the heuristics, and you will be optimising
// blind."
//
// This is the instrument. It ingests a folder of .eml files into a throwaway database,
// compares the system's classification against a label file, and records the result in
// eval_runs of the real database so /api/model/status can serve a measured precision
// instead of a constant.
//
// Usage:
//   dotnet run --project tools/Evaluate -- --emails fixtures --labels eval/fixtures-18.json
//   ... --record-to <path.db>     where to record the run (default: the product's database)
//   ... --no-record               measure only, record nothing

using System.Text;
using System.Text.Json;
using Dapper;
using LocalMailAI.Api.Classification;
using LocalMailAI.Api.Data;
using LocalMailAI.Api.Mail;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;

Console.OutputEncoding = Encoding.UTF8;

var arguments = ReadArguments(args);

if (!arguments.TryGetValue("emails", out var emailFolder) ||
    !arguments.TryGetValue("labels", out var labelFile))
{
    Console.Error.WriteLine("usage: --emails <folder of .eml> --labels <file.json> [--record-to <path.db> | --no-record]");
    return 2;
}

if (!Directory.Exists(emailFolder))
{
    Console.Error.WriteLine($"folder {emailFolder} does not exist");
    return 2;
}

if (!File.Exists(labelFile))
{
    Console.Error.WriteLine($"label file {labelFile} does not exist");
    return 2;
}

// --- Labels -------------------------------------------------------------------

using var labelDoc = JsonDocument.Parse(File.ReadAllBytes(labelFile));
var labelRoot = labelDoc.RootElement;
var setName = labelRoot.GetProperty("set").GetString() ?? "unnamed";

// Not representative by default: it has to be declared deliberately. If the default
// were true, a test set would reach the user's interface by omission, which is the
// kind of mistake nobody catches.
var representative = labelRoot.TryGetProperty("representativo", out var rep) && rep.GetBoolean();

var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
foreach (var e in labelRoot.GetProperty("labels").EnumerateArray())
{
    labels[e.GetProperty("file").GetString()!] = e.GetProperty("bucket").GetString()!;
}

Console.WriteLine($"set        : {setName} ({labels.Count} labels, {(representative ? "representative" : "NOT representative: never reaches the UI")})");

if (labelRoot.TryGetProperty("aviso", out var notice))
{
    Console.WriteLine();
    Console.WriteLine("SET NOTICE:");
    foreach (var line in Wrap(notice.GetString() ?? string.Empty, 76))
    {
        Console.WriteLine($"  {line}");
    }
    Console.WriteLine();
}

// --- Ingest into a throwaway database -------------------------------------------
// Never the user's own: measuring must not touch their data.

var tempPath = Path.Combine(Path.GetTempPath(), $"localmail-eval-{Guid.NewGuid():N}.db");
var db = new Database(tempPath);
var ingestor = new EmailIngestor(NullLogger<EmailIngestor>.Instance);

try
{
    using (var cn = db.Open())
    {
        new Migrator(NullLogger<Migrator>.Instance).Migrate(cn);

        await cn.ExecuteAsync(
            """
            INSERT INTO accounts (id, email, imap_host, imap_port, credential_key, created_at)
            VALUES ('eval', 'kevin@sagetasolutions.com', 'localhost', 0, 'none-evaluation', @now)
            """,
            new { now = DateTimeOffset.UtcNow.ToString("O") });

        // In name order, which is chronological for the fixtures: thread state depends
        // on the order messages arrive in, just as in a real sync.
        var files = Directory.GetFiles(emailFolder, "*.eml")
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        var unlabelled = new List<string>();
        var predictions = new List<(string File, string Expected, string? Produced, double Confidence)>();

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);

            if (!labels.TryGetValue(name, out var expected))
            {
                unlabelled.Add(name);
                continue;
            }

            var message = await MimeMessage.LoadAsync(file);
            var r = await ingestor.IngestAsync(cn, "eval", message, 0, "INBOX", new FileInfo(file).Length);

            var row = await cn.QuerySingleAsync<(string Bucket, double Confidence)>(
                "SELECT bucket, confidence FROM classifications WHERE email_id = @id",
                new { id = r.EmailId });

            predictions.Add((name, expected, row.Bucket, row.Confidence));
        }

        if (unlabelled.Count > 0)
        {
            Console.WriteLine($"WARNING: {unlabelled.Count} messages have no label and are excluded:");
            foreach (var n in unlabelled)
            {
                Console.WriteLine($"  {n}");
            }
            Console.WriteLine();
        }

        var missing = labels.Keys.Except(predictions.Select(p => p.File), StringComparer.OrdinalIgnoreCase).ToList();
        if (missing.Count > 0)
        {
            Console.WriteLine($"WARNING: {missing.Count} labels have no matching message:");
            foreach (var n in missing)
            {
                Console.WriteLine($"  {n}");
            }
            Console.WriteLine();
        }

        if (predictions.Count == 0)
        {
            Console.Error.WriteLine("there is nothing labelled to measure");
            return 2;
        }

        var report = Measure(predictions);
        Print(report, predictions);

        // --- Record the run -------------------------------------------------------

        if (arguments.ContainsKey("no-record"))
        {
            Console.WriteLine("run NOT recorded (--no-record)");
            return 0;
        }

        var destinationPath = arguments.TryGetValue("record-to", out var r2) && !string.IsNullOrWhiteSpace(r2)
            ? r2
            : Database.ResolveDefaultPath(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

        var destination = new Database(destinationPath);
        using var destinationCn = destination.Open();
        new Migrator(NullLogger<Migrator>.Instance).Migrate(destinationCn);

        await destinationCn.ExecuteAsync(
            """
            INSERT INTO eval_runs (
                created_at, model_version, eval_set, set_size, representative, accuracy,
                precision_macro, recall_macro, f1_macro, coverage,
                per_bucket_json, confusion_json)
            VALUES (
                @createdAt, @modelVersion, @evalSet, @setSize, @representative, @accuracy,
                @precisionMacro, @recallMacro, @f1Macro, @coverage,
                @perBucketJson, @confusionJson)
            """,
            new
            {
                createdAt = DateTimeOffset.UtcNow.ToString("O"),
                // 0 = deterministic signals only. This is the baseline milestone 6 has
                // to beat; if it does not, the model adds nothing.
                modelVersion = 0,
                evalSet = setName,
                setSize = predictions.Count,
                representative = representative ? 1 : 0,
                accuracy = report.Accuracy,
                precisionMacro = report.PrecisionMacro,
                recallMacro = report.RecallMacro,
                f1Macro = report.F1Macro,
                coverage = report.Coverage,
                perBucketJson = JsonSerializer.Serialize(report.PerBucket),
                confusionJson = JsonSerializer.Serialize(report.Confusion),
            });

        Console.WriteLine($"run recorded in {destinationPath}");
    }
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    foreach (var f in new[] { tempPath, tempPath + "-wal", tempPath + "-shm" })
    {
        if (File.Exists(f))
        {
            File.Delete(f);
        }
    }
}

return 0;

// --- Metrics --------------------------------------------------------------------

static Report Measure(List<(string File, string Expected, string? Produced, double Confidence)> predictions)
{
    var buckets = new[] { "responder", "informativo", "automatizado", "ruido" };

    // A message below the threshold is NOT a prediction: the system said it does not
    // know. Counting it as a failure would punish caution; counting it as a success
    // would reward it. It leaves precision and recall, and is measured separately as
    // coverage.
    var committed = predictions.Where(p => p.Confidence >= SignalBaseline.ConfidenceThreshold).ToList();
    var coverage = (double)committed.Count / predictions.Count;

    var correct = committed.Count(p => p.Produced == p.Expected);
    var accuracy = committed.Count == 0 ? 0 : (double)correct / committed.Count;

    var perBucket = new Dictionary<string, BucketMetrics>();

    foreach (var bucket in buckets)
    {
        var tp = committed.Count(p => p.Produced == bucket && p.Expected == bucket);
        var fp = committed.Count(p => p.Produced == bucket && p.Expected != bucket);
        var fn = committed.Count(p => p.Produced != bucket && p.Expected == bucket);

        var precision = tp + fp == 0 ? 0 : (double)tp / (tp + fp);
        var recall = tp + fn == 0 ? 0 : (double)tp / (tp + fn);
        var f1 = precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);

        // Support comes from the labels, not from what the system predicted: it
        // includes the messages of that bucket that fell below the threshold.
        var support = predictions.Count(p => p.Expected == bucket);

        perBucket[bucket] = new BucketMetrics(precision, recall, f1, support, tp, fp, fn);
    }

    // Macro and not micro: the four buckets have very different sizes and micro would
    // be dominated by the largest, hiding the fact that the system fails precisely on
    // the small one. Only buckets with labelled messages count: averaging an empty
    // bucket as 0 would sink the mean without meaning anything.
    var supported = perBucket.Where(kv => kv.Value.Support > 0).Select(kv => kv.Value).ToList();

    var confusion = new Dictionary<string, Dictionary<string, int>>();
    foreach (var expected in buckets)
    {
        var row = new Dictionary<string, int>();
        foreach (var produced in buckets)
        {
            row[produced] = committed.Count(p => p.Expected == expected && p.Produced == produced);
        }
        // Extra column: the ones the system would not commit to.
        row["duda"] = predictions.Count(p =>
            p.Expected == expected && p.Confidence < SignalBaseline.ConfidenceThreshold);
        confusion[expected] = row;
    }

    return new Report(
        accuracy,
        supported.Count == 0 ? 0 : supported.Average(m => m.Precision),
        supported.Count == 0 ? 0 : supported.Average(m => m.Recall),
        supported.Count == 0 ? 0 : supported.Average(m => m.F1),
        coverage,
        perBucket,
        confusion);
}

static void Print(Report r, List<(string File, string Expected, string? Produced, double Confidence)> predictions)
{
    Console.WriteLine($"messages measured        : {predictions.Count}");
    Console.WriteLine($"coverage                 : {r.Coverage:P0}  (the rest fell below the {SignalBaseline.ConfidenceThreshold} threshold)");
    Console.WriteLine($"accuracy over covered    : {r.Accuracy:P0}");
    Console.WriteLine($"macro precision          : {r.PrecisionMacro:F3}");
    Console.WriteLine($"macro recall             : {r.RecallMacro:F3}");
    Console.WriteLine($"macro F1                 : {r.F1Macro:F3}");
    Console.WriteLine();

    Console.WriteLine("per bucket:");
    Console.WriteLine("  bucket         prec.  recall     F1   support   TP  FP  FN");
    foreach (var (bucket, m) in r.PerBucket)
    {
        Console.WriteLine($"  {bucket,-13} {m.Precision,6:F2} {m.Recall,7:F2} {m.F1,6:F2} {m.Support,9} {m.Tp,4} {m.Fp,3} {m.Fn,3}");
    }
    Console.WriteLine();

    Console.WriteLine("confusion matrix (rows = expected, columns = produced):");
    Console.WriteLine("                  respond.  inform.  automat.   ruido    duda");
    foreach (var (expected, row) in r.Confusion)
    {
        Console.Write($"  {expected,-14}");
        foreach (var column in new[] { "responder", "informativo", "automatizado", "ruido", "duda" })
        {
            Console.Write($"{row[column],9}");
        }
        Console.WriteLine();
    }
    Console.WriteLine();

    var failures = predictions
        .Where(p => p.Confidence >= SignalBaseline.ConfidenceThreshold && p.Produced != p.Expected)
        .ToList();

    if (failures.Count > 0)
    {
        Console.WriteLine($"failures ({failures.Count}):");
        foreach (var f in failures)
        {
            Console.WriteLine($"  {f.File,-32} expected {f.Expected,-13} produced {f.Produced,-13} ({f.Confidence:F2})");
        }
        Console.WriteLine();
    }

    var abstentions = predictions.Where(p => p.Confidence < SignalBaseline.ConfidenceThreshold).ToList();
    if (abstentions.Count > 0)
    {
        Console.WriteLine($"no answer given ({abstentions.Count}):");
        foreach (var a in abstentions)
        {
            Console.WriteLine($"  {a.File,-32} expected {a.Expected,-13} would have said {a.Produced,-13} ({a.Confidence:F2})");
        }
        Console.WriteLine();
    }
}

static Dictionary<string, string> ReadArguments(string[] args)
{
    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }

        var key = args[i][2..];
        var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[++i]
            : string.Empty;

        d[key] = value;
    }

    return d;
}

static IEnumerable<string> Wrap(string text, int width)
{
    var line = new StringBuilder();

    foreach (var word in text.Split(' '))
    {
        if (line.Length + word.Length + 1 > width && line.Length > 0)
        {
            yield return line.ToString();
            line.Clear();
        }

        if (line.Length > 0)
        {
            line.Append(' ');
        }

        line.Append(word);
    }

    if (line.Length > 0)
    {
        yield return line.ToString();
    }
}

internal sealed record BucketMetrics(
    double Precision, double Recall, double F1, int Support, int Tp, int Fp, int Fn);

internal sealed record Report(
    double Accuracy,
    double PrecisionMacro,
    double RecallMacro,
    double F1Macro,
    double Coverage,
    Dictionary<string, BucketMetrics> PerBucket,
    Dictionary<string, Dictionary<string, int>> Confusion);
