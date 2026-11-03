// Paridad de embeddings entre Python y .NET (hito 6).
//
// El hito 0 demostro que la tokenizacion coincide. Esto comprueba lo que viene
// despues: que el mean pooling con mascara, la normalizacion L2 y los prefijos de e5
// dan el MISMO vector que la referencia.
//
// Ninguno de esos tres pasos falla con una excepcion si se hace mal: dan vectores
// plausibles y peores, y el clasificador y la busqueda se degradan sin que nada avise.
// Por eso se compara, igual que con el tokenizer.
//
// Codigos de salida: 0 paridad, 1 divergencia, 2 problema de montaje.

using System.Text;
using System.Text.Json;
using LocalMailAI.Api.Embeddings;
using Microsoft.Extensions.Logging.Abstractions;

Console.OutputEncoding = Encoding.UTF8;

// Tolerancia: ONNX Runtime no garantiza bit a bit entre plataformas ni entre versiones
// -el orden de las sumas en coma flotante cambia con la vectorizacion-, asi que se
// exige coseno practicamente 1 y una diferencia maxima por componente pequena. Una
// divergencia de verdad (pooling mal, prefijo olvidado, normalizacion ausente) no se
// cuela por aqui: baja el coseno a 0,9 o menos, no a 0,999999.
const double CosenoMinimo = 0.99999;
const double MaxDiferenciaComponente = 1e-4;

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
    Console.Error.WriteLine("no encuentro backend/ (con lab/ y models/)");
    return 2;
}

var referencia = Path.Combine(raiz.FullName, "lab", "reference_embeddings.json");
if (!File.Exists(referencia))
{
    Console.Error.WriteLine($"falta {referencia}");
    Console.Error.WriteLine("ejecuta antes: python backend/lab/dump_embeddings.py");
    return 2;
}

using var embedder = E5Embedder.TryLoad(
    Path.Combine(raiz.FullName, "models", "multilingual-e5-small"),
    NullLogger<E5Embedder>.Instance);

if (embedder is null)
{
    Console.Error.WriteLine("el modelo de embeddings no esta disponible");
    return 2;
}

// --rank "consulta": ordena los correos de fixtures/ por parecido semantico.
// Comprueba algo que la paridad no comprueba: que el modelo sea UTIL, no solo igual.
var indiceRank = Array.IndexOf(args, "--rank");
if (indiceRank >= 0 && indiceRank + 1 < args.Length)
{
    return Rankear(embedder, Path.Combine(raiz.FullName, "fixtures"), args[indiceRank + 1]);
}

using var doc = JsonDocument.Parse(File.ReadAllBytes(referencia));
var raizJson = doc.RootElement;

Console.WriteLine($"modelo      : {raizJson.GetProperty("model").GetString()}");
Console.WriteLine($"referencia  : onnxruntime {raizJson.GetProperty("onnxruntimeVersion").GetString()}");
Console.WriteLine($"pooling     : {raizJson.GetProperty("pooling").GetString()}");
Console.WriteLine();

var divergencias = 0;
var total = 0;
var cosenoMinimoVisto = 1.0;

foreach (var item in raizJson.GetProperty("items").EnumerateArray())
{
    total++;
    var texto = item.GetProperty("text").GetString()!;
    var esperado = item.GetProperty("vector").EnumerateArray().Select(v => (float)v.GetDouble()).ToArray();

    // El texto de la referencia YA lleva el prefijo, asi que aqui hay que llamar al
    // metodo que no lo anade otra vez. Se parte el prefijo y se usa el metodo que
    // corresponde: asi tambien se comprueba que los dos prefijan igual.
    float[] obtenido;
    if (texto.StartsWith("query: ", StringComparison.Ordinal))
    {
        obtenido = embedder.EmbedQuery(texto["query: ".Length..]);
    }
    else if (texto.StartsWith("passage: ", StringComparison.Ordinal))
    {
        obtenido = embedder.EmbedPassage(texto["passage: ".Length..]);
    }
    else
    {
        Console.Error.WriteLine($"el texto de referencia no lleva prefijo de e5: {texto[..40]}");
        return 2;
    }

    if (obtenido.Length != esperado.Length)
    {
        Console.WriteLine($"[DIVERGE] dimension {obtenido.Length}, esperada {esperado.Length}");
        divergencias++;
        continue;
    }

    var coseno = (double)Pooling.Cosine(obtenido, esperado);
    var maxDif = 0.0;
    var peorIndice = 0;

    for (var i = 0; i < esperado.Length; i++)
    {
        var dif = Math.Abs(obtenido[i] - esperado[i]);
        if (dif > maxDif)
        {
            maxDif = dif;
            peorIndice = i;
        }
    }

    cosenoMinimoVisto = Math.Min(cosenoMinimoVisto, coseno);

    var ok = coseno >= CosenoMinimo && maxDif <= MaxDiferenciaComponente;
    var etiqueta = ok ? "ok      " : "DIVERGE ";

    Console.WriteLine($"[{etiqueta}] coseno {coseno:F8}  maxdif {maxDif:E2} (comp. {peorIndice})  {Recortar(texto, 44)}");

    if (!ok)
    {
        divergencias++;
        Console.WriteLine($"            esperado[{peorIndice}] = {esperado[peorIndice]:F8}");
        Console.WriteLine($"            obtenido[{peorIndice}] = {obtenido[peorIndice]:F8}");
    }
}

// Las relaciones entre vectores importan mas que los numeros sueltos: si los cosenos
// entre pares se mantienen, el modelo hace lo que tiene que hacer aunque la ultima
// cifra decimal no coincida.
Console.WriteLine();
Console.WriteLine("cosenos entre pares (lo que de verdad usa la busqueda):");

var items = raizJson.GetProperty("items").EnumerateArray().ToList();
var esperados = raizJson.GetProperty("expectedCosines");

var vectores = items.Select(it =>
{
    var t = it.GetProperty("text").GetString()!;
    return t.StartsWith("query: ", StringComparison.Ordinal)
        ? embedder.EmbedQuery(t["query: ".Length..])
        : embedder.EmbedPassage(t["passage: ".Length..]);
}).ToList();

var pares = new (string Nombre, int A, int B)[]
{
    ("reunion_query_vs_passage", 1, 0),
    ("factura_query_vs_passage", 3, 2),
    ("factura_query_vs_newsletter", 3, 4),
};

foreach (var (nombre, a, b) in pares)
{
    var mio = (double)Pooling.Cosine(vectores[a], vectores[b]);
    var suyo = esperados.GetProperty(nombre).GetDouble();
    var dif = Math.Abs(mio - suyo);
    var ok = dif <= 1e-4;

    Console.WriteLine($"  {(ok ? "ok     " : "DIVERGE")} {nombre,-30} C# {mio:F4}  Python {suyo:F4}  dif {dif:E2}");

    if (!ok)
    {
        divergencias++;
    }
}

Console.WriteLine();
Console.WriteLine($"vectores comparados : {total}");
Console.WriteLine($"coseno minimo visto : {cosenoMinimoVisto:F8}");
Console.WriteLine($"divergencias        : {divergencias}");
Console.WriteLine();

if (divergencias > 0)
{
    Console.WriteLine("PARIDAD DE EMBEDDINGS NO SUPERADA. Revisa, por orden de probabilidad:");
    Console.WriteLine("  1. el mean pooling: ¿pondera por la mascara, o promedia tambien el relleno?");
    Console.WriteLine("  2. la normalizacion L2: ¿se aplica al final?");
    Console.WriteLine("  3. los prefijos: ¿'query: ' y 'passage: ' en el lado correcto?");
    Console.WriteLine("  4. el recorte a 512 tokens: ¿conserva el </s> final?");
    return 1;
}

Console.WriteLine("PARIDAD DE EMBEDDINGS SUPERADA: el post-procesado de C# coincide con la referencia.");
return 0;

static string Recortar(string s, int max) =>
    s.Length <= max ? s : s[..max] + "...";

static int Rankear(E5Embedder embedder, string carpeta, string consulta)
{
    if (!Directory.Exists(carpeta))
    {
        Console.Error.WriteLine($"no encuentro {carpeta}");
        return 2;
    }

    var ficheros = Directory.GetFiles(carpeta, "*.eml").OrderBy(f => f, StringComparer.Ordinal).ToList();
    var cuerpos = new List<(string Nombre, string Asunto, string Texto)>();

    foreach (var fichero in ficheros)
    {
        var mensaje = MimeKit.MimeMessage.Load(fichero);
        var cuerpo = LocalMailAI.Api.Mail.BodyNormalizer.FromMessage(mensaje);

        // Asunto + cuerpo limpio: es lo que el indice guardaria de verdad.
        var texto = mensaje.Subject + "\n" + cuerpo.Clean;
        cuerpos.Add((Path.GetFileName(fichero), mensaje.Subject ?? "(sin asunto)", texto));
    }

    var cronometro = System.Diagnostics.Stopwatch.StartNew();
    var vectores = embedder.EmbedPassages([.. cuerpos.Select(c => c.Texto)]);
    var msEmbeber = cronometro.ElapsedMilliseconds;

    cronometro.Restart();
    var vectorConsulta = embedder.EmbedQuery(consulta);
    var msConsulta = cronometro.ElapsedMilliseconds;

    var ordenados = cuerpos
        .Select((c, i) => (c.Nombre, c.Asunto, Score: Pooling.Cosine(vectorConsulta, vectores[i])))
        .OrderByDescending(x => x.Score)
        .ToList();

    Console.WriteLine($"consulta: \"{consulta}\"");
    Console.WriteLine($"{cuerpos.Count} correos embebidos en {msEmbeber} ms ({msEmbeber / (double)cuerpos.Count:F0} ms/correo), consulta en {msConsulta} ms");
    Console.WriteLine();

    foreach (var (nombre, asunto, score) in ordenados.Take(6))
    {
        Console.WriteLine($"  {score:F4}  {Recortar(asunto, 52)}");
    }

    Console.WriteLine("  ...");
    foreach (var (nombre, asunto, score) in ordenados.TakeLast(2))
    {
        Console.WriteLine($"  {score:F4}  {Recortar(asunto, 52)}");
    }

    return 0;
}
