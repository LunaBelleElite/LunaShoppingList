namespace LunaShoppingList.Core.Tests;

public class LifestreamNoteTests
{
    [Fact]
    public void NotAvailable_ignores_the_world() =>
        Assert.Equal("Lifestream is not installed or not loaded.", LifestreamNote.Line(LifestreamOutcome.NotAvailable, "Cerberus"));

    [Fact]
    public void Busy_ignores_the_world() =>
        Assert.Equal("Lifestream is busy.", LifestreamNote.Line(LifestreamOutcome.Busy, "Cerberus"));

    [Fact]
    public void Refused_names_the_world() =>
        Assert.Equal("Lifestream can't go to Cerberus right now (busy, or the world is not reachable from here).",
            LifestreamNote.Line(LifestreamOutcome.Refused, "Cerberus"));

    [Fact]
    public void Requested_names_the_world_and_does_not_promise_arrival() =>
        Assert.Equal("Requesting travel to Cerberus via Lifestream.",
            LifestreamNote.Line(LifestreamOutcome.Requested, "Cerberus"));

    [Fact]
    public void Every_outcome_has_a_line()
    {
        foreach (var o in Enum.GetValues<LifestreamOutcome>())
            Assert.NotEmpty(LifestreamNote.Line(o, "Odin"));
    }
}
