using Dapper;
using LocalMailAI.Api.Data;
using LocalMailAI.Api.Security;
using MailKit.Net.Imap;
using MailKit.Security;

namespace LocalMailAI.Api.Accounts;

/// <summary>
/// Alta interactiva de una cuenta IMAP (<c>--add-account</c>).
///
/// Existe como comando y no como endpoint a proposito: la contrasena no debe viajar
/// por HTTP, ni aparecer en un log de peticiones, ni pasar por el navegador. Se teclea
/// en la consola sin eco, se verifica contra el servidor, y se guarda cifrada con DPAPI.
///
/// El orden importa: **primero se comprueba que las credenciales funcionan y solo
/// despues se guardan**. Guardar antes dejaria al usuario con una cuenta dada de alta
/// que falla en cada sincronizacion sin decirle por que.
/// </summary>
public static class AccountSetup
{
    /// <summary>Servidores conocidos, para no obligar a buscar el host a mano.</summary>
    private static readonly Dictionary<string, (string Host, int Puerto)> Conocidos = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gmail.com"] = ("imap.gmail.com", 993),
        ["googlemail.com"] = ("imap.gmail.com", 993),
        ["outlook.com"] = ("outlook.office365.com", 993),
        ["hotmail.com"] = ("outlook.office365.com", 993),
        ["live.com"] = ("outlook.office365.com", 993),
        ["office365.com"] = ("outlook.office365.com", 993),
        ["yahoo.com"] = ("imap.mail.yahoo.com", 993),
        ["icloud.com"] = ("imap.mail.me.com", 993),
        ["me.com"] = ("imap.mail.me.com", 993),
        ["fastmail.com"] = ("imap.fastmail.com", 993),
        ["zoho.com"] = ("imap.zoho.com", 993),
    };

    public static async Task<int> RunAsync(WebApplication app)
    {
        var db = app.Services.GetRequiredService<Database>();
        var credenciales = app.Services.GetRequiredService<CredentialStore>();

        Console.WriteLine();
        Console.WriteLine("  Alta de cuenta de correo en LocalMail AI");
        Console.WriteLine("  ----------------------------------------");
        Console.WriteLine();
        Console.WriteLine("  Necesitas una CONTRASENA DE APLICACION, no la de tu cuenta.");
        Console.WriteLine("  En Gmail: cuenta de Google > Seguridad > Verificacion en dos pasos >");
        Console.WriteLine("  Contrasenas de aplicaciones. Son 16 letras.");
        Console.WriteLine();
        Console.WriteLine("  La contrasena no se mostrara al teclearla, no se escribe en ningun");
        Console.WriteLine("  fichero de texto y no sale por la API. Se cifra con DPAPI y solo la");
        Console.WriteLine("  puede descifrar tu usuario de Windows en este equipo.");
        Console.WriteLine();

        var email = Preguntar("  Direccion de correo");
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
        {
            Console.Error.WriteLine("  Eso no parece una direccion de correo.");
            return 2;
        }

        email = email.Trim().ToLowerInvariant();
        var dominio = email.Split('@')[1];

        var (hostSugerido, puertoSugerido) = Conocidos.TryGetValue(dominio, out var conocido)
            ? conocido
            : ("imap." + dominio, 993);

        var host = Preguntar($"  Servidor IMAP [{hostSugerido}]");
        host = string.IsNullOrWhiteSpace(host) ? hostSugerido : host.Trim();

        var puertoTexto = Preguntar($"  Puerto [{puertoSugerido}]");
        var puerto = string.IsNullOrWhiteSpace(puertoTexto) ? puertoSugerido : int.Parse(puertoTexto.Trim());

        var contrasena = PreguntarSinEco("  Contrasena de aplicacion");
        if (string.IsNullOrWhiteSpace(contrasena))
        {
            Console.Error.WriteLine("  No has escrito nada.");
            return 2;
        }

        // Gmail muestra la contrasena de aplicacion en grupos de cuatro. Mucha gente
        // la copia con los espacios y el servidor la rechaza sin explicar por que.
        var limpia = contrasena.Replace(" ", string.Empty);
        if (limpia.Length != contrasena.Length)
        {
            Console.WriteLine("  (quitados los espacios de la contrasena de aplicacion)");
            contrasena = limpia;
        }

        // --- Verificar ANTES de guardar -------------------------------------------

        Console.WriteLine();
        Console.WriteLine($"  Conectando a {host}:{puerto}...");

        string[] carpetas;
        try
        {
            carpetas = await VerificarAsync(host, puerto, email, contrasena);
        }
        catch (AuthenticationException)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("  El servidor ha rechazado las credenciales.");
            Console.Error.WriteLine("  Causas habituales, por orden de frecuencia:");
            Console.Error.WriteLine("    - Has usado la contrasena normal en vez de una de aplicacion.");
            Console.Error.WriteLine("    - La verificacion en dos pasos no esta activada (Gmail la exige");
            Console.Error.WriteLine("      para poder crear contrasenas de aplicacion).");
            Console.Error.WriteLine("    - El acceso IMAP esta desactivado en los ajustes del correo.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("  No se ha guardado nada.");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"  No se ha podido conectar: {ex.Message}");
            Console.Error.WriteLine("  Comprueba el servidor, el puerto y tu conexion. No se ha guardado nada.");
            return 1;
        }

        Console.WriteLine($"  Conexion correcta. {carpetas.Length} carpetas visibles.");

        var enviados = carpetas.FirstOrDefault(c =>
            c.Contains("sent", StringComparison.OrdinalIgnoreCase) ||
            c.Contains("enviado", StringComparison.OrdinalIgnoreCase));

        // La carpeta de Enviados es la fuente del arranque en frio (§7): sin ella, el
        // sistema empieza sin ningun ejemplo y tres de los cinco cajones tardan mucho
        // mas en significar algo.
        Console.WriteLine(enviados is not null
            ? $"  Carpeta de enviados detectada: {enviados}"
            : "  AVISO: no encuentro la carpeta de enviados. El arranque en frio sera mas lento.");

        // --- Guardar --------------------------------------------------------------

        var key = CredentialStore.KeyFor(email);
        credenciales.Save(key, contrasena);

        var id = Guid.NewGuid().ToString("n")[..12];

        using var cn = db.Open();
        app.Services.GetRequiredService<Migrator>().Migrate(cn);

        await cn.ExecuteAsync(
            """
            INSERT INTO accounts (id, email, imap_host, imap_port, credential_key, created_at)
            VALUES (@id, @email, @host, @puerto, @key, @ahora)
            ON CONFLICT(email) DO UPDATE SET
                imap_host = @host,
                imap_port = @puerto,
                credential_key = @key
            """,
            new { id, email, host, puerto, key, ahora = DateTimeOffset.UtcNow.ToString("O") });

        Console.WriteLine();
        Console.WriteLine($"  Cuenta {email} dada de alta.");
        Console.WriteLine($"  Contrasena cifrada en {credenciales.Directory}");
        Console.WriteLine($"  Base de datos        {db.Path}");
        Console.WriteLine();
        Console.WriteLine("  Siguiente paso: arranca el backend y sincroniza.");
        Console.WriteLine();

        return 0;
    }

    /// <summary>
    /// Conecta, autentica y lista carpetas. Es la prueba minima de que la cuenta sirve
    /// para lo que el producto necesita: leer.
    /// </summary>
    private static async Task<string[]> VerificarAsync(string host, int puerto, string email, string contrasena)
    {
        using var cliente = new ImapClient();

        // SslOnConnect para el 993 (IMAPS); StartTls para el 143. Sin cifrado no se
        // conecta nunca: seria mandar la contrasena en claro por la red.
        var opciones = puerto == 143 ? SecureSocketOptions.StartTls : SecureSocketOptions.SslOnConnect;

        using var tiempo = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await cliente.ConnectAsync(host, puerto, opciones, tiempo.Token);
        await cliente.AuthenticateAsync(email, contrasena, tiempo.Token);

        var carpetas = (await cliente.GetFoldersAsync(cliente.PersonalNamespaces[0], cancellationToken: tiempo.Token))
            .Select(c => c.FullName)
            .ToArray();

        await cliente.DisconnectAsync(true, tiempo.Token);

        return carpetas;
    }

    private static string Preguntar(string etiqueta)
    {
        Console.Write($"{etiqueta}: ");
        return Console.ReadLine() ?? string.Empty;
    }

    /// <summary>Lee sin mostrar lo tecleado, para que la contrasena no quede en pantalla.</summary>
    private static string PreguntarSinEco(string etiqueta)
    {
        Console.Write($"{etiqueta}: ");

        var sb = new System.Text.StringBuilder();

        while (true)
        {
            var tecla = Console.ReadKey(intercept: true);

            if (tecla.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return sb.ToString();
            }

            if (tecla.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0)
                {
                    sb.Length--;
                }
                continue;
            }

            if (!char.IsControl(tecla.KeyChar))
            {
                sb.Append(tecla.KeyChar);
            }
        }
    }
}
