using PinqOps.Deploy;
using Xunit;

namespace PinqOps.Tests.Deploy;

public class ComposePortPublicationTests
{
    private static string Generated() => ComposeTemplate.Yaml("acme", "shop", "shop", 8080, 3000);

    private static ComposeRewrite ToProxy(string yaml) =>
        ComposePortPublication.Rewrite(yaml, ComposePublishMode.Proxy);

    private static ComposeRewrite ToHostPort(string yaml) =>
        ComposePortPublication.Rewrite(yaml, ComposePublishMode.HostPort);

    // ---- the generated file -------------------------------------------------

    [Fact]
    public void TheGeneratedProjectMovesOntoTheProxy()
    {
        var result = ToProxy(Generated());

        Assert.False(result.Refused);
        Assert.True(result.Changed);
        Assert.DoesNotContain("\n            ports:", result.Yaml, StringComparison.Ordinal);
        Assert.Contains("expose:", result.Yaml, StringComparison.Ordinal);
        Assert.Contains(ComposePortPublication.Marker, result.Yaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// A key with nothing but comments under it is null, and compose refuses the
    /// file outright: "services.app.expose must be a array". So the rewrite has to
    /// leave a real entry behind, not just rename the key.
    ///
    /// <para>Asserting the key exists is what let this through — the invalid shape
    /// contains the string "expose:" as happily as the valid one does. What has to
    /// be checked is that something is listed under it.</para>
    /// </summary>
    [Fact]
    public void TheExposeKeyHasAnEntryUnderIt()
    {
        var lines = ToProxy(Generated()).Yaml.Split('\n');
        var key = Array.FindIndex(lines, line => line.TrimEnd().EndsWith("expose:", StringComparison.Ordinal));

        Assert.True(key >= 0, "the rewrite produced no expose: key");
        var entries = lines
            .Skip(key + 1)
            .TakeWhile(line => line.TrimStart().StartsWith('#') || line.TrimStart().StartsWith('-'))
            .Where(line => line.TrimStart().StartsWith('-'))
            .ToList();

        Assert.True(entries.Count > 0, "expose: has only comments under it, which compose reads as null");
    }

    /// <summary>The entry is the container port — what the app actually listens on.</summary>
    [Fact]
    public void TheExposedEntryIsTheContainerPort() =>
        Assert.Contains(
            "- \"${PINQOPS_CONTAINER_PORT:-3000}\"",
            ToProxy(Generated()).Yaml,
            StringComparison.Ordinal);

    /// <summary>
    /// The published mapping is commented rather than deleted, so the reverse
    /// rewrite restores the exact line and an operator reading the file can see what
    /// was there.
    /// </summary>
    [Fact]
    public void TheOriginalMappingIsKeptInTheFile()
    {
        Assert.Contains(
            "${PINQOPS_HOST_PORT:-8080}:${PINQOPS_CONTAINER_PORT:-3000}",
            ToProxy(Generated()).Yaml,
            StringComparison.Ordinal);
    }

    /// <summary>Enrolling and unenrolling has to land back on the original file, or
    /// the rollback path is not a rollback.</summary>
    [Fact]
    public void TheRoundTripIsExact()
    {
        var original = Generated();

        var enrolled = ToProxy(original);
        var reverted = ToHostPort(enrolled.Yaml);

        Assert.False(reverted.Refused);
        Assert.Equal(original, reverted.Yaml);
    }

    /// <summary>Calling it twice must be safe — an enrol that half-finished and is
    /// retried must not double-comment anything.</summary>
    [Fact]
    public void RewritingTwiceChangesNothingTheSecondTime()
    {
        var once = ToProxy(Generated());
        var twice = ToProxy(once.Yaml);

        Assert.False(twice.Refused);
        Assert.False(twice.Changed);
        Assert.Equal(once.Yaml, twice.Yaml);
    }

    [Fact]
    public void UnenrollingAFileThatWasNeverEnrolledChangesNothing()
    {
        var result = ToHostPort(Generated());

        Assert.False(result.Changed);
        Assert.False(result.Refused);
        Assert.Equal(Generated(), result.Yaml);
    }

    // ---- an edited file -----------------------------------------------------

    /// <summary>
    /// The generated project says "add whatever else YOUR application needs here",
    /// so by the time anyone enrols an app the file may carry volumes, environment
    /// and extra services. Regenerating would delete all of it.
    /// </summary>
    [Fact]
    public void EverythingTheOperatorAddedSurvives()
    {
        // Anchored on the comment's text rather than its indentation, which the raw
        // string literal in the template controls.
        const string Invitation = "# Add whatever else YOUR application needs here (volumes, env):";
        var edited = Generated().Replace(
            Invitation,
            "volumes:\n      - ./data:/data\n    environment:\n      LOG_LEVEL: debug",
            StringComparison.Ordinal);
        Assert.DoesNotContain(Invitation, edited, StringComparison.Ordinal);

        var result = ToProxy(edited);

        Assert.False(result.Refused);
        Assert.Contains("./data:/data", result.Yaml, StringComparison.Ordinal);
        Assert.Contains("LOG_LEVEL: debug", result.Yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRoundTripIsExactForAnEditedFileToo()
    {
        var edited = Generated().Replace(
            "            restart: unless-stopped",
            "            restart: unless-stopped\n            mem_limit: 512m",
            StringComparison.Ordinal);

        Assert.Equal(edited, ToHostPort(ToProxy(edited).Yaml).Yaml);
    }

    // ---- what it refuses ----------------------------------------------------

    /// <summary>
    /// A wrong edit here does not produce an error — it produces an app that is
    /// quietly unreachable, or a port collision at the next deploy. Refusing and
    /// saying so is the only safe answer.
    /// </summary>
    [Fact]
    public void AFileThatPublishesItsPortSomeOtherWayIsRefused()
    {
        var handWritten = """
        name: "shop"
        services:
          app:
            image: ghcr.io/acme/shop:latest
            ports:
              - "8080:3000"
        """;

        var result = ToProxy(handWritten);

        Assert.True(result.Refused);
        Assert.False(result.Changed);
        Assert.Equal(handWritten, result.Yaml);
        Assert.Contains("by hand", result.Blockers[0], StringComparison.Ordinal);
    }

    [Fact]
    public void AFileWithTheSameMappingOnTwoServicesIsRefused()
    {
        var two = Generated() + """

        services:
          worker:
            image: ghcr.io/acme/worker:latest
            ports:
              - "${PINQOPS_HOST_PORT:-8080}:${PINQOPS_CONTAINER_PORT:-3000}"
        """;

        var result = ToProxy(two);

        Assert.True(result.Refused);
        Assert.Equal(two, result.Yaml);
        Assert.Contains("2 services", result.Blockers[0], StringComparison.Ordinal);
    }

    /// <summary>A refusal never half-edits: the file it hands back is the file it
    /// was given.</summary>
    [Fact]
    public void ARefusalLeavesTheFileByteForByte()
    {
        const string Unrecognised = "name: \"shop\"\nservices:\n  app:\n    image: nginx\n";

        Assert.Equal(Unrecognised, ToProxy(Unrecognised).Yaml);
    }

    /// <summary>
    /// A project with no published port at all is neither enrolled nor refusable —
    /// there is nothing to move, and saying "cannot edit" would be wrong.
    /// </summary>
    [Fact]
    public void AProjectWithNoPublishedPortIsRefusedWithAnExplanation()
    {
        const string NoPorts = "name: \"shop\"\nservices:\n  app:\n    image: nginx\n    expose:\n      - \"3000\"\n";

        var result = ToProxy(NoPorts);

        Assert.True(result.Refused);
        Assert.Contains("does not publish its port", result.Blockers[0], StringComparison.Ordinal);
    }

    // ---- shape tolerance ----------------------------------------------------

    /// <summary>Indentation and the default values are not what identifies the
    /// line, so a reformatted file still enrols.</summary>
    [Fact]
    public void ADifferentIndentAndDefaultsStillMatch()
    {
        var reindented = """
        name: "shop"
        services:
            app:
                image: ghcr.io/acme/shop:latest
                ports:
                    - "${PINQOPS_HOST_PORT:-9000}:${PINQOPS_CONTAINER_PORT:-80}"
        """;

        var result = ToProxy(reindented);

        Assert.False(result.Refused);
        Assert.Contains("expose:", result.Yaml, StringComparison.Ordinal);
        Assert.Contains("9000}:${PINQOPS_CONTAINER_PORT:-80}", result.Yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnquotedMappingMatchesToo()
    {
        var unquoted = Generated().Replace(
            "\"${PINQOPS_HOST_PORT:-8080}:${PINQOPS_CONTAINER_PORT:-3000}\"",
            "${PINQOPS_HOST_PORT:-8080}:${PINQOPS_CONTAINER_PORT:-3000}",
            StringComparison.Ordinal);

        Assert.False(ToProxy(unquoted).Refused);
    }

    // ---- a project with more than one service -------------------------------

    /// <summary>
    /// The app is not always the first service in the file, and the key that gets
    /// renamed has to be the one belonging to the mapping that was found — not
    /// whichever <c>ports:</c> appears first.
    /// </summary>
    private const string DatabaseAbove = """
        name: "shop"
        services:
          db:
            image: postgres:17
            ports:
              - "5432:5432"
          app:
            image: ghcr.io/acme/shop:latest
            ports:
              - "${PINQOPS_HOST_PORT:-8080}:${PINQOPS_CONTAINER_PORT:-3000}"
        """;

    [Fact]
    public void ASiblingServiceDeclaredFirstKeepsItsPorts()
    {
        var result = ToProxy(DatabaseAbove);

        Assert.False(result.Refused);
        // The database still publishes 5432 on the host.
        Assert.Contains("ports:\n      - \"5432:5432\"", result.Yaml, StringComparison.Ordinal);
        Assert.Single(
            result.Yaml.Split('\n'),
            line => line.TrimEnd().EndsWith("expose:", StringComparison.Ordinal));
    }

    /// <summary>
    /// A second mapping under the SAME ports: key — an operator's metrics listener,
    /// say — would ride along into <c>expose:</c>, where a host:container mapping
    /// publishes nothing: the extra port silently stopped being reachable, with no
    /// blocker and no report. Refused instead, like every other shape the rewrite
    /// does not own.
    /// </summary>
    [Fact]
    public void ASiblingEntryUnderTheAppsOwnPortsKeyIsABlocker()
    {
        const string WithMetricsPort = """
            name: "shop"
            services:
              app:
                image: ghcr.io/acme/shop:latest
                ports:
                  - "${PINQOPS_HOST_PORT:-8080}:${PINQOPS_CONTAINER_PORT:-3000}"
                  - "9090:9090"
            """;

        var result = ToProxy(WithMetricsPort);

        Assert.True(result.Refused);
        Assert.False(result.Changed);
        Assert.Contains("9090:9090", result.Blockers[0], StringComparison.Ordinal);
        // Refusal returns the file untouched.
        Assert.Equal(WithMetricsPort, result.Yaml);
    }

    /// <summary>The order must not matter: an extra entry ABOVE the pinqops mapping is the same problem.</summary>
    [Fact]
    public void ASiblingEntryAboveThePinqopsMappingIsABlockerToo()
    {
        const string MetricsFirst = """
            name: "shop"
            services:
              app:
                image: ghcr.io/acme/shop:latest
                ports:
                  - "9090:9090"
                  - "${PINQOPS_HOST_PORT:-8080}:${PINQOPS_CONTAINER_PORT:-3000}"
            """;

        var result = ToProxy(MetricsFirst);

        Assert.True(result.Refused);
        Assert.Contains("9090:9090", result.Blockers[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// Unenrolling must put back only what enrolling took. A service that was
    /// internal-only all along has to stay internal-only — turning its
    /// <c>expose:</c> into <c>ports:</c> publishes a database on the host.
    /// </summary>
    [Fact]
    public void UnenrollingLeavesAnInternalOnlyServiceAlone()
    {
        const string WithInternalService = """
            name: "shop"
            services:
              cache:
                image: redis:7
                expose:
                  - "6379"
              app:
                image: ghcr.io/acme/shop:latest
                ports:
                  - "${PINQOPS_HOST_PORT:-8080}:${PINQOPS_CONTAINER_PORT:-3000}"
            """;

        var enrolled = ToProxy(WithInternalService);
        Assert.False(enrolled.Refused);

        var back = ToHostPort(enrolled.Yaml);

        Assert.False(back.Refused);
        Assert.Contains("expose:\n      - \"6379\"", back.Yaml, StringComparison.Ordinal);
        Assert.Equal(WithInternalService.ReplaceLineEndings("\n"), back.Yaml.ReplaceLineEndings("\n"));
    }
}
