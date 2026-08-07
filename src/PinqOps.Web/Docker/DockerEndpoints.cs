using System.Text.Json;
using PinqOps;
using PinqOps.Alerts;
using static PinqOps.Web.EndpointHelpers;

namespace PinqOps.Web;

/// <summary>
/// The <c>/api/docker</c> routes: containers, images, volumes, networks and
/// the per-container reads and actions.
/// </summary>
public static class DockerEndpoints
{
    public static void MapDockerEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // `docker ps` carries the container's Command, and a catalog app's generated
        // password can be in it (redis' --requirepass, nats' --auth). This route sits at
        // the plain "read" scope, so the field is masked for everyone below admin — the
        // same rule the inspect route applies to the same argv.
        app.MapGet("/api/docker/containers", async Task<object?> (
            HttpContext context, DockerService docker, ResourceVisibility visibility) =>
        {
            var containers = await Env(context, docker).ListContainersAsync();
            if (context.Items["scope"] as string == "admin")
            {
                return new { items = containers.Cast<object?>().ToList() };
            }

            // Rows a team has claimed and this caller is not in are dropped before
            // the redactor runs: masking the values in a row that should not be
            // listed at all would still confirm the container exists.
            var visible = visibility.Visible(context, ResourceKinds.Container, containers, ContainerName);
            return new { items = visible.Select(container => (object?)SecretRedactor.RedactListing(container)).ToList() };
        });

        app.MapGet("/api/docker/stats", async Task<object?> (
            HttpContext context, DockerService docker, ResourceVisibility visibility) =>
        {
            var rows = await Env(context, docker).StatsAsync();
            if (context.Items["scope"] as string == "admin")
            {
                return new { items = rows.Cast<object?>().ToList() };
            }

            // The same rows as the listing, under a different name for the same
            // column: leaving these unfiltered handed back every container the
            // listing had just been changed to hide, with its load beside it.
            return new { items = visibility.Visible(context, ResourceKinds.Container, rows, StatsName).Cast<object?>().ToList() };
        });

        app.MapGet("/api/docker/images", async Task<object?> (HttpContext context, DockerService docker) =>
            new { items = await Env(context, docker).ListImagesAsync() });

        app.MapGet("/api/docker/images/updates", async Task<object?> (ImageUpdateService updates) =>
        {
            await Task.CompletedTask;
            var latest = updates.Latest;
            return new
            {
                // Nothing yet is a check that has not run, not "everything is up to
                // date" — the page says which, because they look the same otherwise.
                checkedAt = latest.Count > 0 ? latest.Values.Max(update => update.CheckedAt) : (DateTimeOffset?)null,
                items = latest.Values
                    .Select(update => new
                    {
                        update.Image,
                        update.UpdateAvailable,
                        update.Problem,
                        update.CheckedAt,
                    })
                    .OrderBy(update => update.Image, StringComparer.Ordinal),
            };
        });

        app.MapPost("/api/docker/images/updates", async Task<object?> (ImageUpdateService updates) =>
        {
            await updates.CheckAsync();
            return new { ok = true, checkedImages = updates.Latest.Count };
        });

        app.MapGet("/api/docker/images/inspect", async Task<object?> (HttpContext context, DockerService docker) =>
            new { data = await Env(context, docker).InspectImageAsync(Reference(context)) });

        app.MapGet("/api/docker/images/history", async Task<object?> (HttpContext context, DockerService docker) =>
            new { items = await Env(context, docker).ImageHistoryAsync(Reference(context)) });

        // The reference travels in the query rather than the route: it carries
        // slashes and a colon (ghcr.io/acme/app:sha-abc), and a route parameter that
        // has to be double-escaped to survive is one an operator will eventually
        // paste wrong.
        app.MapGet("/api/docker/images/pull/{jobId}", (string jobId, ImagePullJobs pulls) =>
        {
            var job = pulls.Find(jobId);
            return job is null
                ? Error(404, "Unknown pull job.")
                : Results.Json(new { image = job.Image, phase = job.Phase, done = job.Done, error = job.Error, output = job.Output });
        });

        app.MapPost("/api/docker/images/pull", async Task<object?> (HttpContext context, DockerService docker, ImagePullJobs pulls) =>
        {
            var request = await context.Request.ReadFromJsonAsync<ImageReferenceRequest>();
            var image = (request?.Image ?? string.Empty).Trim();
            if (image.Length == 0)
            {
                throw new ArgumentException("An image is required.");
            }

            var service = Env(context, docker);
            var environmentId = context.Request.Query["env"].ToString();
            var job = pulls.Start(image, environmentId, async started =>
            {
                started.Output = await service.PullImageAsync(started.Image);
            });

            return new { jobId = job.Id };
        });

        app.MapPost("/api/docker/images/tag", async Task<object?> (HttpContext context, DockerService docker) =>
        {
            var request = await context.Request.ReadFromJsonAsync<ImageTagRequest>();
            var output = await Env(context, docker)
                .TagImageAsync((request?.Image ?? string.Empty).Trim(), (request?.Target ?? string.Empty).Trim());
            return new { ok = true, output };
        });

        app.MapPost("/api/docker/images/remove", async Task<object?> (HttpContext context, DockerService docker) =>
        {
            var request = await context.Request.ReadFromJsonAsync<ImageReferenceRequest>();
            return new { ok = true, output = await Env(context, docker).RemoveImageAsync((request?.Image ?? string.Empty).Trim()) };
        });

        app.MapGet("/api/docker/volumes", async Task<object?> (HttpContext context, DockerService docker) =>
            new { items = await Env(context, docker).ListVolumesAsync() });

        app.MapGet("/api/docker/volumes/inspect", async Task<object?> (HttpContext context, DockerService docker) =>
            new { data = await Env(context, docker).InspectVolumeAsync(context.Request.Query["name"].ToString()) });

        app.MapGet("/api/docker/volumes/browse", async Task<object?> (HttpContext context, DockerService docker) =>
        {
            var path = context.Request.Query["path"].ToString();
            var items = await Env(context, docker)
                .ListVolumeContentsAsync(context.Request.Query["name"].ToString(), path);

            // The normalised path goes back with the listing so the page navigates
            // from what the server resolved rather than from what was typed — those
            // differ the moment anyone uses the parent-directory button.
            VolumePath.TryNormalize(path, out var normalized);
            return new { path = normalized, parent = VolumePath.Parent(normalized), items };
        });

        app.MapGet("/api/docker/volumes/file", async Task<IResult> (HttpContext context, DockerService docker) =>
        {
            var volume = context.Request.Query["name"].ToString();
            var path = context.Request.Query["path"].ToString();

            // Copied to a scratch directory and served from there: a file is bytes,
            // and the process runner deals in text — a round-trip through it turns
            // every binary into a corrupt one without saying so.
            var scratch = Directory.CreateTempSubdirectory("pinqops-volume-download").FullName;
            try
            {
                await Env(context, docker).CopyFromVolumeAsync(volume, path, scratch);

                VolumePath.TryNormalize(path, out var normalized);
                var bytes = await File.ReadAllBytesAsync(Path.Combine(scratch, DockerService.CopiedFileName));

                // The file's own name reaches the browser as a Content-Disposition
                // value, which ASP.NET quotes and encodes — never as a path.
                return Results.File(bytes, "application/octet-stream", Path.GetFileName(normalized));
            }
            finally
            {
                try
                {
                    Directory.Delete(scratch, recursive: true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }
        });

        app.MapPost("/api/docker/volumes", async Task<object?> (HttpContext context, DockerService docker) =>
        {
            var request = await context.Request.ReadFromJsonAsync<VolumeRequest>();
            return new { ok = true, output = await Env(context, docker).CreateVolumeAsync((request?.Name ?? string.Empty).Trim()) };
        });

        app.MapPost("/api/docker/volumes/remove", async Task<object?> (HttpContext context, DockerService docker) =>
        {
            var request = await context.Request.ReadFromJsonAsync<VolumeRequest>();
            return new { ok = true, output = await Env(context, docker).RemoveVolumeAsync((request?.Name ?? string.Empty).Trim()) };
        });

        app.MapPost("/api/docker/volumes/prune", async Task<object?> (HttpContext context, DockerService docker) =>
            new { ok = true, output = await Env(context, docker).PruneVolumesAsync() });

        app.MapGet("/api/docker/networks", async Task<object?> (HttpContext context, DockerService docker) =>
            new { items = await Env(context, docker).ListNetworksAsync() });

        app.MapGet("/api/docker/networks/{name}/inspect", async Task<object?> (string name, HttpContext context, DockerService docker) =>
            new { data = await Env(context, docker).InspectNetworkAsync(name) });

        app.MapPost("/api/docker/networks", async Task<object?> (HttpContext context, DockerService docker) =>
        {
            var request = await context.Request.ReadFromJsonAsync<NetworkCreateRequest>();
            var output = await Env(context, docker).CreateNetworkAsync(request?.Name ?? "", request?.Driver, request?.Internal ?? false);
            return new { ok = true, output };
        });

        app.MapPost("/api/docker/networks/{name}/remove", async Task<object?> (string name, HttpContext context, DockerService docker) =>
            new { ok = true, output = await Env(context, docker).RemoveNetworkAsync(name) });

        app.MapPost("/api/docker/networks/{name}/connect", async Task<object?> (string name, HttpContext context, DockerService docker) =>
        {
            var request = await context.Request.ReadFromJsonAsync<NetworkContainerRequest>();
            return new { ok = true, output = await Env(context, docker).ConnectNetworkAsync(name, request?.Container ?? "") };
        });

        app.MapPost("/api/docker/networks/{name}/disconnect", async Task<object?> (string name, HttpContext context, DockerService docker) =>
        {
            var request = await context.Request.ReadFromJsonAsync<NetworkContainerRequest>();
            return new { ok = true, output = await Env(context, docker).DisconnectNetworkAsync(name, request?.Container ?? "") };
        });

        app.MapGet("/api/docker/df", async Task<object?> (HttpContext context, DockerService docker) =>
            new { items = await Env(context, docker).SystemDiskUsageAsync() });

        app.MapGet("/api/docker/version", async Task<object?> (HttpContext context, DockerService docker) =>
            new { version = await Env(context, docker).VersionAsync() });

        app.MapGet("/api/docker/containers/{id}/logs", async Task<object?> (string id, HttpContext context, DockerService docker) =>
        {
            var tail = int.TryParse(context.Request.Query["tail"], out var parsed)
                ? Math.Clamp(parsed, 10, 5000)
                : 200;
            return new { logs = await Env(context, docker).ContainerLogsAsync(id, tail) };
        }).RequireContainerOwnership(ContainerOwnershipSource.ContainerRouteId);

        // `docker inspect` carries Config.Env, i.e. the container's credentials. Only an
        // admin gets the payload verbatim; everyone else sets the variable names with
        // their values masked, which keeps inspect useful without handing out secrets.
        app.MapGet("/api/docker/containers/{id}/inspect", async Task<object?> (string id, HttpContext context, DockerService docker) =>
        {
            var raw = await Env(context, docker).InspectContainerAsync(id);
            var isAdmin = context.Items["scope"] as string == "admin";
            return new
            {
                data = isAdmin ? (object?)raw : SecretRedactor.RedactInspect(raw),
                redacted = !isAdmin,
            };
        }).RequireContainerOwnership(ContainerOwnershipSource.ContainerRouteId);

        app.MapPost("/api/docker/containers/{id}/action", async Task<object?> (string id, HttpContext context, DockerService docker) =>
        {
            var request = await context.Request.ReadFromJsonAsync<ContainerActionRequest>();
            var output = await Env(context, docker).ContainerActionAsync(id, request?.Action ?? "");
            return new { ok = true, output };
        }).RequireContainerOwnership(ContainerOwnershipSource.ContainerRouteId);

        app.MapPost("/api/docker/containers/{id}/remove", async Task<object?> (string id, HttpContext context, DockerService docker, ContainerOwnershipStore ownership) =>
        {
            var request = await context.Request.ReadFromJsonAsync<ContainerRemoveRequest>();

            // Resolved BEFORE the removal, because afterwards there is nothing left to
            // inspect. The route accepts an id or a name, but ownership is keyed by name,
            // so a caller passing an id (which every listing also returns) left the record
            // behind under the old name — exactly what the comment below says must not
            // happen. The UI always sends names; API and script callers do not.
            var removeEnvironment = EnvId(context);
            var removedName = await Env(context, docker).ContainerNameAsync(id) ?? id;

            var output = await Env(context, docker).RemoveContainerAsync(id, request?.RemoveVolumes ?? false);
            // The container is gone; a lingering record would be inherited by
            // whatever takes its name next.
            ownership.Remove(removeEnvironment, removedName);
            return new { ok = true, output };
        }).RequireContainerOwnership(ContainerOwnershipSource.ContainerRouteId);

        app.MapPost("/api/docker/containers/{id}/rename", async Task<object?> (string id, HttpContext context, DockerService docker, ContainerOwnershipStore ownership) =>
        {
            var request = await context.Request.ReadFromJsonAsync<ContainerRenameRequest>();
            var newName = request?.Name ?? "";

            // Same as remove: the record is keyed by name, and {id} may be an id.
            var renameEnvironment = EnvId(context);
            var previousName = await Env(context, docker).ContainerNameAsync(id) ?? id;

            var output = await Env(context, docker).RenameContainerAsync(id, newName);

            // Ownership is keyed by container name, so it has to follow the rename.
            // Leaving it behind would strand the record under the old name (making
            // the container admin-only) and hand it to whatever takes that name next.
            if (ownership.Get(renameEnvironment, previousName) is { } record)
            {
                ownership.Remove(renameEnvironment, previousName);
                ownership.Set(renameEnvironment, newName, record.Owner, record.Access);
            }

            return new { ok = true, output };
        }).RequireContainerOwnership(ContainerOwnershipSource.ContainerRouteId);

        app.MapPost("/api/docker/containers/{id}/restart-policy", async Task<object?> (string id, HttpContext context, DockerService docker) =>
        {
            var request = await context.Request.ReadFromJsonAsync<ContainerRestartPolicyRequest>();
            var output = await Env(context, docker).UpdateRestartPolicyAsync(id, request?.Policy ?? "");
            return new { ok = true, output };
        }).RequireContainerOwnership(ContainerOwnershipSource.ContainerRouteId);

        app.MapPost("/api/docker/containers/{id}/commit", async Task<object?> (string id, HttpContext context, DockerService docker) =>
        {
            var request = await context.Request.ReadFromJsonAsync<ContainerCommitRequest>();
            var output = await Env(context, docker).CommitContainerAsync(id, request?.Repo ?? "");
            return new { ok = true, output };
        }).RequireContainerOwnership(ContainerOwnershipSource.ContainerRouteId);

        // Runs a non-interactive command inside a container (argv list, no shell). This
        // is arbitrary code execution inside the container, so it is admin-scoped (the
        // default for /api/docker writes) and recorded in the audit log with the id.
        app.MapPost("/api/docker/containers/{id}/exec", async Task<object?> (string id, HttpContext context, DockerService docker) =>
        {
            var request = await context.Request.ReadFromJsonAsync<ContainerExecRequest>();
            var output = await Env(context, docker).ExecCommandAsync(id, request?.Command ?? []);
            return new { ok = true, output };
        }).RequireContainerOwnership(ContainerOwnershipSource.ContainerRouteId);

        // Constrained create: admin-scoped (no deploy-prefix), audited. DockerService
        // builds the argv from typed fields — no raw flags, bind mounts or --privileged.
        app.MapPost("/api/docker/containers", async Task<object?> (HttpContext context, DockerService docker, ContainerOwnershipStore ownership) =>
        {
            var request = await context.Request.ReadFromJsonAsync<CreateContainerRequest>()
                ?? throw new ArgumentException("A container spec is required.");
            var output = await Env(context, docker).CreateContainerAsync(request);
            // The creator owns the new container so a non-admin can manage what it made.
            if (context.Items["user"] is string creator && creator.Length > 0)
            {
                var name = !string.IsNullOrWhiteSpace(request.Name)
                    ? request.Name
                    : await Env(context, docker).ContainerNameAsync(output);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    ownership.Set(EnvId(context), name, creator, ContainerOwnershipStore.AccessPrivate);
                }
            }

            return new { ok = true, output };
        });

        // Ownership map plus the caller's identity, so the UI can hide actions on
        // containers the caller cannot manage. The full map says who owns what, so only
        // an admin sees all of it; everyone else gets just the records that decide their
        // own access — the public ones and their own — which is exactly what
        // ContainerAccess.CanManage consults.
        app.MapGet("/api/docker/ownership", async Task<object?> (
            HttpContext context, ContainerOwnershipStore ownership, TeamStore teams) =>
        {
            await Task.CompletedTask;
            var isAdmin = (context.Items["scope"] as string) == "admin";
            var user = context.Items["user"] as string ?? "";
            var granted = ContainersManagedByGrant(teams, EnvId(context), user);
            var all = ownership.All(EnvId(context));
            var readable = isAdmin
                ? all
                : (IReadOnlyDictionary<string, ContainerOwnershipStore.ContainerOwnership>)all
                    .Where(entry =>
                        entry.Value.Access == ContainerOwnershipStore.AccessPublic
                        || string.Equals(entry.Value.Owner, user, StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(entry => entry.Key, entry => entry.Value);

            return new
            {
                user,
                isAdmin,
                // "stale" marks a record naming the principal every API token used to
                // share, before each got its own. It resolves to unowned — which is
                // admin-only, and therefore safe — but it looks owned, so it is
                // labelled rather than silently reinterpreted as belonging to someone.
                items = ManageMap(readable, granted),
            };
        });

        // Assigning ownership is always admin-only (even though the deploy scope can
        // reach the endpoint), so an owner cannot hand a container to themselves twice
        // or seize someone else's.
        app.MapPost("/api/docker/containers/{id}/owner", async Task<object?> (string id, HttpContext context, DockerService docker, ContainerOwnershipStore ownership) =>
        {
            var request = await context.Request.ReadFromJsonAsync<ContainerOwnerRequest>();
            var owner = (request?.Owner ?? "").Trim();
            var ownerEnvironment = EnvId(context);

            // Resolved to the container's NAME, exactly as remove and rename do.
            // Ownership is keyed by name everywhere it is written — container create,
            // app install, rename — but this route stored whatever {id} happened to be.
            // The route accepts an id, and every listing returns one, so assigning
            // ownership with an id wrote a record under the id: the gate then found
            // nothing for the name-addressed requests the dashboard sends, and the
            // container stayed admin-only however many times an admin handed it over.
            var ownerContainer = await Env(context, docker).ContainerNameAsync(id) ?? id;
            if (owner.Length == 0)
            {
                ownership.Remove(ownerEnvironment, ownerContainer);
            }
            else
            {
                ownership.Set(ownerEnvironment, ownerContainer, owner, request?.Access ?? ContainerOwnershipStore.AccessPrivate);
            }

            return new { ok = true };
        }).RequireContainerOwnership(ContainerOwnershipSource.ContainerRouteId);

        app.MapGet("/api/docker/containers/{id}/top", async Task<object?> (string id, HttpContext context, DockerService docker) =>
            new { output = await Env(context, docker).TopAsync(id) }).RequireContainerOwnership(ContainerOwnershipSource.ContainerRouteId);

        app.MapGet("/api/docker/image-id", async Task<object?> (HttpContext context, DockerService docker) =>
            new { id = await Env(context, docker).ImageIdAsync(context.Request.Query["ref"].ToString()) });

        app.MapPost("/api/docker/prune", async Task<object?> (HttpContext context, DockerService docker) =>
        {
            // One route, two operations, chosen by an explicit flag rather than by a
            // second route: they differ only in what they destroy, and `all` removes
            // the previous version of every application on this server — which is
            // what a rollback needs and cannot get back without a pull. A body-less
            // POST keeps meaning exactly what it always did.
            var request = await ReadOptionalAsync<ImagePruneRequest>(context);
            var service = Env(context, docker);
            return new
            {
                ok = true,
                output = request?.All == true ? await service.PruneAllImagesAsync() : await service.PruneImagesAsync(),
            };
        });
    }

    /// <summary>
    /// The image reference a read route was asked about. In the query rather than
    /// the route because it carries slashes and a colon
    /// (<c>ghcr.io/acme/app:sha-abc</c>), and a route parameter that has to be
    /// double-escaped to survive is one an operator will eventually paste wrong.
    /// </summary>
    private static string Reference(HttpContext context)
    {
        var reference = context.Request.Query["ref"].ToString().Trim();
        return reference.Length > 0 ? reference : throw new ArgumentException("An image is required.");
    }

    /// <summary>
    /// A body that may not be there. <c>ReadFromJsonAsync</c> throws on an empty
    /// one, which would turn every existing body-less caller into a 400.
    /// </summary>
    private static async Task<T?> ReadOptionalAsync<T>(HttpContext context)
        where T : class
    {
        if (context.Request.ContentLength is null or 0)
        {
            return null;
        }

        try
        {
            return await context.Request.ReadFromJsonAsync<T>();
        }
        catch (System.Text.Json.JsonException)
        {
            throw new ArgumentException("Invalid request body.");
        }
    }

    /// <summary>
    /// The name a listing row is governed by — the same name the ownership records
    /// and the grants key on. <c>docker ps</c> reports every name a container
    /// answers to; the first is the one pinqops uses everywhere else.
    /// </summary>
    /// <summary>
    /// The containers this caller may act on because a team they are in holds a
    /// manage grant, rather than because a personal ownership record names them.
    ///
    /// <para>The two paths were one when this route was written and are not any
    /// more: the gate consults both, and this reported only the second — so a
    /// container reachable solely through a grant listed for its team with every
    /// action button missing, and the grant looked applied while doing nothing an
    /// operator could see.</para>
    /// </summary>
    internal static IReadOnlyList<string> ContainersManagedByGrant(
        TeamStore teams, string environmentId, string? user)
    {
        ArgumentNullException.ThrowIfNull(teams);

        var callerTeams = teams.TeamsOf(user);
        if (callerTeams.Count == 0)
        {
            return [];
        }

        return
        [
            .. teams.Grants
                .Where(grant =>
                    string.Equals(grant.Kind, ResourceKinds.Container, StringComparison.Ordinal)
                    && string.Equals(grant.EnvironmentId, environmentId, StringComparison.OrdinalIgnoreCase)
                    && callerTeams.Contains(grant.TeamId, StringComparer.OrdinalIgnoreCase)
                    && GrantAccess.Satisfies(GrantAccess.Normalize(grant.Access), GrantAccess.Manage))
                .Select(grant => grant.ResourceId)
                .Distinct(StringComparer.Ordinal)
        ];
    }

    /// <summary>
    /// What the page needs to decide which actions to offer: the ownership records
    /// it may read, plus a flag for the containers a grant lets it manage. A grant is
    /// not ownership, so it is reported as its own thing rather than by inventing an
    /// owner — the page's "who owns this" column stays truthful.
    /// </summary>
    private static Dictionary<string, object> ManageMap(
        IReadOnlyDictionary<string, ContainerOwnershipStore.ContainerOwnership> readable,
        IReadOnlyList<string> managedByGrant)
    {
        var items = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var entry in readable)
        {
            items[entry.Key] = new
            {
                entry.Value.Owner,
                entry.Value.Access,
                // "stale" marks a record naming the principal every API token used to
                // share, before each got its own. It resolves to unowned — which is
                // admin-only, and therefore safe — but it looks owned, so it is
                // labelled rather than silently reinterpreted as belonging to someone.
                stale = ApiTokenStore.IsRetiredPrincipal(entry.Value.Owner),
                manage = managedByGrant.Contains(entry.Key, StringComparer.Ordinal),
            };
        }

        foreach (var name in managedByGrant)
        {
            if (items.ContainsKey(name))
            {
                continue;
            }

            items[name] = new { Owner = string.Empty, Access = string.Empty, stale = false, manage = true };
        }

        return items;
    }

    /// <summary>
    /// The container a <c>docker stats</c> row is about. Not the same property as a
    /// listing row's: <c>ps</c> reports <c>Names</c>, plural and comma-joined, while
    /// <c>stats</c> reports one <c>Name</c> — so the listing's reader applied here
    /// would find nothing and, failing closed, hide every row from everyone.
    /// </summary>
    internal static string? StatsName(JsonElement row) =>
        row.TryGetProperty("Name", out var name) && name.ValueKind == JsonValueKind.String
            ? MetricParsing.FirstName(name.GetString())
            : null;

    private static string? ContainerName(JsonElement container) =>
        container.TryGetProperty("Names", out var names) && names.ValueKind == JsonValueKind.String
            ? MetricParsing.FirstName(names.GetString())
            : null;
}
