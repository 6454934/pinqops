using System.Globalization;

namespace PinqOps.Databases;

/// <summary>
/// What pinqops knows about one database engine: how to name a version of it, how to
/// build a connection string, and — the part that matters — how to get the data out
/// of one version and into another.
/// </summary>
/// <param name="Id">The catalog id, e.g. <c>postgres</c>.</param>
/// <param name="Repository">The image repository, without a tag.</param>
/// <param name="Versions">The versions offered, newest first. Only these are ever put in an image tag.</param>
/// <param name="PasswordVariable">The environment variable holding the password.</param>
/// <param name="Scheme">The URI scheme a connection string uses.</param>
public sealed record DatabaseEngine(
    string Id,
    string Name,
    string Repository,
    IReadOnlyList<string> Versions,
    string DefaultUser,
    string PasswordVariable,
    int Port,
    string Scheme,
    bool SupportsUpgrade);

/// <summary>
/// The engines a version upgrade or a password rotation understands.
///
/// <para><b>Deliberately shorter than the catalog.</b> The catalog installs a dozen
/// databases; this covers the four whose dump and restore are a single well-known
/// command pair. An engine whose upgrade is a multi-step migration with its own
/// tooling is better left out than half-supported — a "one-click upgrade" that works
/// for three engines and corrupts the fourth is worse than a button that is not
/// there.</para>
/// </summary>
public static class DatabaseEngines
{
    public static readonly IReadOnlyList<DatabaseEngine> All =
    [
        new(
            "postgres", "PostgreSQL", "postgres",
            ["17-alpine", "16-alpine", "15-alpine", "14-alpine"],
            DefaultUser: "postgres",
            PasswordVariable: "POSTGRES_PASSWORD",
            Port: 5432,
            Scheme: "postgresql",
            SupportsUpgrade: true),
        new(
            "mysql", "MySQL", "mysql",
            ["8.4", "8.0"],
            DefaultUser: "root",
            PasswordVariable: "MYSQL_ROOT_PASSWORD",
            Port: 3306,
            Scheme: "mysql",
            SupportsUpgrade: true),
        new(
            "mariadb", "MariaDB", "mariadb",
            ["11", "10.11"],
            DefaultUser: "root",
            PasswordVariable: "MARIADB_ROOT_PASSWORD",
            Port: 3306,
            Scheme: "mysql",
            SupportsUpgrade: true),
        new(
            "mongo", "MongoDB", "mongo",
            ["7", "6"],
            DefaultUser: "root",
            PasswordVariable: "MONGO_INITDB_ROOT_PASSWORD",
            Port: 27017,
            Scheme: "mongodb",
            SupportsUpgrade: true),
        // Redis has no dump-and-restore across versions in the same sense — its
        // persistence file is the data — so it is listed for the connection string
        // and the password, and its upgrade button is not offered.
        new(
            "redis", "Redis", "redis",
            ["7-alpine", "6-alpine"],
            DefaultUser: "",
            PasswordVariable: "",
            Port: 6379,
            Scheme: "redis",
            SupportsUpgrade: false),
    ];

    public static DatabaseEngine? Find(string? id) =>
        All.FirstOrDefault(engine => string.Equals(engine.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The image for one version, or null when the version is not one this offers.
    ///
    /// <para>An allow-list rather than a format check: the value goes into a docker
    /// image tag, and "any string that looks like a version" is how a tag becomes an
    /// image nobody reviewed.</para>
    /// </summary>
    public static string? ImageFor(DatabaseEngine engine, string? version)
    {
        ArgumentNullException.ThrowIfNull(engine);
        return engine.Versions.Contains(version, StringComparer.Ordinal)
            ? $"{engine.Repository}:{version}"
            : null;
    }

    /// <summary>
    /// Whether <paramref name="to"/> is a later version than <paramref name="from"/>.
    ///
    /// <para>The list is newest first, so this is a position comparison rather than
    /// a version parse — which keeps <c>17-alpine</c> and <c>8.4</c> comparable
    /// without inventing a scheme neither of them follows.</para>
    /// </summary>
    public static bool IsUpgrade(DatabaseEngine engine, string from, string to)
    {
        ArgumentNullException.ThrowIfNull(engine);

        var fromIndex = engine.Versions.ToList().IndexOf(from);
        var toIndex = engine.Versions.ToList().IndexOf(to);
        return fromIndex >= 0 && toIndex >= 0 && toIndex < fromIndex;
    }
}

/// <summary>
/// The connection string for one database, as an application would put it in its
/// environment.
/// </summary>
public static class ConnectionString
{
    /// <summary>
    /// Builds the URI form every one of these engines accepts.
    ///
    /// <para>The user and password are percent-encoded, which is not decoration: a
    /// generated password containing <c>@</c> or <c>/</c> — and generated passwords
    /// do — produces a string that parses as a different host, and the failure reads
    /// as "could not resolve" rather than as a quoting problem.</para>
    /// </summary>
    public static string For(DatabaseEngine engine, string host, int port, string password, string? database = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        var credentials = engine.DefaultUser.Length == 0
            ? (password.Length == 0 ? string.Empty : $":{Uri.EscapeDataString(password)}@")
            : $"{Uri.EscapeDataString(engine.DefaultUser)}:{Uri.EscapeDataString(password)}@";

        var path = database is { Length: > 0 } ? $"/{Uri.EscapeDataString(database)}" : string.Empty;
        return $"{engine.Scheme}://{credentials}{host}:{port.ToString(CultureInfo.InvariantCulture)}{path}";
    }
}
