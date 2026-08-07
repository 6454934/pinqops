namespace PinqOps.Tests.Fakes;

/// <summary>Returns queued answers and records the questions asked.</summary>
public sealed class FakePrompt : IPrompt
{
    private readonly Queue<string> _answers;

    public FakePrompt(params string[] answers)
    {
        _answers = new Queue<string>(answers);
    }

    public List<string> Questions { get; } = new();

    public string Ask(string question) => Next(question);

    public string AskSecret(string question) => Next(question);

    private string Next(string question)
    {
        Questions.Add(question);
        return _answers.Count > 0 ? _answers.Dequeue() : string.Empty;
    }
}
