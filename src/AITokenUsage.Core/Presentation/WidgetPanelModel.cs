using AITokenUsage.Core.Models;

namespace AITokenUsage.Core.Presentation;

public enum WidgetRowKind
{
    /// <summary>Label + 44 DIP bar + value.</summary>
    Meter,

    /// <summary>A text line (TextOverride) such as "↻ 11/1" or "12 크레딧 사용". No bar.</summary>
    Text,

    /// <summary>A muted status line such as "로그인 필요".</summary>
    Message,
}

/// <summary>One line of the widget's right column.</summary>
public sealed record WidgetRow(WidgetRowKind Kind, string Label, double? Fraction, string Value, Severity Severity, bool Dimmed)
{
    public static WidgetRow Message(string text) => new(WidgetRowKind.Message, "", null, text, Severity.Normal, false);
}

/// <summary>Everything one provider panel draws. When Row2 is null, Row1 is centred across both lines.</summary>
public sealed record WidgetPanelModel(
    string ProviderId,
    string DisplayName,
    bool IsStale,
    WidgetRow Row1,
    WidgetRow? Row2,
    int DotCount,
    int DotIndex)
{
    /// <summary>Position dots are shown only when two or more providers rotate (spec §2.1).</summary>
    public bool ShowDots => DotCount >= 2;
}
