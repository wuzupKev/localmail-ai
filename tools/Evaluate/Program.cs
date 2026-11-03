// Hito 5 de BACKEND.md §9: set de evaluacion y metricas por bucket.
//
// "El hito 5 no es opcional ni posterior: sin set de evaluacion no puedes saber si el
// LLM aporta algo sobre las heuristicas, y estaras optimizando a ciegas."
//
// Esta herramienta es el instrumento. Ingiere una carpeta de .eml en una base de datos
// desechable, compara la clasificacion del sistema con las etiquetas de un fichero, y
// guarda el resultado en eval_runs de la base real para que /api/model/status pueda
// servir una precision medida en vez de una constante.
//
// Uso:
//   dotnet run --project tools/Evaluate -- --emails fixtures --labels eval/fixtures-18.json
//   ... --record-to <ruta.db>     donde anotar la pasada (por defecto, la BD del producto)
//   ... --no-record               solo medir, sin guardar

using System.Text;
using System.Text.Json;
using Dapper;
using LocalMailAI.Api.Classification;
using LocalMailAI.Api.Data;
using LocalMailAI.Api.Mail;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;

Console.OutputEncoding = Encoding.UTF8;

var argumentos = LeerArgumentos(args);

if (!argumentos.TryGetValue("emails", out var carpetaCorreos) ||
    !argumentos.TryGetValue("labels", out var ficheroEtiquetas))
{
    Console.Error.WriteLine("uso: --emails <carpeta .eml> --labels <fichero .json> [--record-to <ruta.db> | --no-record]");
    return 2;
}

if (!Directory.Exists(carpetaCorreos))
{
    Console.Error.WriteLine($"la carpeta {carpetaCorreos} no existe");
    return 2;
}

if (!File.Exists(ficheroEtiquetas))
{
    Console.Error.WriteLine($"el fichero de etiquetas {ficheroEtiquetas} no existe");
    return 2;
}

// --- Etiquetas ---------------------------------------------------------------

using var docEtiquetas = JsonDocument.Parse(File.ReadAllBytes(ficheroEtiquetas));
var raiz = docEtiquetas.RootElement;
var nombreSet = raiz.GetProperty("set").GetString() ?? "sin-nombre";

// Por defecto NO representativo: hay que declararlo a proposito. Si el valor por
// defecto fuera true, un set de pruebas acabaria en la interfaz del usuario por
// olvido, que es la clase de error que nadie detecta.
var representativo = raiz.TryGetProperty("representativo", out var rep) && rep.GetBoolean();

var etiquetas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
foreach (var e in raiz.GetProperty("labels").EnumerateArray())
{
    etiquetas[e.GetProperty("file").GetString()!] = e.GetProperty("bucket").GetString()!;
}

Console.WriteLine($"set        : {nombreSet} ({etiquetas.Count} etiquetas, {(representativo ? "representativo" : "NO representativo: no llega a la interfaz")})");

if (raiz.TryGetProperty("aviso", out var aviso))
{
    Console.WriteLine();
    Console.WriteLine("AVISO DEL SET:");
    foreach (var linea in Envolver(aviso.GetString() ?? string.Empty, 76))
    {
        Console.WriteLine($"  {linea}");
    }
    Console.WriteLine();
}

// --- Ingesta en una base desechable ------------------------------------------
// Nunca sobre la base del usuario: medir no debe tocar sus datos.

var rutaTemporal = Path.Combine(Path.GetTempPath(), $"localmail-eval-{Guid.NewGuid():N}.db");
var db = new Database(rutaTemporal);
var ingestor = new EmailIngestor(NullLogger<EmailIngestor>.Instance);

try
{
    using (var cn = db.Open())
    {
        new Migrator(NullLogger<Migrator>.Instance).Migrate(cn);

        await cn.ExecuteAsync(
            """
            INSERT INTO accounts (id, email, imap_host, imap_port, credential_key, created_at)
            VALUES ('eval', 'kevin@sagetasolutions.com', 'localhost', 0, 'ninguna-evaluacion', @ahora)
            """,
            new { ahora = DateTimeOffset.UtcNow.ToString("O") });

        // En orden de nombre, que es el orden cronologico de las fixtures: el estado
        // del hilo depende de en que orden entran los mensajes, igual que en un sync.
        var ficheros = Directory.GetFiles(carpetaCorreos, "*.eml")
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        var sinEtiqueta = new List<string>();
        var predicciones = new List<(string Fichero, string Esperado, string? Obtenido, double Confianza)>();

        foreach (var fichero in ficheros)
        {
            var nombre = Path.GetFileName(fichero);

            if (!etiquetas.TryGetValue(nombre, out var esperado))
            {
                sinEtiqueta.Add(nombre);
                continue;
            }

            var mensaje = await MimeMessage.LoadAsync(fichero);
            var r = await ingestor.IngestAsync(cn, "eval", mensaje, 0, "INBOX", new FileInfo(fichero).Length);

            var fila = await cn.QuerySingleAsync<(string Bucket, double Confidence)>(
                "SELECT bucket, confidence FROM classifications WHERE email_id = @id",
                new { id = r.EmailId });

            predicciones.Add((nombre, esperado, fila.Bucket, fila.Confidence));
        }

        if (sinEtiqueta.Count > 0)
        {
            Console.WriteLine($"ADVERTENCIA: {sinEtiqueta.Count} correos sin etiqueta, excluidos de la medicion:");
            foreach (var n in sinEtiqueta)
            {
                Console.WriteLine($"  {n}");
            }
            Console.WriteLine();
        }

        var faltantes = etiquetas.Keys.Except(predicciones.Select(p => p.Fichero), StringComparer.OrdinalIgnoreCase).ToList();
        if (faltantes.Count > 0)
        {
            Console.WriteLine($"ADVERTENCIA: {faltantes.Count} etiquetas sin correo correspondiente:");
            foreach (var n in faltantes)
            {
                Console.WriteLine($"  {n}");
            }
            Console.WriteLine();
        }

        if (predicciones.Count == 0)
        {
            Console.Error.WriteLine("no hay ningun correo etiquetado que medir");
            return 2;
        }

        var informe = Medir(predicciones);
        Imprimir(informe, predicciones);

        // --- Anotar la pasada --------------------------------------------------

        if (argumentos.ContainsKey("no-record"))
        {
            Console.WriteLine("pasada NO anotada (--no-record)");
            return 0;
        }

        var rutaDestino = argumentos.TryGetValue("record-to", out var r2) && !string.IsNullOrWhiteSpace(r2)
            ? r2
            : Database.ResolveDefaultPath(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

        var destino = new Database(rutaDestino);
        using var cnDestino = destino.Open();
        new Migrator(NullLogger<Migrator>.Instance).Migrate(cnDestino);

        await cnDestino.ExecuteAsync(
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
                // 0 = solo senales deterministas. Es la linea base que el hito 6
                // tiene que superar; si no la supera, el modelo no aporta.
                modelVersion = 0,
                evalSet = nombreSet,
                setSize = predicciones.Count,
                representative = representativo ? 1 : 0,
                accuracy = informe.Accuracy,
                precisionMacro = informe.PrecisionMacro,
                recallMacro = informe.RecallMacro,
                f1Macro = informe.F1Macro,
                coverage = informe.Coverage,
                perBucketJson = JsonSerializer.Serialize(informe.PorBucket),
                confusionJson = JsonSerializer.Serialize(informe.Confusion),
            });

        Console.WriteLine($"pasada anotada en {rutaDestino}");
    }
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    foreach (var f in new[] { rutaTemporal, rutaTemporal + "-wal", rutaTemporal + "-shm" })
    {
        if (File.Exists(f))
        {
            File.Delete(f);
        }
    }
}

return 0;

// --- Metricas ----------------------------------------------------------------

static Informe Medir(List<(string Fichero, string Esperado, string? Obtenido, double Confianza)> predicciones)
{
    var buckets = new[] { "responder", "informativo", "automatizado", "ruido" };

    // Un correo por debajo del umbral NO es una prediccion: el sistema ha dicho que no
    // sabe. Contarlo como fallo castigaria la prudencia, y contarlo como acierto la
    // premiaria. Sale de precision y recall, y se mide aparte como cobertura.
    var comprometidas = predicciones.Where(p => p.Confianza >= SignalBaseline.ConfidenceThreshold).ToList();
    var cobertura = (double)comprometidas.Count / predicciones.Count;

    var aciertos = comprometidas.Count(p => p.Obtenido == p.Esperado);
    var accuracy = comprometidas.Count == 0 ? 0 : (double)aciertos / comprometidas.Count;

    var porBucket = new Dictionary<string, MetricasBucket>();

    foreach (var bucket in buckets)
    {
        var tp = comprometidas.Count(p => p.Obtenido == bucket && p.Esperado == bucket);
        var fp = comprometidas.Count(p => p.Obtenido == bucket && p.Esperado != bucket);
        var fn = comprometidas.Count(p => p.Obtenido != bucket && p.Esperado == bucket);

        var precision = tp + fp == 0 ? 0 : (double)tp / (tp + fp);
        var recall = tp + fn == 0 ? 0 : (double)tp / (tp + fn);
        var f1 = precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);

        // Soporte segun las etiquetas, no segun lo que el sistema predijo: incluye los
        // correos de ese bucket que quedaron bajo el umbral.
        var soporte = predicciones.Count(p => p.Esperado == bucket);

        porBucket[bucket] = new MetricasBucket(precision, recall, f1, soporte, tp, fp, fn);
    }

    // Macro y no micro: los cuatro buckets tienen tamanos muy distintos y la micro
    // quedaria dominada por el mayoritario, escondiendo que el sistema falla justo en
    // el cajon pequeno. Solo cuentan los buckets con algun correo etiquetado: promediar
    // un bucket vacio como 0 hundiria la media sin que signifique nada.
    var conSoporte = porBucket.Where(kv => kv.Value.Soporte > 0).Select(kv => kv.Value).ToList();

    var confusion = new Dictionary<string, Dictionary<string, int>>();
    foreach (var esperado in buckets)
    {
        var fila = new Dictionary<string, int>();
        foreach (var obtenido in buckets)
        {
            fila[obtenido] = comprometidas.Count(p => p.Esperado == esperado && p.Obtenido == obtenido);
        }
        // Columna extra: los que el sistema no se atrevio a clasificar.
        fila["duda"] = predicciones.Count(p =>
            p.Esperado == esperado && p.Confianza < SignalBaseline.ConfidenceThreshold);
        confusion[esperado] = fila;
    }

    return new Informe(
        accuracy,
        conSoporte.Count == 0 ? 0 : conSoporte.Average(m => m.Precision),
        conSoporte.Count == 0 ? 0 : conSoporte.Average(m => m.Recall),
        conSoporte.Count == 0 ? 0 : conSoporte.Average(m => m.F1),
        cobertura,
        porBucket,
        confusion);
}

static void Imprimir(Informe i, List<(string Fichero, string Esperado, string? Obtenido, double Confianza)> predicciones)
{
    Console.WriteLine($"correos medidos          : {predicciones.Count}");
    Console.WriteLine($"cobertura                : {i.Coverage:P0}  (el resto quedo bajo el umbral {SignalBaseline.ConfidenceThreshold})");
    Console.WriteLine($"acierto sobre lo cubierto: {i.Accuracy:P0}");
    Console.WriteLine($"precision macro          : {i.PrecisionMacro:F3}");
    Console.WriteLine($"recall macro             : {i.RecallMacro:F3}");
    Console.WriteLine($"F1 macro                 : {i.F1Macro:F3}");
    Console.WriteLine();

    Console.WriteLine("por bucket:");
    Console.WriteLine("  bucket         prec.  recall     F1   soporte   TP  FP  FN");
    foreach (var (bucket, m) in i.PorBucket)
    {
        Console.WriteLine($"  {bucket,-13} {m.Precision,6:F2} {m.Recall,7:F2} {m.F1,6:F2} {m.Soporte,9} {m.Tp,4} {m.Fp,3} {m.Fn,3}");
    }
    Console.WriteLine();

    Console.WriteLine("matriz de confusion (filas = esperado, columnas = obtenido):");
    Console.WriteLine("                  respond.  inform.  automat.   ruido    duda");
    foreach (var (esperado, fila) in i.Confusion)
    {
        Console.Write($"  {esperado,-14}");
        foreach (var col in new[] { "responder", "informativo", "automatizado", "ruido", "duda" })
        {
            Console.Write($"{fila[col],9}");
        }
        Console.WriteLine();
    }
    Console.WriteLine();

    var fallos = predicciones
        .Where(p => p.Confianza >= SignalBaseline.ConfidenceThreshold && p.Obtenido != p.Esperado)
        .ToList();

    if (fallos.Count > 0)
    {
        Console.WriteLine($"fallos ({fallos.Count}):");
        foreach (var f in fallos)
        {
            Console.WriteLine($"  {f.Fichero,-32} esperado {f.Esperado,-13} obtenido {f.Obtenido,-13} ({f.Confianza:F2})");
        }
        Console.WriteLine();
    }

    var dudosos = predicciones.Where(p => p.Confianza < SignalBaseline.ConfidenceThreshold).ToList();
    if (dudosos.Count > 0)
    {
        Console.WriteLine($"sin pronunciarse ({dudosos.Count}):");
        foreach (var d in dudosos)
        {
            Console.WriteLine($"  {d.Fichero,-32} esperado {d.Esperado,-13} habria dicho {d.Obtenido,-13} ({d.Confianza:F2})");
        }
        Console.WriteLine();
    }
}

static Dictionary<string, string> LeerArgumentos(string[] args)
{
    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }

        var clave = args[i][2..];
        var valor = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[++i]
            : string.Empty;

        d[clave] = valor;
    }

    return d;
}

static IEnumerable<string> Envolver(string texto, int ancho)
{
    var linea = new StringBuilder();

    foreach (var palabra in texto.Split(' '))
    {
        if (linea.Length + palabra.Length + 1 > ancho && linea.Length > 0)
        {
            yield return linea.ToString();
            linea.Clear();
        }

        if (linea.Length > 0)
        {
            linea.Append(' ');
        }

        linea.Append(palabra);
    }

    if (linea.Length > 0)
    {
        yield return linea.ToString();
    }
}

internal sealed record MetricasBucket(
    double Precision, double Recall, double F1, int Soporte, int Tp, int Fp, int Fn);

internal sealed record Informe(
    double Accuracy,
    double PrecisionMacro,
    double RecallMacro,
    double F1Macro,
    double Coverage,
    Dictionary<string, MetricasBucket> PorBucket,
    Dictionary<string, Dictionary<string, int>> Confusion);
