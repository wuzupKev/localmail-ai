// Milestone 0 of BACKEND.md §10: tokenizer parity between Python and .NET.
//
// multilingual-e5-small uses the XLM-RoBERTa tokenizer (SentencePiece, Precompiled
// normaliser). ONNX Runtime runs the model without trouble; the risk is tokenising
// differently from the reference. If the ids do not match, the embeddings come out
// wrong and everything downstream - classifier, vector search - degrades silently.
//
// Acceptance: all 100 corpus texts tokenise identically.
// Exit codes: 0 parity, 1 divergence, 2 setup problem.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tokenizers.DotNet;

Console.OutputEncoding = Encoding.UTF8;

// --- Locate backend/ by walking up from the executable ------------------------

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
    Console.Error.WriteLine("cannot find backend/ (containing lab/ and models/) above the executable");
    return 2;
}

var tokenizerPath = args.Length > 0
    ? args[0]
    : Path.Combine(root.FullName, "models", "multilingual-e5-small", "tokenizer.json");
var referencePath = args.Length > 1
    ? args[1]
    : Path.Combine(root.FullName, "lab", "reference_ids.json");

foreach (var path in new[] { tokenizerPath, referencePath })
{
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"missing {path}");
        Console.Error.WriteLine("run first: python backend/lab/build_corpus.py && python backend/lab/dump_reference.py");
        return 2;
    }
}

// --- The same artefact on both sides -------------------------------------------
// If each side read its own tokenizer.json the test would prove nothing.

using var fs = File.OpenRead(tokenizerPath);
var sha = Convert.ToHexStringLower(SHA256.HashData(fs));

using var doc = JsonDocument.Parse(File.ReadAllBytes(referencePath));
var rootJson = doc.RootElement;
var expectedSha = rootJson.GetProperty("tokenizerJsonSha256").GetString();

Console.WriteLine($"model         : {rootJson.GetProperty("model").GetString()}");
Console.WriteLine($"reference     : transformers {rootJson.GetProperty("transformersVersion").GetString()} / tokenizers {rootJson.GetProperty("tokenizersVersion").GetString()}");
Console.WriteLine($"normaliser    : {rootJson.GetProperty("normalizerType").GetString()}");
Console.WriteLine($"vocab         : {rootJson.GetProperty("vocabSize").GetInt32()}");
using var tok = new Tokenizer(tokenizerPath);
Console.WriteLine($"Tokenizers.DotNet {tok.GetVersion()}");
Console.WriteLine();

if (!string.Equals(sha, expectedSha, StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("ABORTED: tokenizer.json is not the same file Python used.");
    Console.Error.WriteLine($"  C#     : {sha}");
    Console.Error.WriteLine($"  Python : {expectedSha}");
    Console.Error.WriteLine("Comparing ids from two different artefacts proves nothing. Re-run dump_reference.py.");
    return 2;
}
Console.WriteLine($"tokenizer.json sha256 matches on both sides: {sha[..16]}...");
Console.WriteLine();

// --- Comparison -----------------------------------------------------------------

static string IdsToString(IEnumerable<int> v) => string.Join(" ", v);

// Shorten the text so a failure fits on a readable line.
static string Brief(string s, int max = 60)
{
    var flat = s.Replace("\n", "\\n").Replace("\t", "\\t").Replace("\r", "\\r");
    return flat.Length <= max ? flat : flat[..max] + "...";
}

var withSpecials = 0;
var withoutSpecials = 0;
var divergences = new List<object>();
var errors = new List<object>();
var total = 0;

foreach (var item in rootJson.GetProperty("items").EnumerateArray())
{
    total++;
    var id = item.GetProperty("id").GetString()!;
    var text = item.GetProperty("text").GetString()!;
    var expectedWith = item.GetProperty("idsWithSpecial").EnumerateArray().Select(e => e.GetInt32()).ToArray();
    var expectedWithout = item.GetProperty("idsNoSpecial").EnumerateArray().Select(e => e.GetInt32()).ToArray();

    uint[] produced;
    try
    {
        produced = tok.Encode(text) ?? [];
    }
    catch (Exception ex)
    {
        errors.Add(new { id, text = Brief(text), exception = ex.GetType().Name, message = ex.Message });
        Console.WriteLine($"[ERROR ] {id}  exception while tokenising: {ex.GetType().Name}: {ex.Message}");
        continue;
    }

    var asInt = produced.Select(u => (int)u).ToArray();

    if (asInt.SequenceEqual(expectedWith))
    {
        withSpecials++;
        continue;
    }
    if (asInt.SequenceEqual(expectedWithout))
    {
        withoutSpecials++;
        continue;
    }

    // A real divergence: locate the first token that differs, against whichever
    // variant it resembles most (the one sharing the longest prefix).
    static int CommonPrefix(int[] a, int[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < n && a[i] == b[i]) i++;
        return i;
    }

    var prefixWith = CommonPrefix(asInt, expectedWith);
    var prefixWithout = CommonPrefix(asInt, expectedWithout);
    var against = prefixWith >= prefixWithout ? "idsWithSpecial" : "idsNoSpecial";
    var expected = prefixWith >= prefixWithout ? expectedWith : expectedWithout;
    var at = Math.Max(prefixWith, prefixWithout);

    var from = Math.Max(0, at - 3);
    var toExpected = Math.Min(expected.Length, at + 4);
    var toProduced = Math.Min(asInt.Length, at + 4);

    divergences.Add(new
    {
        id,
        text,
        against,
        firstDivergence = at,
        expectedLength = expected.Length,
        producedLength = asInt.Length,
        expected,
        produced = asInt,
    });

    Console.WriteLine($"[DIVERGE] {id}  \"{Brief(text)}\"");
    Console.WriteLine($"          against {against}, first differing token at index {at} (expected length {expected.Length}, produced {asInt.Length})");
    Console.WriteLine($"          expected[{from}..{toExpected}] = {IdsToString(expected[from..toExpected])}");
    Console.WriteLine($"          produced[{from}..{toProduced}] = {IdsToString(asInt[from..toProduced])}");
}

// --- Verdict ----------------------------------------------------------------------

Console.WriteLine();
Console.WriteLine($"texts                      : {total}");
Console.WriteLine($"identical (with specials)  : {withSpecials}");
Console.WriteLine($"identical (without)        : {withoutSpecials}");
Console.WriteLine($"divergences                : {divergences.Count}");
Console.WriteLine($"errors                     : {errors.Count}");

var report = Path.Combine(root.FullName, "lab", "parity_report.json");
File.WriteAllText(report, JsonSerializer.Serialize(new
{
    date = DateTime.UtcNow.ToString("O"),
    tokenizerJsonSha256 = sha,
    total,
    withSpecials,
    withoutSpecials,
    divergences,
    errors,
}, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
Console.WriteLine($"report                     : {report}");
Console.WriteLine();

if (divergences.Count > 0 || errors.Count > 0)
{
    Console.WriteLine("MILESTONE 0 FAILED. Do not proceed to embeddings: the vectors would be wrong and the failure is silent.");
    return 1;
}

// One consistent mode across all 100. A mix would be just as dangerous: it would mean
// the special tokens appear or not depending on the text.
if (withSpecials == total)
{
    Console.WriteLine("MILESTONE 0 PASSED: all 100 texts match, and Encode() adds the special tokens (<s> ... </s>)");
    Console.WriteLine("Consequence for the pipeline: do NOT add them again when building the ONNX batch.");
    return 0;
}
if (withoutSpecials == total)
{
    Console.WriteLine("MILESTONE 0 PASSED: all 100 texts match, but Encode() does NOT add the special tokens.");
    Console.WriteLine("Consequence for the pipeline: wrap with <s> (0) and </s> (2) before the ONNX call.");
    return 0;
}

Console.WriteLine("MILESTONE 0 FAILED: all match, but some with specials and some without. Specials depending on the text is unacceptable.");
return 1;
