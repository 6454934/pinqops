using System.Text;

namespace PinqOps;

/// <summary>
/// Console-backed <see cref="IPrompt"/>. Secrets are read without echoing; when
/// input is redirected (no interactive terminal), it falls back to a plain read.
/// </summary>
internal sealed class ConsolePrompt : IPrompt
{
    public string Ask(string question)
    {
        Console.Write(question);
        return Console.ReadLine() ?? string.Empty;
    }

    public string AskSecret(string question)
    {
        Console.Write(question);
        return Console.IsInputRedirected ? Console.ReadLine() ?? string.Empty : ReadMasked();
    }

    private static string ReadMasked()
    {
        var builder = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (builder.Length > 0)
                {
                    builder.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                builder.Append(key.KeyChar);
            }
        }

        return builder.ToString();
    }
}
