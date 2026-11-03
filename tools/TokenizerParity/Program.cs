// Hito 0 de BACKEND.md §10: paridad de tokenizer entre Python y .NET.
//
// multilingual-e5-small usa el tokenizer de XLM-RoBERTa (SentencePiece, normalizador
// Precompiled). ONNX Runtime ejecuta el modelo sin problema; el riesgo es tokenizar
// distinto que la referencia. Si los IDs no coinciden, los embeddings salen mal y
// TODO lo que va detras -clasificador, busqueda vectorial- se degrada en silencio.
//
// Criterio de aceptacion: los 100 textos del corpus tokenizan identicos.
// Codigos de salida: 0 paridad, 1 divergencia, 2 problema de montaje.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tokenizers.DotNet;

Console.OutputEncoding = Encoding.UTF8;

// --- Localizar backend/ subiendo desde el ejecutable --------------------------

static DirectoryInfo? BuscarRaizBackend()
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

var raiz = BuscarRaizBackend();
if (raiz is null)
{
    Console.Error.WriteLine("no encuentro backend/ (con lab/ y models/) subiendo desde el ejecutable");
    return 2;
}

var rutaTokenizer = args.Length > 0
    ? args[0]
    : Path.Combine(raiz.FullName, "models", "multilingual-e5-small", "tokenizer.json");
var rutaReferencia = args.Length > 1
    ? args[1]
    : Path.Combine(raiz.FullName, "lab", "reference_ids.json");

foreach (var ruta in new[] { rutaTokenizer, rutaReferencia })
{
    if (!File.Exists(ruta))
    {
        Console.Error.WriteLine($"falta {ruta}");
        Console.Error.WriteLine("ejecuta antes: python backend/lab/build_corpus.py && python backend/lab/dump_reference.py");
        return 2;
    }
}

// --- El mismo artefacto en los dos lados -------------------------------------
// Si cada lado leyera su propio tokenizer.json la prueba no probaria nada.

using var fs = File.OpenRead(rutaTokenizer);
var sha = Convert.ToHexStringLower(SHA256.HashData(fs));

using var doc = JsonDocument.Parse(File.ReadAllBytes(rutaReferencia));
var raizJson = doc.RootElement;
var shaEsperado = raizJson.GetProperty("tokenizerJsonSha256").GetString();

Console.WriteLine($"modelo        : {raizJson.GetProperty("model").GetString()}");
Console.WriteLine($"referencia    : transformers {raizJson.GetProperty("transformersVersion").GetString()} / tokenizers {raizJson.GetProperty("tokenizersVersion").GetString()}");
Console.WriteLine($"normalizador  : {raizJson.GetProperty("normalizerType").GetString()}");
Console.WriteLine($"vocab         : {raizJson.GetProperty("vocabSize").GetInt32()}");
using var tok = new Tokenizer(rutaTokenizer);
Console.WriteLine($"Tokenizers.DotNet {tok.GetVersion()}");
Console.WriteLine();

if (!string.Equals(sha, shaEsperado, StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("ABORTADO: tokenizer.json no es el mismo fichero que uso Python.");
    Console.Error.WriteLine($"  C#     : {sha}");
    Console.Error.WriteLine($"  Python : {shaEsperado}");
    Console.Error.WriteLine("Comparar IDs de dos artefactos distintos no demuestra nada. Vuelve a ejecutar dump_reference.py.");
    return 2;
}
Console.WriteLine($"sha256 tokenizer.json coincide en ambos lados: {sha[..16]}...");
Console.WriteLine();

// --- Comparacion -------------------------------------------------------------


static string IdsInt(IEnumerable<int> v) => string.Join(" ", v);

// Recorta el texto para que un fallo quepa en una linea legible.
static string Breve(string s, int max = 60)
{
    var plano = s.Replace("\n", "\\n").Replace("\t", "\\t").Replace("\r", "\\r");
    return plano.Length <= max ? plano : plano[..max] + "...";
}

var conEspeciales = 0;
var sinEspeciales = 0;
var divergencias = new List<object>();
var errores = new List<object>();
var total = 0;

foreach (var item in raizJson.GetProperty("items").EnumerateArray())
{
    total++;
    var id = item.GetProperty("id").GetString()!;
    var texto = item.GetProperty("text").GetString()!;
    var esperadoCon = item.GetProperty("idsWithSpecial").EnumerateArray().Select(e => e.GetInt32()).ToArray();
    var esperadoSin = item.GetProperty("idsNoSpecial").EnumerateArray().Select(e => e.GetInt32()).ToArray();

    uint[] obtenido;
    try
    {
        obtenido = tok.Encode(texto) ?? [];
    }
    catch (Exception ex)
    {
        errores.Add(new { id, texto = Breve(texto), excepcion = ex.GetType().Name, mensaje = ex.Message });
        Console.WriteLine($"[ERROR ] {id}  excepcion al tokenizar: {ex.GetType().Name}: {ex.Message}");
        continue;
    }

    var comoInt = obtenido.Select(u => (int)u).ToArray();

    if (comoInt.SequenceEqual(esperadoCon))
    {
        conEspeciales++;
        continue;
    }
    if (comoInt.SequenceEqual(esperadoSin))
    {
        sinEspeciales++;
        continue;
    }

    // Divergencia real: localizar el primer token que se desvia, contra la
    // variante que mas se le parece (la que comparte el prefijo mas largo).
    static int PrefijoComun(int[] a, int[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < n && a[i] == b[i]) i++;
        return i;
    }

    var prefCon = PrefijoComun(comoInt, esperadoCon);
    var prefSin = PrefijoComun(comoInt, esperadoSin);
    var contra = prefCon >= prefSin ? "idsWithSpecial" : "idsNoSpecial";
    var esperado = prefCon >= prefSin ? esperadoCon : esperadoSin;
    var pos = Math.Max(prefCon, prefSin);

    var desde = Math.Max(0, pos - 3);
    var hastaEsp = Math.Min(esperado.Length, pos + 4);
    var hastaObt = Math.Min(comoInt.Length, pos + 4);

    divergencias.Add(new
    {
        id,
        texto,
        contra,
        primeraDivergencia = pos,
        esperadoLen = esperado.Length,
        obtenidoLen = comoInt.Length,
        esperado,
        obtenido = comoInt,
    });

    Console.WriteLine($"[DIVERGE] {id}  \"{Breve(texto)}\"");
    Console.WriteLine($"          contra {contra}, primer token distinto en indice {pos} (len esperado {esperado.Length}, obtenido {comoInt.Length})");
    Console.WriteLine($"          esperado[{desde}..{hastaEsp}] = {IdsInt(esperado[desde..hastaEsp])}");
    Console.WriteLine($"          obtenido[{desde}..{hastaObt}] = {IdsInt(comoInt[desde..hastaObt])}");
}

// --- Veredicto ---------------------------------------------------------------

Console.WriteLine();
Console.WriteLine($"textos                     : {total}");
Console.WriteLine($"identicos (con especiales) : {conEspeciales}");
Console.WriteLine($"identicos (sin especiales) : {sinEspeciales}");
Console.WriteLine($"divergencias               : {divergencias.Count}");
Console.WriteLine($"errores                    : {errores.Count}");

var informe = Path.Combine(raiz.FullName, "lab", "parity_report.json");
File.WriteAllText(informe, JsonSerializer.Serialize(new
{
    fecha = DateTime.UtcNow.ToString("O"),
    tokenizerJsonSha256 = sha,
    total,
    conEspeciales,
    sinEspeciales,
    divergencias,
    errores,
}, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
Console.WriteLine($"informe                    : {informe}");
Console.WriteLine();

if (divergencias.Count > 0 || errores.Count > 0)
{
    Console.WriteLine("HITO 0 NO SUPERADO. No sigas con embeddings: los vectores saldrian mal y el fallo es silencioso.");
    return 1;
}

// Un modo consistente en los 100. Mezclar modos seria igual de peligroso:
// significaria que los especiales entran o no segun el texto.
if (conEspeciales == total)
{
    Console.WriteLine("HITO 0 SUPERADO: los 100 textos coinciden, y Encode() anade los tokens especiales (<s> ... </s>)");
    Console.WriteLine("Consecuencia para el pipeline: NO vuelvas a anadirlos a mano al preparar el batch del ONNX.");
    return 0;
}
if (sinEspeciales == total)
{
    Console.WriteLine("HITO 0 SUPERADO: los 100 textos coinciden, pero Encode() NO anade los tokens especiales.");
    Console.WriteLine("Consecuencia para el pipeline: hay que envolver con <s> (0) y </s> (2) antes de pasar al ONNX.");
    return 0;
}

Console.WriteLine("HITO 0 NO SUPERADO: coinciden todos, pero unos CON especiales y otros SIN. Los especiales dependen del texto: inaceptable.");
return 1;
