using Xunit;

namespace PinqOps.Web.Tests;

public class AppCatalogTests
{
    [Fact]
    public void NoCatalogEntryShipsAHardcodedPassword()
    {
        // Guard: every credential-bearing env entry must use a {{password}}
        // token — literal "pinqops"-style defaults must never come back.
        var offenders = AppCatalog.Apps
            .SelectMany(app => app.Env.Select(env => (app.Id, Env: env)))
            .Where(pair =>
                (pair.Env.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase)
                 || pair.Env.Contains("_KEY", StringComparison.OrdinalIgnoreCase)
                 || pair.Env.Contains("AUTH=", StringComparison.OrdinalIgnoreCase))
                && !pair.Env.Contains("{{password", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(offenders);
    }

    // Same guard for the command line: images that take their password as a flag
    // must use a token there too, not a literal.
    [Fact]
    public void NoCatalogCommandShipsAHardcodedPassword()
    {
        var offenders = AppCatalog.Apps
            .Where(app => app.Cmd is { } cmd
                && (cmd.Contains("pass", StringComparison.OrdinalIgnoreCase)
                    || cmd.Contains("--auth", StringComparison.OrdinalIgnoreCase))
                && !cmd.Contains("{{password", StringComparison.Ordinal))
            .Select(app => app.Id)
            .ToList();

        Assert.Empty(offenders);
    }

    // An app that ships without usable authentication is only safe because ports
    // bind to loopback, so it has to be flagged — the install dialog warns on it
    // and the API refuses to expose it without an explicit admin choice.
    [Fact]
    public void EveryAppWithoutCredentialsIsFlaggedUnauthenticated()
    {
        var unflagged = AppCatalog.Apps
            .Where(app => !app.Unauthenticated)
            .Where(app => !app.Env.Any(env => env.Contains("{{password", StringComparison.Ordinal))
                && app.Cmd?.Contains("{{password", StringComparison.Ordinal) != true)
            // These carry no credential of their own by design: they are either
            // first-run-claims-the-account, or they authenticate against another
            // app's credentials rather than holding one.
            .Where(app => app.Id is not ("uptime-kuma" or "phpmyadmin"
                or "wordpress" or "nginx" or "caddy" or "httpd" or "redisinsight"
                or "gitea" or "jenkins" or "verdaccio" or "vaultwarden" or "ghost"
                or "nextcloud" or "jellyfin" or "navidrome" or "syncthing" or "n8n"
                or "nodered"))
            .Select(app => app.Id)
            .ToList();

        Assert.Empty(unflagged);
    }

    // PMA_ARBITRARY turns phpMyAdmin into a database client for any host the
    // container can reach — an SSRF and lateral-movement tool for whoever opens
    // the page.
    [Fact]
    public void NoAppEnablesArbitraryHostConnections()
    {
        Assert.DoesNotContain(
            AppCatalog.Apps.SelectMany(app => app.Env),
            env => env.StartsWith("PMA_ARBITRARY=1", StringComparison.Ordinal));
    }

    [Fact]
    public void ResolveCmd_SubstitutesThePassword()
    {
        var spec = AppCatalog.Find("redis")!;

        var cmd = AppCatalog.ResolveCmd(spec, _ => "s3cr3t");

        Assert.Equal("redis-server --requirepass s3cr3t", cmd);
    }

    [Fact]
    public void ResolveCmd_NoCommand_IsNull() =>
        Assert.Null(AppCatalog.ResolveCmd(AppCatalog.Find("postgres")!, _ => "x"));

    [Fact]
    public void ResolveEnv_SubstitutesOwnPassword_AndReportsCredential()
    {
        var spec = AppCatalog.Find("postgres")!;

        var (env, credentials) = AppCatalog.ResolveEnv(spec, appId =>
        {
            Assert.Equal("postgres", appId);
            return "s3cret";
        });

        Assert.Contains("POSTGRES_PASSWORD=s3cret", env);
        Assert.Equal("s3cret", credentials["POSTGRES_PASSWORD"]);
    }

    [Fact]
    public void ResolveEnv_CompositeValue_KeepsSurroundingText()
    {
        var spec = AppCatalog.Find("neo4j")!;

        var (env, credentials) = AppCatalog.ResolveEnv(spec, _ => "s3cret");

        Assert.Contains("NEO4J_AUTH=neo4j/s3cret", env);
        Assert.Equal("neo4j/s3cret", credentials["NEO4J_AUTH"]);
    }

    [Fact]
    public void ResolveEnv_CrossAppToken_UsesReferencedAppsPassword()
    {
        var spec = AppCatalog.Find("wordpress")!;
        var asked = new List<string>();

        var (env, _) = AppCatalog.ResolveEnv(spec, appId => { asked.Add(appId); return "mysql-pw"; });

        Assert.Contains("WORDPRESS_DB_PASSWORD=mysql-pw", env);
        Assert.Contains("mysql", asked);
    }

    // Mongo Express takes its connection solely from ME_CONFIG_MONGODB_URL, and the
    // MongoDB the catalog installs sets MONGO_INITDB_ROOT_PASSWORD — which makes
    // mongod require authentication. A URL without credentials meant every command
    // was refused as unauthenticated and the container restarted for ever, while the
    // install still reported success. The URL has to carry the very password that
    // MongoDB was started with, on the same environment.
    [Fact]
    public void ResolveEnv_MongoExpress_CarriesTheMongoAppsPassword()
    {
        var directory = Directory.CreateTempSubdirectory("pinqops-mongo-express-").FullName;
        try
        {
            var credentials = new AppCredentialStore(Path.Combine(directory, "app-credentials.json"));
            string PasswordFor(string appId) =>
                credentials.GetOrCreatePassword(ManagedEnvironment.LocalId, appId);

            var (mongoEnv, _) = AppCatalog.ResolveEnv(AppCatalog.Find("mongo")!, PasswordFor);
            var (mongoExpressEnv, _) = AppCatalog.ResolveEnv(AppCatalog.Find("mongo-express")!, PasswordFor);

            const string MongoPasswordPrefix = "MONGO_INITDB_ROOT_PASSWORD=";
            var mongoPassword = mongoEnv
                .Single(entry => entry.StartsWith(MongoPasswordPrefix, StringComparison.Ordinal))[MongoPasswordPrefix.Length..];

            Assert.Contains(
                $"ME_CONFIG_MONGODB_URL=mongodb://root:{mongoPassword}@pinqops-mongo:27017/?authSource=admin",
                mongoExpressEnv);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ResolveEnv_NoTokens_PassesThroughUntouched()
    {
        var spec = AppCatalog.Find("elasticsearch")!;

        var (env, credentials) = AppCatalog.ResolveEnv(spec, _ => throw new InvalidOperationException("must not be called"));

        Assert.Equal(spec.Env, env);
        Assert.Empty(credentials);
    }
}

public class PasswordGeneratorTests
{
    [Fact]
    public void Generate_ProducesDistinctAlphanumericValuesOfFixedLength()
    {
        var first = PasswordGenerator.Generate();
        var second = PasswordGenerator.Generate();

        Assert.Equal(PasswordGenerator.Length, first.Length);
        Assert.True(first.All(char.IsAsciiLetterOrDigit));
        Assert.NotEqual(first, second);
    }
}
