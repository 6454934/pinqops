using PinqOps.Databases;
using Xunit;

namespace PinqOps.Tests.Databases;

public class DatabaseEngineTests
{
    private static DatabaseEngine Postgres => DatabaseEngines.Find("postgres")!;

    [Fact]
    public void AnEngineIsFoundByItsCatalogId()
    {
        Assert.Equal("PostgreSQL", DatabaseEngines.Find("postgres")!.Name);
        Assert.Equal("PostgreSQL", DatabaseEngines.Find("POSTGRES")!.Name);
        Assert.Null(DatabaseEngines.Find("nothing"));
    }

    /// <summary>
    /// An allow-list rather than a format check: the value goes into a docker image
    /// tag, and "any string that looks like a version" is how a tag becomes an image
    /// nobody reviewed.
    /// </summary>
    [Fact]
    public void OnlyAnOfferedVersionBecomesAnImage()
    {
        Assert.Equal("postgres:17-alpine", DatabaseEngines.ImageFor(Postgres, "17-alpine"));
        Assert.Null(DatabaseEngines.ImageFor(Postgres, "latest"));
        Assert.Null(DatabaseEngines.ImageFor(Postgres, "17-alpine; rm -rf /"));
        Assert.Null(DatabaseEngines.ImageFor(Postgres, null));
    }

    [Fact]
    public void TheVersionListIsNewestFirst() => Assert.Equal("17-alpine", Postgres.Versions[0]);

    [Fact]
    public void MovingTowardsTheFrontOfTheListIsAnUpgrade()
    {
        Assert.True(DatabaseEngines.IsUpgrade(Postgres, "15-alpine", "17-alpine"));
        Assert.False(DatabaseEngines.IsUpgrade(Postgres, "17-alpine", "15-alpine"));
        Assert.False(DatabaseEngines.IsUpgrade(Postgres, "17-alpine", "17-alpine"));
    }

    [Fact]
    public void AVersionNobodyOffersIsNeitherDirection() =>
        Assert.False(DatabaseEngines.IsUpgrade(Postgres, "9.6", "17-alpine"));

    // ---- connection strings ---------------------------------------------------

    [Fact]
    public void AConnectionStringIsTheUriFormEveryOneOfTheseAccepts() =>
        Assert.Equal(
            "postgresql://postgres:hunter2@db.internal:5432/app",
            ConnectionString.For(Postgres, "db.internal", 5432, "hunter2", "app"));

    /// <summary>
    /// Not decoration: a generated password containing <c>@</c> produces a string
    /// that parses as a different host, and the failure reads as "could not resolve"
    /// rather than as a quoting problem.
    /// </summary>
    [Theory]
    [InlineData("p@ssword", "p%40ssword")]
    [InlineData("a/b", "a%2Fb")]
    [InlineData("a:b", "a%3Ab")]
    [InlineData("a?b#c", "a%3Fb%23c")]
    public void ThePasswordIsPercentEncoded(string password, string expected) =>
        Assert.Contains($":{expected}@", ConnectionString.For(Postgres, "db", 5432, password));

    [Fact]
    public void AnEngineWithNoUserStillCarriesItsPassword()
    {
        // Redis: the password is there, the user is not.
        var redis = DatabaseEngines.Find("redis")!;

        Assert.Equal("redis://:hunter2@db:6379", ConnectionString.For(redis, "db", 6379, "hunter2"));
    }

    [Fact]
    public void NoDatabaseNameLeavesThePathOff() =>
        Assert.Equal("postgresql://postgres:x@db:5432", ConnectionString.For(Postgres, "db", 5432, "x"));

    [Fact]
    public void MariadbSpeaksTheMysqlScheme() =>
        Assert.StartsWith("mysql://", ConnectionString.For(DatabaseEngines.Find("mariadb")!, "db", 3306, "x"));
}

/// <summary>
/// The order of an upgrade is the whole design: the thing that must not happen is
/// one that has destroyed the only copy by the time it discovers it cannot finish.
/// </summary>
public class DatabaseUpgradeTests
{
    private static DatabaseEngine Postgres => DatabaseEngines.Find("postgres")!;

    [Fact]
    public void AnOrdinaryUpgradeIsPossible() =>
        Assert.True(DatabaseUpgrade.Check(Postgres, "15-alpine", "17-alpine").Possible);

    [Fact]
    public void AnEngineWithNoDumpPathIsNotOffered()
    {
        var verdict = DatabaseUpgrade.Check(DatabaseEngines.Find("redis"), "6-alpine", "7-alpine");

        Assert.False(verdict.Possible);
        Assert.Contains("data file is the database", string.Join(" ", verdict.Blockers));
    }

    [Fact]
    public void AnUnknownEngineIsNotOffered() =>
        Assert.False(DatabaseUpgrade.Check(null, "1", "2").Possible);

    [Fact]
    public void AVersionPinqopsDoesNotOfferIsRefused() =>
        Assert.Contains(
            "not a version pinqops offers",
            string.Join(" ", DatabaseUpgrade.Check(Postgres, "15-alpine", "latest").Blockers));

    [Fact]
    public void UpgradingToWhatItIsAlreadyOnSaysSo() =>
        Assert.Contains(
            "already on",
            string.Join(" ", DatabaseUpgrade.Check(Postgres, "17-alpine", "17-alpine").Blockers));

    /// <summary>
    /// A dump from a newer server usually will not load into an older one, and the
    /// failure lands after the new container is already running.
    /// </summary>
    [Fact]
    public void ADowngradeIsRefusedRatherThanAttempted()
    {
        var verdict = DatabaseUpgrade.Check(Postgres, "17-alpine", "15-alpine");

        Assert.False(verdict.Possible);
        Assert.Contains("downgrade", string.Join(" ", verdict.Blockers));
    }

    /// <summary>
    /// The new version gets its own volume, so a failed upgrade is undone by
    /// starting the old container again.
    /// </summary>
    [Fact]
    public void EachVersionGetsItsOwnVolume()
    {
        Assert.NotEqual(
            DatabaseUpgrade.VolumeFor("pinqops-postgres", "15-alpine"),
            DatabaseUpgrade.VolumeFor("pinqops-postgres", "17-alpine"));
    }

    [Fact]
    public void AVolumeNameCarriesNoCharacterDockerWouldRefuse()
    {
        var volume = DatabaseUpgrade.VolumeFor("pinqops-mysql", "8.4");

        Assert.DoesNotContain('.', volume);
        Assert.DoesNotContain(':', volume);
        Assert.Equal("pinqops-mysql-data-8-4", volume);
    }

    // ---- the dump and restore commands ---------------------------------------

    [Fact]
    public void EveryUpgradeableEngineHasBothHalvesOfThePlan()
    {
        foreach (var engine in DatabaseEngines.All.Where(candidate => candidate.SupportsUpgrade))
        {
            var (command, file) = DatabaseUpgrade.DumpPlan(engine, "hunter2");

            Assert.NotEmpty(command);
            Assert.NotEmpty(DatabaseUpgrade.RestorePlan(engine, "hunter2", file));
        }
    }

    /// <summary>
    /// A restore has to fail when it fails.
    ///
    /// <para>This is the step where the old data has already been dumped and the
    /// new container built, so the caller reads the exit code and takes it as the
    /// migration having completed. <c>psql</c> does not co-operate by default: it
    /// reports every statement error and then exits 0 regardless, so a restore in
    /// which nothing at all applied was indistinguishable from one that worked —
    /// and the upgrade said it had migrated a database that was empty.</para>
    ///
    /// <para>The other two engines already behave: the <c>mysql</c> client aborts
    /// on the first error in batch mode unless <c>--force</c> is given, and
    /// <c>mongorestore</c> exits non-zero. Asserted here so a later change to
    /// either — adding <c>--force</c>, say — has to argue with a test.</para>
    /// </summary>
    [Fact]
    public void APostgresRestoreStopsAtTheFirstError()
    {
        var engine = DatabaseEngines.All.Single(candidate => candidate.Id == "postgres");

        var plan = string.Join(' ', DatabaseUpgrade.RestorePlan(engine, "hunter2", "/tmp/dump.sql"));

        Assert.Contains("ON_ERROR_STOP=1", plan, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("mysql")]
    [InlineData("mariadb")]
    public void AMysqlRestoreIsNotForcedPastErrors(string engineId)
    {
        var engine = DatabaseEngines.All.Single(candidate => candidate.Id == engineId);

        var plan = string.Join(' ', DatabaseUpgrade.RestorePlan(engine, "hunter2", "/tmp/dump.sql"));

        // --force is what would make the client carry on and still exit 0.
        Assert.DoesNotContain("--force", plan, StringComparison.Ordinal);
        Assert.DoesNotContain(" -f ", plan, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDumpIsWrittenInsideTheContainerRatherThanHeldInMemory()
    {
        // A dump held in the dashboard's memory is one that fails on the database
        // large enough to matter.
        var (_, file) = DatabaseUpgrade.DumpPlan(Postgres, "hunter2");

        Assert.StartsWith("/tmp/", file);
    }

    /// <summary>
    /// These three commands need a shell — a redirect is the point of them — so the
    /// password cannot be a discrete argv entry the way it is everywhere else. It is
    /// single-quoted instead, and the one sequence that ends single quotes is closed
    /// and reopened around an escaped one.
    /// </summary>
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("it's", "it'\\''s")]
    [InlineData("a'; rm -rf /; '", "a'\\''; rm -rf /; '\\''")]
    public void APasswordIsQuotedSoTheShellTakesItLiterally(string password, string expected) =>
        Assert.Equal(expected, DatabaseUpgrade.Escape(password));

    /// <summary>
    /// The invariant that makes single-quoting safe: in the escaped value, every
    /// quote is part of the exact <c>'\''</c> sequence. A bare one anywhere else
    /// would close the quoted word early and turn the rest of the password into
    /// shell.
    /// </summary>
    [Theory]
    [InlineData("a'; touch /tmp/pwned; '")]
    [InlineData("''")]
    [InlineData("'")]
    [InlineData("ends'")]
    public void EveryQuoteInAnEscapedPasswordIsPartOfTheEscapeSequence(string password)
    {
        var escaped = DatabaseUpgrade.Escape(password);

        for (var index = 0; index < escaped.Length; index++)
        {
            if (escaped[index] != '\'')
            {
                continue;
            }

            // Each quote is the first, third or fourth character of "'\''".
            var start = index >= 3 ? index - 3 : 0;
            var window = escaped[start..Math.Min(escaped.Length, index + 4)];
            Assert.Contains("'\\''", window, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ThePasswordReachesTheCommandOnlyInsideItsQuotedWord()
    {
        var (command, _) = DatabaseUpgrade.DumpPlan(Postgres, "a'; touch /tmp/pwned; '");

        // The whole thing is one `sh -c` script with the value inside PGPASSWORD='…'.
        Assert.Equal(["sh", "-c"], command.Take(2));
        Assert.StartsWith("PGPASSWORD='", command[^1], StringComparison.Ordinal);
    }
}
