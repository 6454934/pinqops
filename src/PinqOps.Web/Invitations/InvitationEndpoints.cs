using PinqOps.Invitations;
using PinqOps.Mail;
using static PinqOps.Web.EndpointHelpers;

namespace PinqOps.Web;

/// <summary>
/// Inviting somebody to create an account here, and the two anonymous routes they
/// use to do it.
///
/// <para><b>The invitee sets their own password.</b> The alternative — an admin
/// creating the account and telling them the password — means the password travels
/// through a chat window and is known to two people from the first day. What
/// travels here is a link that works once and then does not.</para>
/// </summary>
public static class InvitationEndpoints
{
    public static void MapInvitationEndpoints(this IEndpointRouteBuilder app, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(logger);

        app.MapGet("/api/invites", async Task<object?> (InvitationStore store, MailService mail) =>
        {
            await Task.CompletedTask;
            var now = DateTimeOffset.UtcNow;
            store.Sweep(now);

            return new
            {
                items = store.Load()
                    .OrderByDescending(invitation => invitation.CreatedAt)
                    .Select(invitation => new
                    {
                        invitation.Id,
                        invitation.Email,
                        invitation.Role,
                        invitation.TeamId,
                        invitation.TeamRole,
                        invitation.CreatedAt,
                        invitation.ExpiresAt,
                        invitation.CreatedBy,
                        invitation.AcceptedAt,
                        invitation.AcceptedAs,
                        status = invitation.StatusAt(now),
                    }),
                // The page says whether the link will actually be sent, rather than
                // letting somebody find out when nobody arrives.
                mailReady = mail.Ready,
            };
        });

        app.MapPost("/api/invites", async Task<object?> (
            HttpContext context,
            InvitationStore store,
            UiConfigStore users,
            TeamStore teams,
            MailService mail) =>
        {
            var request = await context.Request.ReadFromJsonAsync<InviteRequest>()
                ?? throw new ArgumentException("Invalid request body.");

            var email = EmailAddress.Normalize(request.Email);
            var role = UserRoles.IsValid(request.Role)
                ? request.Role!
                : throw new ArgumentException("Role must be viewer, deployer, or admin.");

            var teamId = (request.TeamId ?? string.Empty).Trim();
            if (teamId.Length > 0 && !teams.Teams.Any(team => string.Equals(team.Id, teamId, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException($"There is no team called '{teamId}'.");
            }

            var actor = context.Items["user"] as string ?? UserRoles.LegacyAdmin;
            var now = DateTimeOffset.UtcNow;

            var (id, token) = store.Update(invitations =>
            {
                // Inside the lock, so two requests cannot both pass the cap against
                // the same snapshot. An invitation endpoint is a way to make this
                // server send mail to an address of the caller's choosing.
                if (InvitationPolicy.CheckRate(invitations, actor, now) is { } tooMany)
                {
                    throw new InvalidOperationException(tooMany);
                }

                var newId = InvitationStore.NewId();
                var (link, hash) = InvitationToken.New(newId);
                invitations.Add(new Invitation
                {
                    Id = newId,
                    Email = email,
                    Role = role,
                    TeamId = teamId,
                    TeamRole = TeamRoles.Normalize(request.TeamRole),
                    Sha256 = hash,
                    CreatedAt = now,
                    ExpiresAt = now.AddHours(InvitationPolicy.ValidHours(request.ValidHours)),
                    CreatedBy = actor,
                });

                return (newId, link);
            });

            // The dashboard's own address, as the admin is looking at it right now.
            // The server has no other idea what URL it is reached on, and the person
            // being invited has to reach the same one.
            var link = $"{context.Request.Scheme}://{context.Request.Host}/?invite={Uri.EscapeDataString(token)}";
            var failure = await mail.SendAsync(
                [email],
                $"You have been invited to pinqops on {Environment.MachineName}",
                $"{actor} has invited you to create an account on the pinqops server at {Environment.MachineName}."
                + Environment.NewLine + Environment.NewLine
                + link
                + Environment.NewLine + Environment.NewLine
                + $"The link works once and stops working after {InvitationPolicy.ValidHours(request.ValidHours)} hours."
                + Environment.NewLine
                + "If you were not expecting this, ignore it — nothing happens until somebody uses the link.",
                context.RequestAborted);

            logger.LogWarning("'{Actor}' invited {Email} as {Role}", actor, email, role);

            // The link goes back either way. If the relay is not set up, or refused
            // it, the admin can still pass it on by hand — an invitation that
            // silently went nowhere would be worse than one that has to be copied.
            return new { ok = true, id, link, emailed = failure is null, mailProblem = failure };
        });

        app.MapDelete("/api/invites/{id}", async Task<object?> (string id, InvitationStore store) =>
        {
            await Task.CompletedTask;
            var now = DateTimeOffset.UtcNow;
            store.Update<object?>(invitations =>
            {
                var invitation = invitations.Find(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal))
                    ?? throw new KeyNotFoundException($"No invitation '{id}'.");

                if (invitation.AcceptedAt is not null)
                {
                    // Withdrawing something already used would say the account
                    // should not exist, which is a different operation on a
                    // different page.
                    throw new InvalidOperationException("That invitation has already been accepted.");
                }

                invitation.RevokedAt = now;
                return null;
            });

            return new { ok = true };
        });

        // ---- the two anonymous routes the invitee uses --------------------------

        app.MapGet("/api/auth/invite", async Task<IResult> (HttpContext context, InvitationStore store) =>
        {
            await Task.CompletedTask;
            var now = DateTimeOffset.UtcNow;
            if (Find(store, context.Request.Query["token"].ToString(), now) is not { } invitation)
            {
                return Error(410, "That invitation link is not valid any more.");
            }

            // The email and the role, so the form can say who this is for. Not who
            // sent it, and nothing about the server: this route is anonymous.
            return Results.Json(new { invitation.Email, invitation.Role, invitation.ExpiresAt });
        });

        app.MapPost("/api/auth/invite/accept", async Task<IResult> (
            HttpContext context,
            InvitationStore store,
            UiConfigStore users,
            TeamStore teams,
            SessionStore sessions) =>
        {
            InviteAcceptRequest? request;
            try
            {
                request = await context.Request.ReadFromJsonAsync<InviteAcceptRequest>();
            }
            catch (System.Text.Json.JsonException)
            {
                return Error(400, "Invalid request body.");
            }

            var now = DateTimeOffset.UtcNow;
            if (Find(store, request?.Token, now) is not { } invitation)
            {
                return Error(410, "That invitation link is not valid any more.");
            }

            var username = (request?.Username ?? string.Empty).Trim();
            if (UsernamePolicy.Validate(username) is { } nameRejection)
            {
                return Error(400, nameRejection);
            }

            var password = request?.Password ?? string.Empty;
            if (PasswordPolicy.Validate(password) is { } rejection)
            {
                return Error(400, rejection);
            }

            // The account first: if the name is taken, the invitation has to stay
            // usable so the same person can come back and pick another one.
            try
            {
                users.Update(config =>
                {
                    if (config.Users.Any(user => string.Equals(user.Username, username, StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new InvalidOperationException($"A user named '{username}' already exists.");
                    }

                    config.Users.Add(new UserAccount
                    {
                        Username = username,
                        PasswordHash = PasswordHasher.Hash(password),
                        Role = invitation.Role,
                    });
                });
            }
            catch (InvalidOperationException taken)
            {
                return Error(409, taken.Message);
            }

            // Then spend the invitation, under the lock, and only if it is still
            // unspent — two acceptances of the same link arriving together must
            // produce one account, not two.
            var spent = store.Update(invitations =>
            {
                var stored = invitations.Find(candidate => string.Equals(candidate.Id, invitation.Id, StringComparison.Ordinal));
                if (stored is null || !stored.IsUsable(now))
                {
                    return false;
                }

                stored.AcceptedAt = now;
                stored.AcceptedAs = username;
                return true;
            });

            if (!spent)
            {
                // The account was created a moment ago by the other request. Undo
                // this one rather than leaving a second account nobody asked for.
                users.Update(config => config.Users.RemoveAll(user =>
                    string.Equals(user.Username, username, StringComparison.OrdinalIgnoreCase)));
                return Error(410, "That invitation link is not valid any more.");
            }

            if (invitation.TeamId.Length > 0)
            {
                teams.Update<object?>(directory =>
                {
                    var team = directory.Teams.Find(candidate =>
                        string.Equals(candidate.Id, invitation.TeamId, StringComparison.OrdinalIgnoreCase));

                    // A team deleted between the invitation and its acceptance is
                    // not a reason to refuse the account — it is a reason for the
                    // account to have no team.
                    team?.Members.Add(new TeamMember
                    {
                        Principal = username,
                        Role = TeamRoles.Normalize(invitation.TeamRole),
                    });
                    return null;
                });
            }

            context.Items["user"] = username;
            return Results.Json(new
            {
                token = sessions.Create(username, invitation.Role),
                username,
                role = invitation.Role,
            });
        });
    }

    /// <summary>
    /// The invitation a link names, or null. Every failure looks the same from
    /// outside — expired, withdrawn, already used and never existed are one answer,
    /// because telling them apart tells somebody holding a guess which half of it
    /// was right.
    /// </summary>
    private static Invitation? Find(InvitationStore store, string? token, DateTimeOffset now)
    {
        if (!InvitationToken.TrySplit(token, out var id, out var secret))
        {
            return null;
        }

        var invitation = store.Load().Find(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
        if (invitation is null || !invitation.IsUsable(now) || !InvitationToken.Matches(secret, invitation.Sha256))
        {
            return null;
        }

        return invitation;
    }
}
