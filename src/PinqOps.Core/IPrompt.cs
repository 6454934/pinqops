namespace PinqOps;

/// <summary>
/// Asks the user for input during interactive setup. Abstracted so the wizard
/// and token resolver can be unit-tested without a console.
/// </summary>
public interface IPrompt
{
    /// <summary>Asks a question and returns the typed answer.</summary>
    string Ask(string question);

    /// <summary>Asks for a secret; the console implementation does not echo it.</summary>
    string AskSecret(string question);
}
