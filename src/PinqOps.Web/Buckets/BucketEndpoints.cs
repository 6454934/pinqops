using PinqOps.ObjectStorage;
using static PinqOps.Web.EndpointHelpers;

namespace PinqOps.Web;

/// <summary>
/// Object storage buckets, through the same credentials the offsite backups use.
///
/// <para><b>One set of credentials, not two.</b> Asking for the endpoint and keys a
/// second time would mean two places to rotate them and one of them going stale —
/// and the bucket a backup lands in is a bucket, so the settings are already the
/// right ones. A separate bucket for an application is a different name under the
/// same account.</para>
///
/// <para>Admin throughout: a presigned URL is a credential in a link, and the
/// listing says what this account can reach.</para>
/// </summary>
public static class BucketEndpoints
{
    /// <summary>
    /// How long a shared link lasts, and the ceiling on it. Long enough to paste
    /// into a message, short enough that a link found in a chat log a month later is
    /// no longer one.
    /// </summary>
    public const int DefaultExpirySeconds = 3600;

    public const int MaximumExpirySeconds = 7 * 24 * 3600;

    public static void MapBucketEndpoints(this IEndpointRouteBuilder app, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(logger);

        app.MapGet("/api/buckets", async Task<object?> (OffsiteBackupService offsite, S3Client s3) =>
        {
            var (settings, problem) = offsite.Resolve();
            if (settings is null)
            {
                return new { items = Array.Empty<string>(), error = problem ?? "No object storage is configured." };
            }

            var (buckets, error) = await s3.ListBucketsAsync(settings);
            return new { items = buckets, error };
        });

        app.MapPost("/api/buckets", async Task<IResult> (
            HttpContext context, OffsiteBackupService offsite, S3Client s3) =>
        {
            var request = await context.Request.ReadFromJsonAsync<BucketRequest>();
            var (settings, problem) = offsite.Resolve();
            if (settings is null)
            {
                return Error(400, problem ?? "No object storage is configured.");
            }

            var result = await s3.CreateBucketAsync(settings, (request?.Name ?? string.Empty).Trim());
            if (!result.Ok)
            {
                return Error(400, result.Error!);
            }

            logger.LogWarning("Bucket {Name} created", request!.Name);
            return Results.Json(new { ok = true });
        });

        app.MapDelete("/api/buckets/{name}", async Task<IResult> (
            string name, OffsiteBackupService offsite, S3Client s3) =>
        {
            var (settings, problem) = offsite.Resolve();
            if (settings is null)
            {
                return Error(400, problem ?? "No object storage is configured.");
            }

            var result = await s3.DeleteBucketAsync(settings, name);
            return result.Ok ? Results.Json(new { ok = true }) : Error(400, result.Error!);
        });

        app.MapGet("/api/buckets/{name}/objects", async Task<IResult> (
            string name, OffsiteBackupService offsite, S3Client s3) =>
        {
            var (settings, problem) = offsite.Resolve();
            if (settings is null)
            {
                return Error(400, problem ?? "No object storage is configured.");
            }

            if (!BucketName.IsValid(name))
            {
                return Error(400, $"'{name}' is not a bucket name.");
            }

            // The bucket being browsed, not the one backups go to, and with no
            // prefix — this is the whole bucket rather than pinqops' corner of it.
            var (objects, error) = await s3.ListAsync(settings with { Bucket = name, Prefix = string.Empty });
            return Results.Json(new
            {
                items = objects.Select(entry => new { entry.Key, entry.Size, entry.LastModified }),
                error,
            });
        });

        app.MapPost("/api/buckets/{name}/link", async Task<IResult> (
            string name, HttpContext context, OffsiteBackupService offsite, S3Client s3) =>
        {
            var request = await context.Request.ReadFromJsonAsync<BucketLinkRequest>();
            if (request?.Key is not { Length: > 0 } key)
            {
                return Error(400, "A key is required.");
            }

            var (settings, problem) = offsite.Resolve();
            if (settings is null)
            {
                return Error(400, problem ?? "No object storage is configured.");
            }

            if (!BucketName.IsValid(name))
            {
                return Error(400, $"'{name}' is not a bucket name.");
            }

            var expires = Math.Clamp(
                request.ExpiresInSeconds == 0 ? DefaultExpirySeconds : request.ExpiresInSeconds,
                60,
                MaximumExpirySeconds);

            // Recorded, because the link outlives the click: anyone holding it can
            // read that object until it expires, and the trail is the only record
            // that it was handed out.
            logger.LogWarning("A {Expires}s link to {Bucket}/{Key} was created", expires, name, key);

            return Results.Json(new
            {
                url = s3.PresignGet(settings with { Bucket = name, Prefix = string.Empty }, key, expires),
                expiresInSeconds = expires,
            });
        });
    }

    private sealed record BucketRequest(string? Name);

    private sealed record BucketLinkRequest(string? Key, int ExpiresInSeconds);
}
