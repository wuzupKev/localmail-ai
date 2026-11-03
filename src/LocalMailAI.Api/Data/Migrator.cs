using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace LocalMailAI.Api.Data;

/// <summary>
/// Migraciones con SQL versionado y PRAGMA user_version (BACKEND.md §4).
/// Nada de EF Core ni de un runner externo: los ficheros .sql van embebidos en el
/// ensamblado y se aplican en orden, una transaccion por migracion.
/// </summary>
public sealed partial class Migrator(ILogger<Migrator> log)
{
    [GeneratedRegex(@"\.(?<ver>\d{3})_(?<nombre>[^.]+)\.sql$", RegexOptions.CultureInvariant)]
    private static partial Regex NombreRecurso();

    private sealed record Migracion(int Version, string Nombre, string Recurso);

    private static List<Migracion> Descubrir()
    {
        var asm = Assembly.GetExecutingAssembly();
        var migraciones = new List<Migracion>();

        foreach (var recurso in asm.GetManifestResourceNames())
        {
            var m = NombreRecurso().Match(recurso);
            if (m.Success)
            {
                migraciones.Add(new Migracion(
                    int.Parse(m.Groups["ver"].Value),
                    m.Groups["nombre"].Value,
                    recurso));
            }
        }

        migraciones.Sort((a, b) => a.Version.CompareTo(b.Version));

        // Dos migraciones con el mismo numero significan que alguien ha creado un
        // fichero sin mirar el anterior: el orden seria indefinido y el esquema
        // divergiria entre maquinas. Mejor no arrancar.
        for (var i = 1; i < migraciones.Count; i++)
        {
            if (migraciones[i].Version == migraciones[i - 1].Version)
            {
                throw new InvalidOperationException(
                    $"dos migraciones con la version {migraciones[i].Version}: " +
                    $"{migraciones[i - 1].Nombre} y {migraciones[i].Nombre}");
            }
        }

        return migraciones;
    }

    private static string Leer(string recurso)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(recurso)
            ?? throw new InvalidOperationException($"recurso embebido no encontrado: {recurso}");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    /// <summary>Aplica lo que falte. Devuelve la version final del esquema.</summary>
    public int Migrate(SqliteConnection cn)
    {
        var migraciones = Descubrir();
        if (migraciones.Count == 0)
        {
            throw new InvalidOperationException(
                "no hay migraciones embebidas: revisa el EmbeddedResource de Migrations/*.sql en el .csproj");
        }

        var actual = LeerUserVersion(cn);
        var objetivo = migraciones[^1].Version;

        if (actual > objetivo)
        {
            // Base de datos escrita por una version mas nueva del backend. Seguir
            // adelante corromperia datos del usuario en lugar de solo fallar.
            throw new InvalidOperationException(
                $"la base de datos esta en la version {actual} y este binario solo conoce hasta la {objetivo}. " +
                "Actualiza el backend en lugar de degradarlo.");
        }

        if (actual == objetivo)
        {
            log.LogInformation("Esquema ya en la version {Version}, nada que migrar", actual);
            return actual;
        }

        foreach (var migracion in migraciones.Where(m => m.Version > actual))
        {
            var sql = Leer(migracion.Recurso);

            using var tx = cn.BeginTransaction();
            using (var cmd = cn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }

            // user_version no acepta parametros, pero el valor viene de un int ya
            // parseado del nombre del fichero, no de entrada externa.
            using (var cmd = cn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = $"PRAGMA user_version = {migracion.Version};";
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
            log.LogInformation("Migracion {Version} ({Nombre}) aplicada", migracion.Version, migracion.Nombre);
        }

        var final = LeerUserVersion(cn);
        if (final != objetivo)
        {
            throw new InvalidOperationException(
                $"tras migrar, user_version es {final} y deberia ser {objetivo}");
        }

        return final;
    }

    private static int LeerUserVersion(SqliteConnection cn)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
