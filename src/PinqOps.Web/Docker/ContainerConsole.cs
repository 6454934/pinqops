using System.Diagnostics;
using System.Text;

namespace PinqOps.Web;

/// <summary>
/// A line-oriented shell inside a running container.
///
/// <para><b>Line-oriented, deliberately, and the page says so.</b> A real terminal
/// means a pseudo-terminal, escape sequences, cursor addressing and a client that
/// understands all three — in this dashboard that would be xterm.js inlined into a
/// single CSP-hashed script, taking the page from 470KB to about 750KB and forcing
/// the hash to be recomputed on every release. What people actually reach for a
/// console for is <c>psql</c>, <c>redis-cli</c>, <c>ls</c> and <c>cat</c>, and those
/// work perfectly well on a pipe. What will not work is anything that redraws:
/// <c>top</c>, <c>vim</c>, a progress bar. Saying that plainly is better than a
/// terminal that is almost one.</para>
///
/// <para><b>It is one <c>docker exec -i</c>, not one per line.</b> A shell that is
/// restarted for every command has no working directory, no shell variables and no
/// open <c>psql</c> session — which is to say it is not a shell, it is the one-shot
/// exec that already exists.</para>
/// </summary>
public sealed class ContainerConsole : IAsyncDisposable
{
    /// <summary>
    /// The shell to ask for. <c>sh</c> rather than <c>bash</c>: every image that has
    /// bash has sh, and the alpine-based ones that most containers are do not have
    /// bash at all.
    /// </summary>
    public const string Shell = "sh";

    /// <summary>
    /// How much output one line of input may produce before the rest is dropped. A
    /// <c>cat</c> of a large log would otherwise push megabytes through a socket
    /// that caps each message at 64KB, one message at a time, for minutes.
    /// </summary>
    public const int MaximumOutputCharactersPerCommand = 200_000;

    private static readonly string[] ExecArguments = ["exec", "-i", "--"];

    private readonly Process _process;
    private readonly Channel _output = new();

    private ContainerConsole(Process process) => _process = process;

    /// <summary>Output the container has produced, in arrival order.</summary>
    public IAsyncEnumerable<string> Output => _output.Read();

    /// <summary>
    /// Starts a shell in <paramref name="container"/>.
    ///
    /// <para>Not through <see cref="IProcessRunner"/>: that runs a process to
    /// completion and hands back its output, which is the opposite of what a session
    /// is. This owns the process for as long as the socket lives.</para>
    /// </summary>
    public static ContainerConsole Start(string container, DockerEndpoint? endpoint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(container);

        var startInfo = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in ArgumentsFor(endpoint ?? DockerEndpoint.Local, container))
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };
        var console = new ContainerConsole(process);

        process.OutputDataReceived += (_, eventArgs) => console._output.Write(eventArgs.Data);
        // Both streams, interleaved, because a shell writes its errors to stderr and
        // a console that shows only stdout reports "command not found" as silence.
        process.ErrorDataReceived += (_, eventArgs) => console._output.Write(eventArgs.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return console;
    }

    /// <summary>
    /// The full docker argument list a session runs, kept separate from starting
    /// the process so the one thing worth checking about it can be checked.
    ///
    /// <para>The endpoint's routing arguments come <em>first</em>: they are options
    /// to the docker client rather than to <c>exec</c>, and anything after the
    /// subcommand is passed on to the shell instead. That ordering is the whole
    /// content of this method, which is why it has a test.</para>
    /// </summary>
    internal static IReadOnlyList<string> ArgumentsFor(DockerEndpoint endpoint, string container)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(container);

        // -i without -t: stdin stays open so this is a session, and no pseudo-terminal
        // is allocated so nothing emits escape sequences the client cannot render.
        // `--` before the container name, as everywhere else.
        return [.. endpoint.Arguments, .. ExecArguments, container, Shell];
    }

    /// <summary>Sends one line to the shell. Returns false when it has already exited.</summary>
    public async Task<bool> SendAsync(string line, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (_process.HasExited)
        {
            return false;
        }

        try
        {
            // A single newline, whatever the client sent. A line with an embedded
            // one would otherwise be two commands, which is not what "send this
            // line" means and is how a paste becomes something nobody typed.
            await _process.StandardInput.WriteLineAsync(
                line.ReplaceLineEndings(" ").AsMemory(), cancellationToken).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _output.Finish();

        try
        {
            if (!_process.HasExited)
            {
                // Closing stdin is how a shell is asked to leave; killing is the
                // fallback for one that is blocked in a child process.
                _process.StandardInput.Close();
                if (!_process.WaitForExit(1000))
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or NotSupportedException)
        {
        }

        _process.Dispose();
        await ValueTask.CompletedTask;
    }

    /// <summary>
    /// A bounded queue between the process's output callbacks and the socket's send
    /// loop.
    ///
    /// <para>Bounded because they run at different speeds: a container that prints a
    /// megabyte does so faster than a socket can carry it, and an unbounded queue
    /// turns that into the dashboard's memory. Dropping the overflow and saying so
    /// is the honest version.</para>
    /// </summary>
    private sealed class Channel
    {
        private readonly System.Threading.Channels.Channel<string> _lines =
            System.Threading.Channels.Channel.CreateBounded<string>(
                new System.Threading.Channels.BoundedChannelOptions(2_000)
                {
                    FullMode = System.Threading.Channels.BoundedChannelFullMode.DropWrite,
                });

        public void Write(string? line)
        {
            if (line is not null)
            {
                _lines.Writer.TryWrite(line);
            }
        }

        public void Finish() => _lines.Writer.TryComplete();

        public IAsyncEnumerable<string> Read() => _lines.Reader.ReadAllAsync();
    }
}

/// <summary>
/// Keeps one command's output inside a ceiling, so a <c>cat</c> of a large file
/// cannot hold the socket for minutes.
/// </summary>
public sealed class ConsoleOutputBudget
{
    private int _spent;

    /// <summary>
    /// What of <paramref name="line"/> may be sent, or null when the budget is gone.
    /// The line that crosses the ceiling is sent truncated, because a cut that is
    /// not visible reads as the command having finished.
    /// </summary>
    public string? Take(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var remaining = ContainerConsole.MaximumOutputCharactersPerCommand - _spent;
        if (remaining <= 0)
        {
            return null;
        }

        _spent += line.Length;
        return line.Length <= remaining ? line : line[..remaining] + " …(truncated)";
    }

    /// <summary>Whether the ceiling has been reached, so the client can be told once.</summary>
    public bool Exhausted => _spent >= ContainerConsole.MaximumOutputCharactersPerCommand;

    /// <summary>Starts the next command with a full budget.</summary>
    public void Reset() => _spent = 0;
}
