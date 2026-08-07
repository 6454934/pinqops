using System.Text.Json;
using System.Text.Json.Nodes;

namespace PinqOps.Web;

/// <summary>
/// Masks the secret-bearing fields of a <c>docker inspect</c> payload.
///
/// <c>docker inspect</c> returns <c>Config.Env</c> verbatim, and by convention
/// that is exactly where a container keeps its database password, API keys and
/// tokens. The dashboard hands the payload to any caller that manages the
/// container, so everything below admin sees the variable <em>names</em> — which
/// is what makes inspect useful — with the values masked.
///
/// The command line is masked too, and wholesale. <see cref="AppCatalog"/> puts
/// generated passwords straight into argv — <c>redis-server --requirepass …</c>,
/// <c>nats --auth …</c>, <c>surreal start --pass …</c> — so <c>Config.Cmd</c>,
/// <c>Args</c>, <c>Config.Entrypoint</c> and <c>Path</c> are every bit as
/// sensitive as <c>Env</c>. Masking only the flag's value would leak the moment
/// a catalog entry uses a spelling this file has not been taught, so the whole
/// argv is replaced.
/// </summary>
public static class SecretRedactor
{
    /// <summary>The replacement value, matching the mask used elsewhere in the API.</summary>
    public const string Mask = "••••••••";

    /// <summary>Keys whose value is an argv array that may carry a secret.
    /// <c>Test</c> is the healthcheck argv — <c>redis-cli -a &lt;password&gt; ping</c>
    /// is the canonical healthcheck, so it carries secrets like any other argv.</summary>
    private static readonly string[] ArgumentArrays = ["Cmd", "Args", "Entrypoint", "Test"];

    /// <summary>Keys whose value is a single command string.</summary>
    private static readonly string[] ArgumentStrings = ["Path", "Command"];

    /// <summary>
    /// A copy of <paramref name="source"/> with every <c>Env</c> array's values
    /// masked. The key is preserved (<c>POSTGRES_PASSWORD=••••••••</c>) so the
    /// shape of the container's configuration stays readable. Any <c>Env</c> is
    /// masked wherever it appears, not just under <c>Config</c>, so a payload
    /// shape change upstream cannot silently unmask a secret.
    /// </summary>
    public static JsonNode? RedactInspect(JsonElement source)
    {
        var node = JsonNode.Parse(source.GetRawText());
        Redact(node);
        return node;
    }

    /// <summary>
    /// A copy of a <c>docker ps</c> row with its <c>Command</c> masked. That field
    /// is the same argv <see cref="RedactInspect"/> masks, and the container list
    /// sits at the plain "read" scope — so without this a viewer reads a catalog
    /// app's generated password out of the table it renders by default.
    /// </summary>
    public static JsonNode? RedactListing(JsonElement source)
    {
        var node = JsonNode.Parse(source.GetRawText());
        Redact(node);
        return node;
    }

    private static void Redact(JsonNode? node)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (var item in array)
                {
                    Redact(item);
                }

                break;

            case JsonObject envelope:
                if (envelope["Env"] is JsonArray environment)
                {
                    MaskEnvironment(environment);
                }

                foreach (var key in ArgumentArrays)
                {
                    if (envelope[key] is JsonArray arguments)
                    {
                        MaskArguments(arguments);
                    }
                }

                foreach (var key in ArgumentStrings)
                {
                    if (envelope[key] is JsonValue command && command.TryGetValue<string>(out _))
                    {
                        envelope[key] = Mask;
                    }
                }

                foreach (var (_, value) in envelope)
                {
                    Redact(value);
                }

                break;
        }
    }

    /// <summary>
    /// Replaces an argv array with a single mask. Keeping the argument count would
    /// leak nothing, but keeping any element risks leaking the secret itself —
    /// which of them carries it depends on the image.
    /// </summary>
    private static void MaskArguments(JsonArray arguments)
    {
        if (arguments.Count == 0)
        {
            return;
        }

        arguments.Clear();
        arguments.Add(Mask);
    }

    /// <summary>Masks each <c>KEY=value</c> entry in place, keeping the key.</summary>
    private static void MaskEnvironment(JsonArray environment)
    {
        for (var index = 0; index < environment.Count; index++)
        {
            if (environment[index] is not JsonValue value
                || !value.TryGetValue<string>(out var entry))
            {
                continue;
            }

            var separator = entry.IndexOf('=');

            // An entry with no '=' is not a name/value pair, so there is no name
            // worth keeping — mask the whole thing.
            environment[index] = separator < 0 ? Mask : string.Concat(entry.AsSpan(0, separator + 1), Mask);
        }
    }
}
