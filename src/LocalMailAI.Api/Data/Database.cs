using Microsoft.Data.Sqlite;

namespace LocalMailAI.Api.Data;

/// <summary>
/// A single SQLite file at %LOCALAPPDATA%/LocalMailAI/localmail.db (BACKEND.md §4).
/// The path can be overridden with the "Database:Path" configuration key, which is
/// what the tests use so they never touch the user's real database.
/// </summary>
public sealed class Database(string path)
{
    public string Path { get; } = path;

    public static string ResolveDefaultPath(IConfiguration config)
    {
        var configured = config["Database:Path"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return System.IO.Path.GetFullPath(configured);
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            // There is no LOCALAPPDATA outside Windows; fall back to the XDG equivalent.
            localAppData = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }

        return System.IO.Path.Combine(localAppData, "LocalMailAI", "localmail.db");
    }

    /// <summary>
    /// Opens a configured connection. The journal and foreign_keys pragmas have to be
    /// set here and not inside a migration: journal_mode cannot be changed inside a
    /// transaction, and foreign_keys would be a no-op there.
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
