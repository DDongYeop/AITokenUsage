namespace AITokenUsage.Core.Settings;

/// <summary>Where the widget sits on the taskbar (설정: 작업 표시줄 왼쪽 끝 / 시작 버튼 왼쪽 / 알림 영역 왼쪽).</summary>
public enum WidgetPlacement
{
    LeftEdge,
    BeforeStart,
    BeforeTray,
}

/// <summary>Show used % (default) or remaining % (= 100 − used).</summary>
public enum ValueMode
{
    Used,
    Remaining,
}

/// <summary>Auto follows the taskbar theme; Light/Dark force the text color.</summary>
public enum TextColorMode
{
    Auto,
    Light,
    Dark,
}
