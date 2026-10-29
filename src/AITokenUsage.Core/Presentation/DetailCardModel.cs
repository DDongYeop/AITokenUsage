using AITokenUsage.Core.Models;

namespace AITokenUsage.Core.Presentation;

/// <summary>One limit row of the detail card. Text rows (IsText) have no bar.</summary>
public sealed record CardRow(
    string Label,
    double? Fraction,
    string Value,
    string ResetText,
    Severity Severity,
    bool IsText,
    bool Dimmed);

/// <summary>Everything the hover card shows for the provider currently on the widget.</summary>
public sealed record DetailCardModel(
    string ProviderId,
    string DisplayName,
    string? Plan,
    IReadOnlyList<CardRow> Rows,
    string? ExtraLine,
    IReadOnlyList<string> StatusLines,
    bool IsStale,
    string? SystemLine = null);
