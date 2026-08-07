namespace PinqOps.Web;

public sealed record PasswordRequest(string? Password, string? Username = null);

public sealed record SetupRequest(string? Password, string? SetupCode);

public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

public sealed record UserRequest(string? Username, string? Password, string? Role);

public sealed record UserPasswordRequest(string? Password);

public sealed record SettingsRequest(
    string? RepoUrl,
    string? Username,
    string? Pat,
    string? ComposeFile,
    string? RunnerDirectory,
    string? GithubClientId,
    string? AppId);

public sealed record AppRemoveRequest(string? Id);

public sealed record CreateDockerfileRequest(string? Content, string? Dir);

/// <summary>
/// Installs or reconfigures the managed proxy. <c>Dns</c> is optional; omitting it
/// leaves the DNS challenge as it was, and changing it replaces the container,
/// because the provider token lives in its environment.
/// </summary>
public sealed record ProxyInstallRequest(
    string? AcmeEmail, bool? Staging, bool? Force, PinqOps.Proxy.DnsChallenge? Dns = null);

/// <summary>
/// Releases a proxy port entry left behind by a removed app. Named by host port
/// because the removed app's id no longer resolves anywhere else.
/// </summary>
public sealed record PortReleaseRequest(int HostPort);

/// <summary>
/// Adds or repoints a domain. <c>Security</c> is optional and omitting it keeps
/// whatever the domain already had — the routing target and the response headers
/// are separate decisions.
/// </summary>
public sealed record DomainRequest(
    string? Domain, string? Target, int? TargetPort, PinqOps.Proxy.SecurityHeaders? Security = null);

/// <summary>
/// Turns running-behind-a-CDN on or off. Enabling refetches the CDN's address
/// ranges, so this is also how a stale trust list is refreshed.
/// </summary>
public sealed record EdgeModeRequest(bool? Enabled, int? StaticCacheSeconds);

/// <summary>
/// Changes what the proxy does with a request for a domain, without touching where
/// it routes. Either half may be omitted and is then left as it was.
/// </summary>
public sealed record DomainSettingsRequest(
    PinqOps.Proxy.SecurityHeaders? Security,
    PinqOps.Proxy.RateLimit? RateLimit,
    PinqOps.Proxy.UpstreamOptions? Upstream = null);

/// <summary>
/// Writes the Cloudflare A record. <c>Proxied</c> null or omitted means orange-cloud
/// (true); false is DNS-only.
/// </summary>
public sealed record DomainDnsPointRequest(bool? Proxied);

/// <summary>Installs a custom certificate (and optional key) for one domain.</summary>
public sealed record DomainCustomTlsRequest(string? FullChain, string? PrivateKey);

public sealed record BackupTargetRequest(
    string? Id, string? Kind, string? Name, string? Engine, string? Schedule, int? AtHour, int? RetentionCount, bool? Enabled);

public sealed record BackupRestoreRequest(string? TargetId, string? Snapshot);

/// <param name="ExpiresInDays">Null or 0 mints a token that never expires.</param>
public sealed record TokenCreateRequest(string? Name, string? Scope, int? ExpiresInDays);

public sealed record TokenRequest(string? Pat, string? Username);

public sealed record DeviceStartRequest(string? ClientId);

public sealed record DevicePollRequest(string? Handle);

public sealed record NetworkCreateRequest(string? Name, string? Driver, bool Internal);

public sealed record NetworkContainerRequest(string? Container);

/// <summary>One volume, for the routes that create or remove one.</summary>
public sealed record VolumeRequest(string? Name);

/// <summary>One image, for the routes that pull or remove one.</summary>
public sealed record ImageReferenceRequest(string? Image);

/// <summary>Give <paramref name="Image"/> a second name; it keeps its own.</summary>
public sealed record ImageTagRequest(string? Image, string? Target);

/// <param name="All">
/// Remove every image no container is using, not only the untagged layers. That
/// includes the previous version of every application on this server — which is
/// what a rollback needs and cannot get back without a pull — so it is never the
/// default.
/// </param>
public sealed record ImagePruneRequest(bool All);

/// <param name="Public">
/// Publish the app's ports on every interface instead of loopback. Off by
/// default: several catalog services have no authentication of their own, so
/// binding 0.0.0.0 put them on the internet the moment they were installed.
/// </param>
public sealed record AppInstallRequest(string? Id, int? HostPort, int[]? HostPorts, bool? Public);

/// <param name="PrivateKey">
/// Omitted on an edit to keep whatever is already stored, so changing a name
/// cannot silently drop the key that reaches the host.
/// </param>
public sealed record EnvironmentRequest(
    string? Id,
    string? Name,
    string? Transport,
    string? Host,
    string? User,
    int? Port,
    string? PrivateKey,
    string? HostKey,
    bool? ReadOnly);

public sealed record ContainerActionRequest(string? Action);

public sealed record ContainerRemoveRequest(bool? RemoveVolumes);

public sealed record ContainerRenameRequest(string? Name);

public sealed record ContainerRestartPolicyRequest(string? Policy);

public sealed record ContainerCommitRequest(string? Repo);

public sealed record ContainerExecRequest(string[]? Command);

// A deliberately constrained create request: no privileged, no host bind mounts
// (named volumes only), no --cap-add / --device / host namespaces. The server
// builds a fixed argv from these typed fields and never accepts raw docker flags.
public sealed record CreateContainerRequest(
    string? Image,
    string? Name,
    PortMappingRequest[]? Ports,
    string[]? Env,
    string[]? Labels,
    VolumeMountRequest[]? Volumes,
    string? RestartPolicy,
    string[]? Command,
    string? Memory,
    string? Cpus,
    /// <summary>
    /// The docker network to create the container on, or null for docker's
    /// default. A container that has to be reached by name — anything other
    /// containers talk to — needs this at creation: nothing joins it afterwards.
    /// </summary>
    string? Network = null);

public sealed record PortMappingRequest(int Host, int Container);

public sealed record VolumeMountRequest(string? Volume, string? Path);

public sealed record ContainerOwnerRequest(string? Owner, string? Access);

public sealed record RollbackRequest(string? Tag);

public sealed record NotificationsRequest(
    NotificationEventsRequest? Events,
    NotificationWebhookRequest? Webhook,
    NotificationSlackRequest? Slack,
    NotificationTelegramRequest? Telegram);

public sealed record NotificationEventsRequest(
    bool? DeploySucceeded,
    bool? DeployFailed,
    bool? HealthCheckFailed,
    bool? RolledBack);

public sealed record NotificationWebhookRequest(bool? Enabled, string? Url);

public sealed record NotificationSlackRequest(bool? Enabled, string? WebhookUrl);

public sealed record NotificationTelegramRequest(bool? Enabled, string? BotToken, string? ChatId);

public sealed record NotificationTestRequest(string? Channel);

/// <param name="Id">Absent creates a rule; present updates that one.</param>
/// <param name="ForSeconds">
/// How long the condition must hold before the rule fires. Named this way because
/// <c>for</c> is a C# keyword; the dashboard calls it "for".
/// </param>
public sealed record AlertRuleRequest(
    string? Id,
    string? Name,
    bool? Enabled,
    string? Metric,
    string? Target,
    string? Comparator,
    double? Threshold,
    int? ForSeconds,
    string? Severity,
    string[]? Channels,
    int? ReNotifySeconds,
    bool? NotifyOnResolve,
    int? NoDataAfterSeconds);

/// <param name="Minutes">Zero or null lifts the silence.</param>
public sealed record AlertSilenceRequest(int? Minutes);

/// <summary>
/// Reuses the deploy-notification channel shapes: identical fields, and the same
/// "a blank secret keeps the stored one" rule.
/// </summary>
public sealed record AlertChannelsRequest(
    NotificationWebhookRequest? Webhook,
    NotificationSlackRequest? Slack,
    NotificationTelegramRequest? Telegram,
    NotificationEmailRequest? Email);

/// <summary>
/// Only the recipients: where the mail goes through is the server's relay, which
/// is configured once at <c>/api/mail</c> rather than per channel.
/// </summary>
public sealed record NotificationEmailRequest(bool? Enabled, string? To);

/// <param name="SecretName">
/// The vault entry holding the relay password. The password itself never travels
/// through this endpoint — it is written to the vault, and only its name is stored
/// here.
/// </param>
public sealed record MailSettingsRequest(
    bool? Enabled,
    string? Host,
    int? Port,
    string? Security,
    string? Username,
    string? SecretName,
    string? FromAddress,
    string? FromName,
    string? EhloName,
    bool? AllowInsecureAuth);

public sealed record MailTestRequest(string? To);

/// <param name="Challenge">The token the password step handed back.</param>
/// <param name="Code">Six digits, or a recovery code.</param>
public sealed record TwoFactorRequest(string? Challenge, string? Code);

public sealed record TwoFactorCodeRequest(string? Code);

/// <param name="Username">Absent means your own account; naming another is admin-only.</param>
public sealed record TwoFactorDisableRequest(string? Username, string? Code);

public sealed record TwoFactorRequireRequest(bool? Required);

/// <param name="TeamId">Optional; empty invites them into no team.</param>
public sealed record InviteRequest(
    string? Email,
    string? Role,
    string? TeamId,
    string? TeamRole,
    int? ValidHours);

/// <summary>
/// The invitee's own choice of name and password. The invitation decides the role
/// and the team; nothing in this body can change either.
/// </summary>
public sealed record InviteAcceptRequest(string? Token, string? Username, string? Password);

public sealed record ComposeEnvRequest(Dictionary<string, string>? Set, string[]? Remove);

public sealed record ComposeCreateRequest(int? HostPort, int? ContainerPort);
