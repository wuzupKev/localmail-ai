using Microsoft.Data.Sqlite;

namespace LocalMailAI.Api.Data;

/// <summary>
/// Un unico fichero SQLite en %LOCALAPPDATA%/LocalMailAI/localmail.db (BACKEND.md §4).
/// La ruta se puede sobreescribir con la clave de configuracion "Database:Path",
/// que es lo que usan los tests para no tocar la base de datos real del usuario.
/// </summary>
public sealed class Database(string path)
{
    public string Path { get; } = path;

    public static string ResolveDefaultPath(IConfiguration config)
    {
        var configurada = config["Database:Path"];
        if (!string.IsNullOrWhiteSpace(configurada))
        {
            return System.IO.Path.GetFullPath(configurada);
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            // Fuera de Windows no existe LOCALAPPDATA; cae al equivalente XDG.
            localAppData = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }

        return System.IO.Path.Combine(localAppData, "LocalMailAI", "localmail.db");
    }

    /// <summary>
    /// Abre una conexion ya configurada. Los PRAGMA de journal y foreign_keys tienen
    /// que ir aqui y no dentro de una migracion: journal_mode no se puede cambiar
    /// dentro de una transaccion y foreign_keys seria un no-op.
    /// </summary>
    public SqliteConnection Open()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();

        var cn = new SqliteConnection(cs);
        cn.Open();

        using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;
            PRAGMA synchronous = NORMAL;
            """;
        cmd.ExecuteNonQuery();

        return cn;
    }
}
