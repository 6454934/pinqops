using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// A response that arrives after the user has moved on must not be painted.
///
/// <para>The failure is worse than a stale number: what is shown is real data with
/// the wrong label on it. A container listing that lands after the server switch
/// fills the table with the previous host's containers under the new host's name,
/// and every row action then acts on the wrong machine. A detail modal that lands
/// after a second container was opened shows one container's data under the other's
/// title.</para>
///
/// <para>The page has no build step and no component framework, so nothing but a
/// check like this notices when a handler loses its guard again. The shape is the
/// one the DNS hint already uses: remember what was asked for, and at the moment of
/// applying, confirm it is still what is wanted.</para>
/// </summary>
public class DashboardStaleResponseTests
{
    private static string FunctionBody(string declaration) => DashboardSource.FunctionBody(declaration);

    /// <summary>
    /// The environment is captured before the call and checked after it, and the
    /// check comes before anything is stored — a guard that ran after the cache was
    /// written would leave the next render showing the other host's rows.
    /// </summary>
    [Fact]
    public void TheContainerListingIsDroppedWhenTheServerHasChanged()
    {
        var body = FunctionBody("async function loadContainers(");

        var captured = body.IndexOf("=currentEnvId", StringComparison.Ordinal);
        var guard = body.IndexOf("!==currentEnvId", StringComparison.Ordinal);
        var applied = body.IndexOf("containersCache=", StringComparison.Ordinal);

        Assert.True(captured >= 0, "loadContainers does not remember which server it asked");
        Assert.True(guard > captured, "loadContainers does not check the server is still the one it asked");
        Assert.True(guard < applied, "loadContainers stores the listing before checking whose it is");
    }

    /// <summary>
    /// And every other listing whose rows carry an action, for the same reason.
    ///
    /// <para>The containers view was guarded and these were not, which is the worst
    /// version of the split: the hazard was understood, written down, and fixed in one
    /// place. Volumes and images are the sharpest of them, because catalog names are
    /// identical on every host — <c>pinqops-postgres-data</c>, <c>postgres:16-alpine</c>
    /// — so a listing that lands after the switch offers a Remove that reads as the
    /// row in front of the operator and deletes this machine's copy.</para>
    /// </summary>
    [Theory]
    [InlineData("async function loadStorage(")]
    [InlineData("async function loadImages(")]
    [InlineData("async function loadApps(")]
    [InlineData("async function loadNetworks(")]
    [InlineData("async function loadOverview(")]
    public void EveryListingIsDroppedWhenTheServerHasChanged(string declaration)
    {
        var body = FunctionBody(declaration);

        var captured = body.IndexOf("=currentEnvId", StringComparison.Ordinal);
        var guard = body.IndexOf("!==currentEnvId", StringComparison.Ordinal);
        var applied = body.IndexOf("await ", StringComparison.Ordinal);

        Assert.True(captured >= 0, $"{declaration} does not remember which server it asked");
        Assert.True(captured < applied, $"{declaration} remembers the server after it has already asked");
        Assert.True(guard > captured, $"{declaration} does not check the server is still the one it asked");
    }

    /// <summary>
    /// Two overlapping opens: the second wins the title, so the first one's data
    /// must not be committed when it arrives.
    /// </summary>
    [Fact]
    public void TheDetailModalIsDroppedWhenAnotherContainerWasOpened()
    {
        var body = FunctionBody("async function openDetail(");

        var guard = body.IndexOf("detailName!==name", StringComparison.Ordinal);
        var applied = body.IndexOf("detailData=", StringComparison.Ordinal);

        Assert.True(guard >= 0, "openDetail does not check it is still the container the user is on");
        Assert.True(guard < applied, "openDetail commits the data before checking whose it is");
    }

    /// <summary>
    /// A download of something the API serves has to carry the bearer token, because
    /// there is no cookie anywhere in this product — the only credentials the server
    /// reads are the Authorization header and the WebSocket subprotocol.
    ///
    /// <para>The volume browser's Download was a plain anchor, so the browser issued
    /// a top-level request with no header at all, got a 401, and — because the anchor
    /// carried <c>download</c> — wrote the JSON error body to disk under a name taken
    /// from the URL. No toast, nothing in the app: the operator got a "downloaded"
    /// file that was an error message, and the feature had never worked.</para>
    ///
    /// <para>It also has to go through <c>withEnv</c>, or it reads the local daemon's
    /// volume while the page is showing a remote host's.</para>
    /// </summary>
    [Fact]
    public void TheVolumeDownloadCarriesTheTokenAndTheEnvironment()
    {
        var body = FunctionBody("async function downloadVolumeFile(");

        Assert.Contains("Bearer \"+token", body, StringComparison.Ordinal);
        Assert.Contains("withEnv(", body, StringComparison.Ordinal);
        Assert.Contains("blob()", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Server prose that arrives as a field of a successful response reaches the
    /// same translation table an error does.
    ///
    /// <para>Only <c>data.error</c> went through it, so the Mail page's problem
    /// banner and the no-gap blockers list — both of which already have hand-written
    /// Turkish — were read in English, and the operator then saw the identical
    /// sentence in Turkish when the save refused it.</para>
    /// </summary>
    [Theory]
    [InlineData("m.problem")]
    [InlineData("bg.blockers")]
    public void ServerProseInAFieldIsTranslatedLikeAnError(string field)
    {
        var source = DashboardSource.Html;
        var used = source.IndexOf(field, StringComparison.Ordinal);

        Assert.True(used >= 0, $"{field} is no longer rendered — move this check to whatever replaced it");
        Assert.Contains("trApi(", source[used..(used + 200)], StringComparison.Ordinal);
    }

    /// <summary>
    /// And nothing in the page reaches an <c>/api</c> path through a bare
    /// <c>href</c>: such a link cannot carry the token, so it can only ever save the
    /// refusal.
    /// </summary>
    [Fact]
    public void NoLinkPointsStraightAtTheApi() =>
        Assert.DoesNotContain("href=\"/api/", DashboardSource.Html, StringComparison.Ordinal);

    /// <summary>
    /// The image verdict is the mildest member of the family and still the same
    /// mistake: a wrong badge rather than a wrong action, but it is read as a
    /// statement about the container whose panel it is on.
    /// </summary>
    [Fact]
    public void TheImageVerdictIsDroppedWhenAnotherContainerWasOpened()
    {
        var body = FunctionBody("function checkImageStatus(");

        var captured = body.IndexOf("=detailName", StringComparison.Ordinal);
        var guard = body.IndexOf("detailName!==", StringComparison.Ordinal);
        var applied = body.IndexOf("dt-imgstatus", StringComparison.Ordinal);

        Assert.True(captured >= 0, "checkImageStatus does not remember which container it asked about");
        Assert.True(guard > captured, "checkImageStatus does not check the modal is still on that container");
        Assert.True(guard < applied, "checkImageStatus reaches for the panel before checking whose it is");
    }

    /// <summary>
    /// The refresh has the same shape and the same consequence — it rewrites the
    /// open modal's body from a response issued for whichever container was open
    /// when it started.
    /// </summary>
    [Fact]
    public void TheDetailRefreshIsDroppedWhenTheModalHasMovedOn()
    {
        var body = FunctionBody("async function refreshDetail(");

        var guard = body.IndexOf("!==name", StringComparison.Ordinal);
        var applied = body.IndexOf("detailData=", StringComparison.Ordinal);

        Assert.True(guard >= 0, "refreshDetail does not check the modal is still on the container it asked about");
        Assert.True(guard < applied, "refreshDetail commits the data before checking whose it is");
    }
}
