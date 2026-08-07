namespace PinqOps.Web;

/// <summary>
/// The WebSocket routes.
///
/// <para>Right now there is one, and it is a diagnostic. WebSockets are the part
/// of a self-hosted setup most likely to be broken by something between the
/// browser and pinqops — an nginx without <c>Upgrade</c> headers, a corporate proxy
/// that buffers, a tunnel that drops silent connections. The features that will
/// need them (a container console, live log tailing) are the worst possible place
/// to discover that: they fail with an empty pane and nothing to read. This route
/// lets an operator prove the path works first, and says which limit it hit if it
/// does not.</para>
/// </summary>
public static class WebSocketEndpoints
{
    public static void MapWebSocketEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/ws/ping", (HttpContext context, WebSocketChannel channel) =>
            channel.Run(context, async session =>
            {
                // Echoes each message back. Enough to prove frames survive the round
                // trip in both directions, which is exactly what a proxy breaks.
                while (await session.Receive() is { } message)
                {
                    await session.Send(message);
                }
            }));

        app.MapGet("/api/ws/containers/{id}/console", (
            string id, HttpContext context, WebSocketChannel channel, AuditLog audit, ILoggerFactory loggers) =>
            channel.Run(context, async session =>
            {
                var logger = loggers.CreateLogger("PinqOps.Web.ContainerConsole");

                // The daemon the request selected, not whichever one this process
                // happens to sit next to. The resource gate authorized this against
                // the ownership records and grants of `?env=`, so a console pinned
                // to the local daemon would check one host and open a prompt on
                // another — with the audit line naming the host it did not use.
                var environment = EndpointHelpers.EnvEndpoint(context);
                await using var console = ContainerConsole.Start(id, environment);
                var budget = new ConsoleOutputBudget();

                // Output is pumped independently of input: a command that prints for
                // a minute must keep arriving while the operator types the next one,
                // and a loop that alternated the two would deadlock on the first
                // program that waits for something.
                var pump = Task.Run(async () =>
                {
                    await foreach (var line in console.Output.WithCancellation(session.Cancelled))
                    {
                        if (budget.Take(line) is { } allowed)
                        {
                            await session.Send(allowed);
                        }
                        else if (budget.Exhausted)
                        {
                            await session.Send("… output stopped: this command printed more than the console shows.");
                            // Said once, then the rest is dropped silently until the
                            // next command resets the budget.
                            budget.Take(new string(' ', 1));
                        }
                    }
                });

                logger.LogWarning(
                    "Console opened on {Container} ({Environment}) by {User}", id, environment.Id, session.User);

                while (await session.Receive() is { } line)
                {
                    // Every command, before it runs. A console is the one place in
                    // pinqops where an operator can do anything at all, so the trail
                    // has to carry what was typed rather than "a console was opened".
                    audit.Append(new AuditEntry(
                        DateTimeOffset.UtcNow,
                        session.User,
                        "container.console",
                        id,
                        line,
                        Status: 200)
                    {
                        Environment = environment.Id,
                    });

                    budget.Reset();
                    if (!await console.SendAsync(line, session.Cancelled))
                    {
                        await session.Send("… the shell has exited.");
                        break;
                    }
                }

                await pump.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None)
                    .ContinueWith(_ => { }, TaskScheduler.Default);
            })).RequireResourceAccess(ResourceKinds.Container, ResourceIdSource.RouteId, GrantAccess.Manage);
    }
}
