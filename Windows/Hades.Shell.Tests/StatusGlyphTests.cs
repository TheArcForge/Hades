using Hades.Control.Client.Dtos;

namespace Hades.Shell.Tests;

public class StatusGlyphTests
{
    [Theory]
    [InlineData(ControlIconState.Idle)]
    [InlineData(ControlIconState.Indexing)]
    [InlineData(ControlIconState.Attached)]
    [InlineData(ControlIconState.LeaseHeld)]
    [InlineData(ControlIconState.Error)]
    [InlineData(ControlIconState.Unknown)]
    public void EveryIconStateHasAGlyph(ControlIconState state)
    {
        Assert.False(string.IsNullOrEmpty(StatusGlyph.For(state)));
    }

    [Theory]
    [InlineData(ControlSeverity.Ok)]
    [InlineData(ControlSeverity.Warning)]
    [InlineData(ControlSeverity.Error)]
    [InlineData(ControlSeverity.Unknown)]
    public void EverySeverityHasAGlyph(ControlSeverity severity)
    {
        Assert.False(string.IsNullOrEmpty(StatusGlyph.For(severity)));
    }

    [Theory]
    [InlineData(OperationState.Running)]
    [InlineData(OperationState.Done)]
    [InlineData(OperationState.Failed)]
    [InlineData(OperationState.Unknown)]
    public void EveryOperationStateHasAGlyph(OperationState state)
    {
        Assert.False(string.IsNullOrEmpty(StatusGlyph.For(state)));
    }

    [Theory]
    [InlineData(TraceOutcome.Ok)]
    [InlineData(TraceOutcome.Error)]
    [InlineData(TraceOutcome.Unknown)]
    public void EveryTraceOutcomeHasAGlyph(TraceOutcome outcome)
    {
        Assert.False(string.IsNullOrEmpty(StatusGlyph.For(outcome)));
    }

    // The whole point of an Unknown member is that a NEWER core can add a case and an OLDER shell
    // still renders something rather than crashing. That is only true if the switch has a default.
    [Fact]
    public void AnUnrecognisedValueFallsBackRatherThanThrowing()
    {
        Assert.False(string.IsNullOrEmpty(StatusGlyph.For((ControlIconState)9999)));
    }
}
