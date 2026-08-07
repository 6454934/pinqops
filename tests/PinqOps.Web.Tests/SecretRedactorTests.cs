using System.Text.Json;
using System.Text.Json.Nodes;
using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

public class SecretRedactorTests
{
    private static JsonNode Redact(string json) =>
        SecretRedactor.RedactInspect(JsonDocument.Parse(json).RootElement)!;

    /// <summary>Stands in for an entry the daemon did not send as a string.</summary>
    private const string NotAString = "<non-string>";

    /// <summary>
    /// The serialized form escapes the mask's non-ASCII bullets, so the entries
    /// are read back as values rather than matched against the JSON text.
    /// </summary>
    private static string[] EnvOf(JsonNode node) =>
        [.. node[0]!["Config"]!["Env"]!.AsArray()
            .Select(entry => entry is JsonValue value && value.TryGetValue<string>(out var text) ? text : NotAString)];

    // `docker inspect` returns a single-element array, which is the shape the
    // dashboard hands straight to the browser.
    [Fact]
    public void MasksEnvValuesInsideAnInspectArray()
    {
        var redacted = Redact("""
            [{"Id":"abc","Config":{"Env":["POSTGRES_PASSWORD=s3cr3t","PATH=/usr/bin"]}}]
            """);

        Assert.Equal(
            ["POSTGRES_PASSWORD=" + SecretRedactor.Mask, "PATH=" + SecretRedactor.Mask],
            EnvOf(redacted));
        Assert.DoesNotContain("s3cr3t", redacted.ToJsonString());
        Assert.DoesNotContain("/usr/bin", redacted.ToJsonString());
    }

    [Fact]
    public void KeepsEverythingThatIsNotAnEnvValue()
    {
        var result = Redact("""
            [{"Id":"abc","Name":"/web","Config":{"Image":"nginx","Env":["A=b"]}}]
            """).ToJsonString();

        Assert.Contains("\"Id\":\"abc\"", result);
        Assert.Contains("\"Name\":\"/web\"", result);
        Assert.Contains("\"Image\":\"nginx\"", result);
    }

    // The mask is applied wherever an Env array appears, so a payload shape
    // change upstream cannot silently unmask a secret.
    [Fact]
    public void MasksEnvOutsideConfigToo()
    {
        var result = Redact("""[{"Something":{"Nested":{"Env":["TOKEN=abcd1234"]}}}]""");

        Assert.DoesNotContain("abcd1234", result.ToJsonString());
    }

    // An entry with no '=' has no name worth keeping.
    [Fact]
    public void MasksAnEntryWithNoSeparatorEntirely()
    {
        var redacted = Redact("""[{"Config":{"Env":["bare-secret"]}}]""");

        Assert.Equal([SecretRedactor.Mask], EnvOf(redacted));
        Assert.DoesNotContain("bare-secret", redacted.ToJsonString());
    }

    [Fact]
    public void HandlesAnEmptyOrAbsentEnv()
    {
        Assert.Empty(EnvOf(Redact("""[{"Config":{"Env":[]}}]""")));
        Assert.Contains("nginx", Redact("""[{"Config":{"Image":"nginx"}}]""").ToJsonString());
    }

    // A non-string entry must not throw — the payload comes from the daemon.
    [Fact]
    public void IgnoresNonStringEnvEntries()
    {
        var redacted = Redact("""[{"Config":{"Env":[42,null,"A=b"]}}]""");

        Assert.Equal([NotAString, NotAString, "A=" + SecretRedactor.Mask], EnvOf(redacted));
        Assert.Contains("42", redacted.ToJsonString());
    }
}
