using PinqOps.Backups;
using PinqOps;
using static PinqOps.Web.EndpointHelpers;

namespace PinqOps.Web;

/// <summary>
/// Scheduled backups of a database container or a docker volume, plus
/// run/restore/download of their snapshots.
/// </summary>
public static class BackupEndpoints
{
    public static void MapBackupEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/backups", async Task<object?> (
            HttpContext context,
            BackupConfigStore store,
            BackupService backups,
            DockerService docker,
            ResourceVisibility visibility) =>
        {
            var items = new List<object>();
            // A target nobody has claimed stays listed for everyone, as it always
            // was; one granted to a team is shown only to that team.
            var targets = visibility.Visible(
                context, ResourceKinds.BackupTarget, store.Load().Targets, target => target.Id);

            foreach (var target in targets)
            {
                // Per target, best effort. ContainerStateAsync validates the name and
                // throws for anything docker would not accept, and one such row used to
                // fail the whole request — leaving the Backups page with no list at all,
                // and no way to delete the row that broke it. ListSnapshots already takes
                // this stance for a bad id; the running chip now matches.
                bool running;
                try
                {
                    running = target.Kind == "db" && (await docker.ContainerStateAsync(target.Name)).Running;
                }
                catch (ArgumentException)
                {
                    running = false;
                }

                items.Add(new
                {
                    target.Id, target.Kind, target.Name, target.Engine, target.Schedule,
                    target.AtHour, target.RetentionCount, target.Enabled,
                    lastRun = backups.LastRun(target.Id),
                    running,
                    snapshots = backups.ListSnapshots(target.Id),
                });
            }

            return new { items };
        });

        app.MapPost("/api/backups/targets", async Task<object?> (HttpContext context, BackupConfigStore store) =>
        {
            var request = await context.Request.ReadFromJsonAsync<BackupTargetRequest>()
                ?? throw new ArgumentException("Invalid request body.");
            if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Engine)
                || string.IsNullOrWhiteSpace(request.Kind))
            {
                throw new ArgumentException("A source and engine are required.");
            }

            // Validated at the write boundary, not on every later read. Name is a docker
            // container or volume name, and storing one docker would reject made the
            // whole Backups page fail until someone hand-edited backups.json.
            if (!DockerService.IsValidResourceName(request.Name))
            {
                throw new ArgumentException(
                    $"'{request.Name}' is not a valid container or volume name (letters, digits, - _ . and no leading '-').");
            }

            // Rejected here rather than at 3am in the scheduler, where the only symptom
            // is a backup that never ran.
            //
            // A volume is exempt because it has no dump plan and needs none: it is
            // archived wholesale rather than dumped by a database client, so
            // BackupAsync branches away from DumpPlan entirely. Asking for one anyway
            // refused every volume target the page could offer, which made the picker
            // it is chosen from unusable.
            if (!BackupNaming.IsVolume(request.Kind, request.Engine))
            {
                _ = BackupService.DumpPlan(request.Engine);
            }

            var requestedId = request.Id;
            var id = requestedId;
            if (string.IsNullOrWhiteSpace(id))
            {
                // The source is part of a database target's id too. Deriving it from the
                // engine alone gave every postgres container on the host the single id
                // "db-postgres", so adding a second one silently overwrote the first and
                // both then shared one snapshot directory and one retention count.
                var volume = BackupNaming.IsVolume(request.Kind, request.Engine);
                var basis = volume ? request.Name : $"{request.Engine}-{request.Name}";
                id = $"{(volume ? "vol" : "db")}-{Slugify(basis)}";
            }

            if (!BackupNaming.IsValidId(id))
            {
                throw new ArgumentException("Invalid backup id.");
            }

            return store.Update(config =>
            {
                var target = config.Targets.FirstOrDefault(t => t.Id == id);

                // Only an explicit id may repoint an existing target at a different
                // source; a derived collision is a mistake, and silently rebinding one
                // orphans the snapshots already under that id.
                if (target is not null
                    && string.IsNullOrWhiteSpace(requestedId)
                    && !string.Equals(target.Name, request.Name, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Backup target '{id}' already exists for '{target.Name}'. Pass an explicit id to replace it.");
                }

                if (target is null)
                {
                    target = new BackupTarget { Id = id };
                    config.Targets.Add(target);
                }

                target.Kind = request.Kind;
                target.Name = request.Name;
                target.Engine = request.Engine;
                target.Schedule = request.Schedule is "hourly" or "daily" or "weekly" ? request.Schedule : "daily";
                target.AtHour = Math.Clamp(request.AtHour ?? target.AtHour, 0, 23);
                target.RetentionCount = Math.Clamp(request.RetentionCount ?? target.RetentionCount, 1, 365);
                target.Enabled = request.Enabled ?? target.Enabled;
                return new { ok = true, id };
            });
        });

        app.MapPost("/api/backups/targets/{id}/toggle", async Task<object?> (string id, BackupConfigStore store) =>
        {
            await Task.CompletedTask;

            // Through Update so the read and the write are one step: a concurrent edit
            // of another target would otherwise write this toggle away, or vice versa.
            return store.Update(config =>
            {
                var target = config.Targets.FirstOrDefault(t => t.Id == id)
                    ?? throw new ArgumentException($"Unknown backup target '{id}'.");
                target.Enabled = !target.Enabled;
                return new { ok = true, enabled = target.Enabled };
            });
        });

        app.MapDelete("/api/backups/targets/{id}", async Task<object?> (string id, BackupConfigStore store) =>
        {
            await Task.CompletedTask;
            return store.Update(config =>
            {
                config.Targets.RemoveAll(t => t.Id == id);
                return new { ok = true };
            });
        });

        app.MapPost("/api/backups/run/{id}", async Task<object?> (string id, BackupConfigStore store, BackupService backups) =>
        {
            var target = store.Load().Targets.FirstOrDefault(t => t.Id == id)
                ?? throw new ArgumentException($"Unknown backup target '{id}'.");
            return await backups.RunGuardedAsync(target);
        });

        app.MapPost("/api/backups/restore", async Task<object?> (HttpContext context, BackupConfigStore store, BackupService backups) =>
        {
            var request = await context.Request.ReadFromJsonAsync<BackupRestoreRequest>()
                ?? throw new ArgumentException("Invalid request body.");
            var target = store.Load().Targets.FirstOrDefault(t => t.Id == request.TargetId)
                ?? throw new ArgumentException($"Unknown backup target '{request.TargetId}'.");
            await backups.RestoreAsync(target, request.Snapshot ?? "");
            return new { ok = true };
        });

        app.MapDelete("/api/backups/{targetId}/snapshots/{snapshot}", async Task<object?> (string targetId, string snapshot, BackupService backups) =>
        {
            await Task.CompletedTask;
            backups.DeleteSnapshot(targetId, snapshot);
            return new { ok = true };
        });

        app.MapGet("/api/backups/offsite", async Task<object?> (OffsiteBackupService offsite) =>
        {
            await Task.CompletedTask;
            var config = offsite.Store.Load();
            var (_, problem) = offsite.Resolve();
            return new
            {
                config.Enabled,
                config.Endpoint,
                config.Region,
                config.Bucket,
                config.AccessKeyId,
                config.SecretName,
                config.Prefix,
                config.RetentionCount,
                // What is wrong with the settings as they stand, so the page can say
                // so before the next backup discovers it at three in the morning.
                problem,
            };
        });

        app.MapPost("/api/backups/offsite", async Task<object?> (HttpContext context, OffsiteBackupService offsite) =>
        {
            var request = await context.Request.ReadFromJsonAsync<OffsiteConfig>()
                ?? throw new ArgumentException("Invalid request body.");

            request.RetentionCount = Math.Clamp(request.RetentionCount, 1, OffsiteConfig.MaximumRetentionCount);
            offsite.Store.Save(request);

            // Resolved straight after saving: settings nobody has tried are settings
            // nobody has checked, and the first backup is a poor time to find out.
            var (_, problem) = offsite.Resolve();
            return new { ok = true, problem };
        });

        app.MapGet("/api/backups/{targetId}/offsite", async Task<object?> (string targetId, OffsiteBackupService offsite) =>
        {
            var (objects, error) = await offsite.ListAsync(targetId);
            return new { items = objects.Select(entry => new { entry.Key, entry.Size, entry.LastModified }), error };
        });

        app.MapPost("/api/backups/{targetId}/offsite/fetch", async Task<object?> (
            string targetId, HttpContext context, BackupService backups, OffsiteBackupService offsite) =>
        {
            var request = await context.Request.ReadFromJsonAsync<BackupRestoreRequest>()
                ?? throw new ArgumentException("Invalid request body.");
            var snapshot = request.Snapshot ?? string.Empty;

            // Fetched back into the local snapshot directory, so restoring an
            // offsite copy is the restore that already exists rather than a second
            // path with its own failure modes.
            var destination = backups.LocalPathFor(targetId, snapshot);
            var failure = await offsite.DownloadAsync(targetId, snapshot, destination);
            return failure is null
                ? new { ok = true, snapshot }
                : throw new InvalidOperationException(failure);
        });

        app.MapGet("/api/backups/download", (HttpContext context, BackupService backups) =>
        {
            var targetId = context.Request.Query["target"].ToString();
            var snapshot = context.Request.Query["snapshot"].ToString();
            var path = backups.SnapshotPath(targetId, snapshot);
            return path is null
                ? Error(404, "Snapshot not found.")
                : Results.File(path, "application/octet-stream", snapshot);
        });
    }
}
