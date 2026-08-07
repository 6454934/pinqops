using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PinqOps.TwoFactor;
using PinqOps.Web;
using Xunit;

namespace PinqOps.Tests.Web;

/// <summary>
/// A server with two accounts, one instance per test class.
///
/// <para><b>Why not one shared instance.</b> <c>LoginThrottle</c> counts a
/// client-wide bucket as well as a per-account one, so a test that deliberately
/// trips the lockout locks out every other account from the same address. That is
/// the behaviour being asserted — one host must not be able to work through many
/// accounts — which is exactly why the test provoking it cannot share a server with
/// the tests that expect to sign in.</para>
/// </summary>
public abstract class TwoFactorServerFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    internal const string Account = "ada";
    internal const string Password = "a-long-enough-password";

    private readonly string _directory;

    protected TwoFactorServerFixture()
    {
        _directory = Path.Combine(Path.GetTempPath(), "pinqops-2fa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        Environment.SetEnvironmentVariable("PINQOPS_UI_CONFIG", Path.Combine(_directory, "ui.json"));
        Environment.SetEnvironmentVariable("PINQOPS_AUDIT_LOG", Path.Combine(_directory, "audit.jsonl"));

        new UiConfigStore(Path.Combine(_directory, "ui.json")).Update(config =>
            config.Users.Add(new UserAccount
            {
                Username = Account,
                PasswordHash = PasswordHasher.Hash(Password),
                Role = UserRoles.Admin,
            }));
    }

    public HttpClient Client { get; private set; } = null!;

    public Task InitializeAsync()
    {
        Client = CreateClient();
        return Task.CompletedTask;
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory that will not go is not a test failure.
        }
    }
}

/// <summary>Shared helpers over one of those servers.</summary>
public abstract class TwoFactorLoginTestBase
{
    protected TwoFactorLoginTestBase(TwoFactorServerFixture app) => App = app;

    protected TwoFactorServerFixture App { get; }

    protected static string CodeFor(string secret, int stepOffset = 0)
    {
        Base32.TryDecode(secret, out var bytes);
        return Totp.Compute(bytes, Totp.CounterFor(DateTimeOffset.UtcNow) + stepOffset);
    }

    protected async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(string path, object body)
    {
        using var response = await App.Client.PostAsJsonAsync(path, body);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (response.StatusCode, document.RootElement.Clone());
    }

    /// <summary>Enrols the fixture's account from scratch and hands back its secret.</summary>
    protected string Enrol(string username = TwoFactorServerFixture.Account)
    {
        var twoFactor = App.Services.GetRequiredService<TwoFactorService>();
        if (twoFactor.IsEnabledFor(username))
        {
            twoFactor.Disable(username);
        }

        var secret = twoFactor.Begin(username).Secret;
        twoFactor.Confirm(username, CodeFor(secret));
        return secret;
    }

    protected async Task<string> ChallengeAsync()
    {
        var (_, body) = await PostAsync(
            "/api/auth/login",
            new { username = TwoFactorServerFixture.Account, password = TwoFactorServerFixture.Password });
        return body.GetProperty("challenge").GetString()!;
    }
}

/// <summary>
/// The two-step sign-in, driven through the real server.
///
/// <para>What is checked here is what a service-level test cannot see: that a
/// correct password alone stops being a way in, and that the challenge standing in
/// for it is not a credential of its own.</para>
/// </summary>
[Collection(TestServerCollection.Name)]
public sealed class TwoFactorLoginTests : TwoFactorLoginTestBase, IClassFixture<TwoFactorLoginTests.Fixture>
{
    public TwoFactorLoginTests(Fixture app)
        : base(app)
    {
    }

    [Fact]
    public async Task WithoutASecondFactorThePasswordIsStillTheWholeLogin()
    {
        App.Services.GetRequiredService<TwoFactorService>().Disable(TwoFactorServerFixture.Account);

        var (_, body) = await PostAsync(
            "/api/auth/login",
            new { username = TwoFactorServerFixture.Account, password = TwoFactorServerFixture.Password });

        Assert.True(body.TryGetProperty("token", out _));
        Assert.False(body.TryGetProperty("twoFactorRequired", out _));
    }

    /// <summary>The point of the feature: a correct password stops being a way in on its own.</summary>
    [Fact]
    public async Task WithOneTheCorrectPasswordGetsAChallengeRatherThanASession()
    {
        Enrol();

        var (_, body) = await PostAsync(
            "/api/auth/login",
            new { username = TwoFactorServerFixture.Account, password = TwoFactorServerFixture.Password });

        Assert.True(body.GetProperty("twoFactorRequired").GetBoolean());
        Assert.False(body.TryGetProperty("token", out _));
        Assert.NotEmpty(body.GetProperty("challenge").GetString()!);
    }

    [Fact]
    public async Task TheCodeCompletesTheSignIn()
    {
        var secret = Enrol();
        var challenge = await ChallengeAsync();

        var (_, body) = await PostAsync("/api/auth/login/2fa", new { challenge, code = CodeFor(secret, 1) });

        Assert.NotEmpty(body.GetProperty("token").GetString()!);
        Assert.Equal(TwoFactorServerFixture.Account, body.GetProperty("username").GetString());
    }

    [Fact]
    public async Task AWrongCodeIsRefusedAndHandsOverNoToken()
    {
        Enrol();
        var challenge = await ChallengeAsync();

        var (status, body) = await PostAsync("/api/auth/login/2fa", new { challenge, code = "000000" });

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.False(body.TryGetProperty("token", out _));
    }

    /// <summary>
    /// A challenge is minted only by a correct password. Made-up ones are the whole
    /// attack this route would otherwise open.
    /// </summary>
    [Fact]
    public async Task AMadeUpChallengeIsWorthNothing()
    {
        Enrol();

        var (status, _) = await PostAsync(
            "/api/auth/login/2fa", new { challenge = new string('a', 64), code = "000000" });

        // 410 rather than 401, so the lock screen can tell "send the password
        // again" from "that was the wrong code" without reading the prose.
        Assert.Equal(HttpStatusCode.Gone, status);
    }

    /// <summary>
    /// The token that just signed somebody in must not do it twice — otherwise a
    /// challenge left in a log or a browser history is a spare key.
    /// </summary>
    [Fact]
    public async Task AChallengeCannotBeUsedTwice()
    {
        var secret = Enrol();
        var challenge = await ChallengeAsync();

        await PostAsync("/api/auth/login/2fa", new { challenge, code = CodeFor(secret, 1) });
        var (status, _) = await PostAsync("/api/auth/login/2fa", new { challenge, code = CodeFor(secret, 2) });

        Assert.Equal(HttpStatusCode.Gone, status);
    }

    /// <summary>
    /// A mistyped digit must not cost the challenge: otherwise every slip means
    /// entering the password again, and the throttle is what limits the guessing.
    /// </summary>
    [Fact]
    public async Task AWrongCodeDoesNotSpendTheChallenge()
    {
        var secret = Enrol();
        var challenge = await ChallengeAsync();

        await PostAsync("/api/auth/login/2fa", new { challenge, code = "000000" });
        var (_, body) = await PostAsync("/api/auth/login/2fa", new { challenge, code = CodeFor(secret, 1) });

        Assert.NotEmpty(body.GetProperty("token").GetString()!);
    }

    [Fact]
    public async Task ARecoveryCodeSignsInWhenThePhoneIsGone()
    {
        var twoFactor = App.Services.GetRequiredService<TwoFactorService>();
        twoFactor.Disable(TwoFactorServerFixture.Account);
        var secret = twoFactor.Begin(TwoFactorServerFixture.Account).Secret;
        var codes = twoFactor.Confirm(TwoFactorServerFixture.Account, CodeFor(secret));

        var challenge = await ChallengeAsync();
        var (_, body) = await PostAsync("/api/auth/login/2fa", new { challenge, code = codes[0] });

        Assert.NotEmpty(body.GetProperty("token").GetString()!);
        Assert.True(body.GetProperty("usedRecoveryCode").GetBoolean());
        Assert.Equal(RecoveryCode.Count - 1, body.GetProperty("recoveryCodesLeft").GetInt32());
    }

    public sealed class Fixture : TwoFactorServerFixture;
}

/// <summary>
/// The second step is throttled like the first. Its own server, because tripping
/// the lockout is a client-wide event — see <see cref="TwoFactorServerFixture"/>.
/// </summary>
[Collection(TestServerCollection.Name)]
public sealed class TwoFactorLockoutTests : TwoFactorLoginTestBase, IClassFixture<TwoFactorLockoutTests.Fixture>
{
    public TwoFactorLockoutTests(Fixture app)
        : base(app)
    {
    }

    /// <summary>
    /// Six digits is a million combinations, which a machine works through in
    /// minutes. Without the throttle on this step, two-factor would turn a password
    /// somebody already knows into a lock with a very small key space.
    /// </summary>
    [Fact]
    public async Task GuessingTheCodeTripsTheSameLockoutAsGuessingThePassword()
    {
        var secret = Enrol();
        var challenge = await ChallengeAsync();

        var last = HttpStatusCode.OK;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            (last, _) = await PostAsync("/api/auth/login/2fa", new { challenge, code = "000000" });
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last);

        // The right code is refused too while the lockout stands — a throttle that
        // let the correct answer through would not be one.
        var (afterLockout, _) = await PostAsync("/api/auth/login/2fa", new { challenge, code = CodeFor(secret, 1) });
        Assert.Equal(HttpStatusCode.TooManyRequests, afterLockout);
    }

    public sealed class Fixture : TwoFactorServerFixture;
}
