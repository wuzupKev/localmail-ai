// Embedding parity between Python and .NET (milestone 6).
//
// Milestone 0 proved tokenisation matches. This checks what comes after: that
// mask-weighted mean pooling, L2 normalisation and the e5 prefixes produce the SAME
// vector as the reference.
//
// None of those three steps throws when done wrong: they return plausible, worse
// vectors, and the classifier and search degrade with nothing to warn anyone. Hence
// the comparison, exactly as with the tokenizer.
//
// Exit codes: 0 parity, 1 divergence, 2 setup problem.

using System.Text;
using System.Text.Json;
using LocalMailAI.Api.Embeddings;
using Microsoft.Extensions.Logging.Abstractions;

Console.OutputEncoding = Encoding.UTF8;

// Tolerance: ONNX Runtime does not guarantee bit-identical results across platforms
// or versions - the order of floating point sums changes with vectorisation - so the
// bar is a cosine of essentially 1 plus a small per-component difference. A real
// divergence (bad pooling, a forgotten prefix, missing normalisation) does not slip
// through: it drops the cosine to 0.9 or less, not to 0.999999.
const double MinimumCosine = 0.99999;
const double MaxComponentDifference = 1e-4;

static DirectoryInfo? FindBackendRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, "lab")) &&
            Directory.Exists(Path.Combine(dir.FullName, "models")))
        {
            return dir;
        }
        dir = dir.Parent;
    }
    return null;
}

var root = FindBackendRoot();
if (root is null)
{
    Console.Error.WriteLine("cannot find backend/ (containing lab/ and models/)");
    return 2;
}

var referencePath = Path.Combine(root.FullName, "lab", "reference_embeddings.json");
if (!File.Exists(referencePath))
{
    Console.Error.WriteLine($"missing {referencePath}");
    Console.Error.WriteLine("run first: python backend/lab/dump_embeddings.py");
    return 2;
}

using var embedder = E5Embedder.TryLoad(
    Path.Combine(root.FullName, "models", "multilingual-e5-small"),
    NullLogger<E5Embedder>.Instance);

if (embedder is null)
{
    Console.Error.WriteLine("the embedding model is not available");
    return 2;
}

// --rank "query": ranks the messages in fixtures/ by semantic similarity. It checks
// something parity does not: that the model is USEFUL, not merely identical.
var rankIndex = Array.IndexOf(args, "--rank");
if (rankIndex >= 0 && rankIndex + 1 < args.Length)
{
    return Rank(embedder, Path.Combine(root.FullName, "fixtures"), args[rankIndex + 1]);
}

using var doc = JsonDocument.Parse(File.ReadAllBytes(referencePath));
var rootJson = doc.RootElement;

Console.WriteLine($"model       : {rootJson.GetProperty("model").GetString()}");
Console.WriteLine($"reference   : onnxruntime {rootJson.GetProperty("onnxruntimeVersion").GetString()}");
Console.WriteLine($"pooling     : {rootJson.GetProperty("pooling").GetString()}");
Console.WriteLine();

var divergences = 0;
var total = 0;
var lowestCosine = 1.0;

foreach (var item in rootJson.GetProperty("items").EnumerateArray())
{
    total++;
    var text = item.GetProperty("text").GetString()!;
    var expected = item.GetProperty("vector").EnumerateArray().Select(v => (float)v.GetDouble()).ToArray();

    // The reference text ALREADY carries the prefix, so the method that would add it
    // again must not be used. The prefix is split off and the matching method called,
    // which also checks that both sides prefix the same way.
    float[] produced;
    if (text.StartsWith("query: ", StringComparison.Ordinal))
    {
        produced = embedder.EmbedQuery(text["query: ".Length..]);
    }
    else if (text.StartsWith("passage: ", StringComparison.Ordinal))
    {
        produced = embedder.EmbedPassage(text["passage: ".Length..]);
    }
    else
    {
        Console.Error.WriteLine($"the reference text carries no e5 prefix: {text[..40]}");
        return 2;
    }

    if (produced.Length != expected.Length)
    {
        Console.WriteLine($"[DIVERGE] dimension {produced.Length}, expected {expected.Length}");
        divergences++;
        continue;
    }

    var cosine = (double)Pooling.Cosine(produced, expected);
    var maxDifference = 0.0;
    var worstIndex = 0;

    for (var i = 0; i < expected.Length; i++)
    {
        var difference = Math.Abs(produced[i] - expected[i]);
        if (difference > maxDifference)
        {
            maxDifference = difference;
            worstIndex = i;
        }
    }

    lowestCosine = Math.Min(lowestCosine, cosine);

    var ok = cosine >= MinimumCosine && maxDifference <= MaxComponentDifference;
    var label = ok ? "ok      " : "DIVERGE ";

    Console.WriteLine($"[{label}] cosine {cosine:F8}  maxdiff {maxDifference:E2} (component {worstIndex})  {Shorten(text, 44)}");

    if (!ok)
    {
        divergences++;
        Console.WriteLine($"            expected[{worstIndex}] = {expected[worstIndex]:F8}");
        Console.WriteLine($"            produced[{worstIndex}] = {produced[worstIndex]:F8}");
    }
}

// Relationships between vectors matter more than individual numbers: if the pairwise
// cosines hold, the model is doing its job even if the last decimal differs.
Console.WriteLine();
Console.WriteLine("pairwise cosines (what search actually uses):");

var items = rootJson.GetProperty("items").EnumerateArray().ToList();
var expectedCosines = rootJson.GetProperty("expectedCosines");

var vectors = items.Select(it =>
{
    var t = it.GetProperty("text").GetString()!;
    return t.StartsWith("query: ", StringComparison.Ordinal)
        ? embedder.EmbedQuery(t["query: ".Length..])
        : embedder.EmbedPassage(t["passage: ".Length..]);
}).ToList();

var pairs = new (string Name, int A, int B)[]
{
    ("reunion_query_vs_passage", 1, 0),
    ("factura_query_vs_passage", 3, 2),
    ("factura_query_vs_newsletter", 3, 4),
};

foreach (var (name, a, b) in pairs)
{
    var mine = (double)Pooling.Cosine(vectors[a], vectors[b]);
    var theirs = expectedCosines.GetProperty(name).GetDouble();
    var difference = Math.Abs(mine - theirs);
    var ok = difference <= 1e-4;

    Console.WriteLine($"  {(ok ? "ok     " : "DIVERGE")} {name,-30} C# {mine:F4}  Python {theirs:F4}  diff {difference:E2}");

    if (!ok)
    {
        divergences++;
    }
}

Console.WriteLine();
Console.WriteLine($"vectors compared : {total}");
Console.WriteLine($"lowest cosine    : {lowestCosine:F8}");
Console.WriteLine($"divergences      : {divergences}");
Console.WriteLine();

if (divergences > 0)
{
    Console.WriteLine("EMBEDDING PARITY FAILED. Check, in order of likelihood:");
    Console.WriteLine("  1. mean pooling: is it weighted by the mask, or does it average the padding too?");
    Console.WriteLine("  2. L2 normalisation: is it applied at the end?");
    Console.WriteLine("  3. the prefixes: are 'query: ' and 'passage: ' on the right sides?");
    Console.WriteLine("  4. truncation to 512 tokens: does it keep the trailing </s>?");
    return 1;
}

Console.WriteLine("EMBEDDING PARITY PASSED: the C# post-processing matches the reference.");
return 0;

static string Shorten(string s, int max) =>
    s.Length <= max ? s : s[..max] + "...";

static int Rank(E5Embedder embedder, string folder, string query)
{
    if (!Directory.Exists(folder))
    {
        Console.Error.WriteLine($"cannot find {folder}");
        return 2;
    }

    var files = Directory.GetFiles(folder, "*.eml").OrderBy(f => f, StringComparer.Ordinal).ToList();
    var bodies = new List<(string File, string Subject, string Text)>();

    foreach (var file in files)
    {
        var message = MimeKit.MimeMessage.Load(file);
        var body = LocalMailAI.Api.Mail.BodyNormalizer.FromMessage(message);

        // Subject plus clean body: this is what the index would actually store.
        var text = message.Subject + "\n" + body.Clean;
        bodies.Add((Path.GetFileName(file), message.Subject ?? "(no subject)", text));
    }

    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    var vectors = embedder.EmbedPassages([.. bodies.Select(b => b.Text)]);
    var msEmbedding = stopwatch.ElapsedMilliseconds;

    stopwatch.Restart();
    var queryVector = embedder.EmbedQuery(query);
    var msQuery = stopwatch.ElapsedMilliseconds;

    var ranked = bodies
        .Select((b, i) => (b.File, b.Subject, Score: Pooling.Cosine(queryVector, vectors[i])))
        .OrderByDescending(x => x.Score)
        .ToList();

    Console.WriteLine($"query: \"{query}\"");
    Console.WriteLine($"{bodies.Count} messages embedded in {msEmbedding} ms ({msEmbedding / (double)bodies.Count:F0} ms each), query in {msQuery} ms");
    Console.WriteLine();

    foreach (var (file, subject, score) in ranked.Take(6))
    {
        Console.WriteLine($"  {score:F4}  {Shorten(subject, 52)}");
    }

    Console.WriteLine("  ...");
    foreach (var (file, subject, score) in ranked.TakeLast(2))
    {
        Console.WriteLine($"  {score:F4}  {Shorten(subject, 52)}");
    }

    return 0;
}
