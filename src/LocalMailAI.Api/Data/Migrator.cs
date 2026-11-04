using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace LocalMailAI.Api.Data;

/// <summary>
/// Versioned SQL migrations driven by PRAGMA user_version (BACKEND.md §4).
/// No EF Core and no external runner: the .sql files are embedded in the assembly and
/// applied in order, one transaction per migration.
/// </summary>
public sealed partial class Migrator(ILogger<Migrator> log)
{
    [GeneratedRegex(@"\.(?<ver>\d{3})_(?<name>[^.]+)\.sql$", RegexOptions.CultureInvariant)]
    private static partial Regex ResourceName();

    private sealed record Migration(int Version, string Name, string Resource);

    private static List<Migration> Discover()
    {
        var asm = Assembly.GetExecutingAssembly();
        var migrations = new List<Migration>();

        foreach (var resource in asm.GetManifestResourceNames())
        {
            var m = ResourceName().Match(resource);
            if (m.Success)
            {
                migrations.Add(new Migration(
                    int.Parse(m.Groups["ver"].Value),
                    m.Groups["name"].Value,
                    resource));
            }
        }

        migrations.Sort((a, b) => a.Version.CompareTo(b.Version));

        // Two migrations with the same number mean someone created a file without
        // checking the previous one: the order would be undefined and the schema would
        // diverge between machines. Better not to start at all.
        for (var i = 1; i < migrations.Count; i++)
        {
            if (migrations[i].Version == migrations[i - 1].Version)
            {
                throw new InvalidOperationException(
                    $"two migrations share version {migrations[i].Version}: " +
                    $"{migrations[i - 1].Name} and {migrations[i].Name}");
            }
        }

        return migrations;
    }

    private static string Read(string resource)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"embedded resource not found: {resource}");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    /// <summary>Applies whatever is missing. Returns the resulting schema version.</summary>
    public int Migrate(SqliteConnection cn)
    {
        var migrations = Discover();
        if (migrations.Count == 0)
        {
            throw new InvalidOperationException(
                "no embedded migrations: check the EmbeddedResource entry for Migrations/*.sql in the .csproj");
        }

        var current = ReadUserVersion(cn);
        var target = migrations[^1].Version;

        if (current > target)
        {
            // A database written by a newer build. Carrying on would corrupt the
            // user's data rather than merely failing.
            throw new InvalidOperationException(
                $"the database is at version {current} and this binary only knows up to {target}. " +
                "Update the backend instead of downgrading it.");
        }

        if (current == target)
        {
            log.LogInformation("Schema already at version {Version}, nothing to migrate", current);
            return current;
        }

        foreach (var migration in migrations.Where(m => m.Version > current))
        {
            var sql = Read(migration.Resource);

            using var tx = cn.BeginTransaction();
            using (var cmd = cn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }

            // user_version does not accept parameters, but the value is an int already
            // parsed from the file name, not external input.
            using (var cmd = cn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = $"PRAGMA user_version = {migration.Version};";
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
            log.LogInformation("Migration {Version} ({Name}) applied", migration.Version, migration.Name);
        }

        var final = ReadUserVersion(cn);
        if (final != target)
        {
            throw new InvalidOperationException(
                $"after migrating, user_version is {final} and should be {target}");
        }

        return final;
    }

    private static int ReadUserVersion(SqliteConnection cn)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
