namespace AITokenUsage.Core.Presentation;

/// <summary>What a guide step's button does. Steps without a button use <see cref="None"/>.</summary>
public enum GuideAction
{
    None,
    OpenTerminal,
    Recheck,
}

/// <summary>
/// One line of a connection guide. <paramref name="Command"/> (if any) is shown with a copy button;
/// <paramref name="TerminalCommand"/> is what <see cref="GuideAction.OpenTerminal"/> runs.
/// </summary>
public sealed record GuideStep(string Text, string? Command, GuideAction Action, string? TerminalCommand = null);

/// <summary>Ordered steps plus an optional note shown under them (spec §4.4).</summary>
public sealed record ConnectionGuide(IReadOnlyList<GuideStep> Steps, string? Footnote);
