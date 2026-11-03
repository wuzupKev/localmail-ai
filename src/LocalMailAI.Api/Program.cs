using System.Reflection;
using System.Text.Json.Serialization;
using Dapper;
using LocalMailAI.Api.Classification;
using LocalMailAI.Api.Accounts;
using LocalMailAI.Api.Data;
using LocalMailAI.Api.Embeddings;
using LocalMailAI.Api.Mail;
using LocalMailAI.Api.Model;
using LocalMailAI.Api.Security;
using MimeKit;

var builder = WebApplication.CreateBuilder(args);

// Puerto fijo 5174 y SOLO en el bucle local (BACKEND.md §3 y regla 1 de §1:
// cero red fuera de IMAP y localhost). Escuchar en 0.0.0.0 expondria el correo
// del usuario a su red local.
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

// En TypeScript "extracted?: {...}" es "X | undefined", no "X | null": una propiedad
// opcional que no existe se omite, no se serializa como null.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull);

// El front de Next corre en otro puerto, asi que necesita CORS explicito.
// Lista blanca cerrada: no AllowAnyOrigin.
const string CorsFront = "front-local";
builder.Services.AddCors(o => o.AddPolicy(CorsFront, p => p
    .WithOrigins("http://localhost:3000", "http://127.0.0.1:3000")
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();

// Migrar al arrancar: si el esquema no se puede poner al dia, es mejor no
// atender ninguna peticion que atenderla contra una base de datos a medias.
{
    var db = app.Services.GetRequiredService<Database>();
    var migrator = app.Services.GetRequiredService<Migrator>();
    using var cn = db.Open();
    var versionEsquema = migrator.Migrate(cn);
    app.Logger.LogInformation("Base de datos {Ruta} lista, esquema v{Version}", db.Path, versionEsquema);
}

// --add-account da de alta una cuenta IMAP y termina. Es un comando y no un endpoint
// a proposito: la contrasena no debe viajar por HTTP ni pasar por el navegador.
if (args.Contains("--add-account"))
{
    return await AccountSetup.RunAsync(app);
}

// --seed <carpeta> ingiere los .eml de una carpeta y termina. Sirve para tener la
// bandeja poblada sin cuenta IMAP; el producto no lleva datos de ejemplo dentro, solo
// sabe leer ficheros que le den.
var indiceSeed = Array.IndexOf(args, "--seed");
if (indiceSeed >= 0)
{
    if (indiceSeed + 1 >= args.Length)
    {
        app.Logger.LogError("--seed necesita la ruta de una carpeta con ficheros .eml");
        return 2;
    }

    return await SembrarAsync(app, args[indiceSeed + 1]);
}

app.UseCors(CorsFront);

var version = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
    .InformationalVersion.Split('+')[0] ?? "0.0.0";

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", version }));

app.MapGet("/api/emails", async (EmailRepository repo, string? bucket, int? limit, string? cursor) =>
{
    if (bucket is not null && !EsBucketValido(bucket))
    {
        return Error(StatusCodes.Status400BadRequest, "bucket_invalido",
            $"bucket debe ser responder, informativo, automatizado, ruido o duda; llego '{bucket}'");
    }

    var pagina = await repo.ListAsync(bucket, limit ?? 50, cursor, DateTimeOffset.Now);
    return Results.Ok(pagina);
});

app.MapGet("/api/emails/{id}", async (EmailRepository repo, string id) =>
{
    var correo = await repo.GetAsync(id, DateTimeOffset.Now);
    return correo is null
        ? Error(StatusCodes.Status404NotFound, "email_no_encontrado", $"no hay ningun correo con id '{id}'")
        : Results.Ok(correo);
});

// Los cinco tiles de la pantalla principal son la navegacion del front, y se pintan
// en cada carga: un endpoint de recuentos evita pedir los cinco cajones enteros solo
// para mostrar cinco numeros.
app.MapGet("/api/counts", async (EmailRepository repo) => Results.Ok(await repo.CountsAsync()));

// Marcar leido. El front lo llama SOLO en apertura explicita (clic, Enter, hoja en
// movil) y nunca al navegar con j/k: si marcara leido al pasar con el teclado, repasar
// la bandeja la dejaria entera leida sin que el usuario haya leido nada.
app.MapPatch("/api/emails/{id}", async (EmailRepository repo, string id, UnreadPatch cuerpo) =>
{
    if (cuerpo?.Unread is null)
    {
        return Error(StatusCodes.Status400BadRequest, "cuerpo_invalido",
            "se esperaba {\"unread\": true|false}");
    }

    var correo = await repo.SetUnreadAsync(id, cuerpo.Unread.Value, DateTimeOffset.Now);
    return correo is null
        ? Error(StatusCodes.Status404NotFound, "email_no_encontrado", $"no hay ningun correo con id '{id}'")
        : Results.Ok(correo);
});

// Barra lateral del front: ejemplos, precision, correos indexados y si Ollama esta.
// El desglose por origen permite a la UI distinguir "aun no te conozco" (pocos
// ejemplos, y los que hay vienen del bootstrap) de "no se clasificar esto".
app.MapGet("/api/model/status", async (ModelStatusService estado) => Results.Ok(await estado.GetAsync()));

// El nucleo del producto: el usuario corrige y el sistema aprende. La respuesta lleva
// el total de ejemplos porque el front lo muestra en el toast inmediatamente.
app.MapPost("/api/emails/{id}/classification",
    async (CorrectionsRepository correcciones, string id, ClassificationRequest cuerpo) =>
{
    if (cuerpo?.Bucket is null)
    {
        return Error(StatusCodes.Status400BadRequest, "cuerpo_invalido",
            "se esperaba {\"bucket\": \"responder|informativo|automatizado|ruido\", \"source\": \"user\"}");
    }

    // 'duda' no es corregible: es un cajon derivado del umbral, no un destino. Y el
    // usuario no puede decir "no estoy seguro" por el sistema.
    if (!EsBucketValido(cuerpo.Bucket) || cuerpo.Bucket == "duda")
    {
        return Error(StatusCodes.Status400BadRequest, "bucket_invalido",
            $"bucket debe ser responder, informativo, automatizado o ruido; llego '{cuerpo.Bucket}'");
    }

    // Solo se acepta 'user'. Los otros origenes ('bootstrap_sent', 'rule') los pone el
    // sistema, y dejar que entren por la API arruinaria la medicion que los distingue
    // de las correcciones reales de la persona.
    if (cuerpo.Source is not null and not "user")
    {
        return Error(StatusCodes.Status400BadRequest, "source_invalido",
            $"source solo puede ser 'user' desde la API; llego '{cuerpo.Source}'");
    }

    var r = await correcciones.ApplyAsync(id, BucketNames.Parse(cuerpo.Bucket), DateTimeOffset.Now);

    return r is null
        ? Error(StatusCodes.Status404NotFound, "email_no_encontrado", $"no hay ningun correo con id '{id}'")
        : Results.Ok(r);
});

// Deshacer (Ctrl+Z del front). No borra la correccion: inserta el evento inverso.
// Si no hay nada que deshacer devuelve 200 con email null, para que el front no tenga
// que llevar la cuenta de la pila.
app.MapPost("/api/corrections/undo", async (CorrectionsRepository correcciones) =>
{
    return Results.Ok(await correcciones.UndoAsync(DateTimeOffset.Now));
});

app.Run();
return 0;

// --- Auxiliares --------------------------------------------------------------

static bool EsBucketValido(string bucket) =>
    bucket is "responder" or "informativo" or "automatizado" or "ruido" or "duda";

/// <summary>
/// Resuelve una carpeta dada en la linea de ordenes.
///
/// `dotnet run --project X -- --seed fixtures` pone el directorio de trabajo en el del
/// PROYECTO, no donde se escribe el comando, asi que una ruta relativa razonable no se
/// encuentra. Se prueba tal cual, y luego subiendo hasta la raiz de backend/.
/// </summary>
static string? ResolverCarpeta(string carpeta)
{
    if (Directory.Exists(carpeta))
    {
        return carpeta;
    }

    if (Path.IsPathRooted(carpeta))
    {
        return null;
    }

    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        // La raiz de backend/ es la que tiene lab/ y models/, igual que en las
        // herramientas de paridad.
        if (Directory.Exists(Path.Combine(dir.FullName, "lab")) &&
            Directory.Exists(Path.Combine(dir.FullName, "models")))
        {
            var candidata = Path.Combine(dir.FullName, carpeta);
            return Directory.Exists(candidata) ? candidata : null;
        }
        dir = dir.Parent;
    }

    return null;
}

/// <summary>Envoltorio de error de §3: <c>{ "error": { "code", "message" } }</c>.</summary>
static IResult Error(int status, string code, string message) =>
    Results.Json(new { error = new { code, message } }, statusCode: status);

static async Task<int> SembrarAsync(WebApplication app, string carpeta)
{
    var resuelta = ResolverCarpeta(carpeta);

    if (resuelta is null)
    {
        app.Logger.LogError(
            "no encuentro la carpeta {Carpeta}. Probado: tal cual, respecto a {Actual} y respecto a la raiz de backend/",
            carpeta, Directory.GetCurrentDirectory());
        return 2;
    }

    carpeta = resuelta;

    var ficheros = Directory.GetFiles(carpeta, "*.eml", SearchOption.AllDirectories);
    if (ficheros.Length == 0)
    {
        app.Logger.LogError("no hay ficheros .eml en {Carpeta}", carpeta);
        return 2;
    }

    var db = app.Services.GetRequiredService<Database>();
    var ingestor = app.Services.GetRequiredService<EmailIngestor>();
    using var cn = db.Open();

    // Una cuenta local de siembra: las senales necesitan saber cual es la direccion
    // propia para distinguir Para: de Cc:, y de donde sale el dominio de la organizacion.
    const string cuentaId = "seed";
    var correoCuenta = app.Configuration["Seed:Account"] ?? "kevin@sagetasolutions.com";

    await cn.ExecuteAsync(
        """
        INSERT INTO accounts (id, email, imap_host, imap_port, credential_key, created_at)
        VALUES (@cuentaId, @correoCuenta, 'localhost', 0, 'ninguna-siembra-local', @ahora)
        ON CONFLICT(id) DO UPDATE SET email = @correoCuenta
        """,
        new { cuentaId, correoCuenta, ahora = DateTimeOffset.UtcNow.ToString("O") });

    var nuevos = 0;
    var repetidos = 0;
    var fallidos = 0;
    var porBucket = new Dictionary<Bucket, int>();

    // Por fecha: el estado del hilo (participas, mensajes sin responder) depende del
    // orden en que entran los mensajes, igual que en un sync real.
    foreach (var fichero in ficheros.OrderBy(f => f, StringComparer.Ordinal))
    {
        try
        {
            var mensaje = await MimeMessage.LoadAsync(fichero);
            var info = new FileInfo(fichero);
            var r = await ingestor.IngestAsync(cn, cuentaId, mensaje, 0, "INBOX", info.Length);

            if (r.Inserted)
            {
                nuevos++;
                porBucket[r.Bucket] = porBucket.GetValueOrDefault(r.Bucket) + 1;
            }
            else
            {
                repetidos++;
            }
        }
        catch (Exception ex)
        {
            // §11: hay correo roto. Registrarlo, no fallar.
            fallidos++;
            app.Logger.LogWarning("no se ha podido ingerir {Fichero}: {Error}", fichero, ex.Message);
        }
    }

    app.Logger.LogInformation(
        "Siembra terminada: {Nuevos} nuevos, {Repetidos} ya estaban, {Fallidos} fallidos",
        nuevos, repetidos, fallidos);

    foreach (var (bucket, n) in porBucket.OrderByDescending(p => p.Value))
    {
        app.Logger.LogInformation("  {Bucket}: {N}", bucket.ToWire(), n);
    }

    return fallidos > 0 && nuevos == 0 ? 1 : 0;
}

/// <summary>
/// Cuerpo de PATCH /api/emails/{id}. Nullable a proposito: asi se distingue
/// "unread: false" de "no me has mandado el campo".
/// </summary>
internal sealed record UnreadPatch(bool? Unread);

/// <summary>
/// Cuerpo de POST /api/emails/{id}/classification. `source` es opcional y solo
/// admite "user": ver el comentario del endpoint.
/// </summary>
internal sealed record ClassificationRequest(string? Bucket, string? Source);
