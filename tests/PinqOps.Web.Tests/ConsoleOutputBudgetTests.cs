using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// The ceiling on one command's output. A <c>cat</c> of a large log would otherwise
/// push megabytes through a socket that carries 64KB at a time, one message at a
/// time, for minutes — during which the console accepts nothing else.
/// </summary>
public class ConsoleOutputBudgetTests
{
    private const int Ceiling = ContainerConsole.MaximumOutputCharactersPerCommand;

    [Fact]
    public void AnOrdinaryLinePassesThroughUnchanged()
    {
        var budget = new ConsoleOutputBudget();

        Assert.Equal("total 12", budget.Take("total 12"));
        Assert.False(budget.Exhausted);
    }

    /// <summary>
    /// The line that crosses the ceiling is sent truncated rather than dropped: a
    /// cut nobody can see reads as the command having finished.
    /// </summary>
    [Fact]
    public void TheLineThatCrossesTheCeilingIsCutVisibly()
    {
        var budget = new ConsoleOutputBudget();
        budget.Take(new string('x', Ceiling - 10));

        var last = budget.Take(new string('y', 100));

        Assert.NotNull(last);
        Assert.Contains("(truncated)", last);
        Assert.True(budget.Exhausted);
    }

    [Fact]
    public void EverythingAfterTheCeilingIsDropped()
    {
        var budget = new ConsoleOutputBudget();
        budget.Take(new string('x', Ceiling));

        Assert.Null(budget.Take("more"));
    }

    /// <summary>
    /// Per command, not per session: a console that stopped printing after one large
    /// file would be a console that had to be reopened to be used again.
    /// </summary>
    [Fact]
    public void TheNextCommandStartsWithAFullBudget()
    {
        var budget = new ConsoleOutputBudget();
        budget.Take(new string('x', Ceiling));
        Assert.Null(budget.Take("dropped"));

        budget.Reset();

        Assert.Equal("total 12", budget.Take("total 12"));
        Assert.False(budget.Exhausted);
    }

    [Fact]
    public void AnEmptyLineCostsNothingAndIsStillSent()
    {
        // A blank line is output — a shell prints them — and charging for it would
        // make a quiet command exhaust the budget on nothing.
        var budget = new ConsoleOutputBudget();

        Assert.Equal(string.Empty, budget.Take(string.Empty));
        Assert.False(budget.Exhausted);
    }
}
