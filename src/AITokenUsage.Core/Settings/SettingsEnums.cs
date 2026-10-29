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

/// <summary>Where PC usage (CPU and memory) is shown. Off by default.</summary>
public enum SystemMonitorMode
{
    /// <summary>Not shown and not measured.</summary>
    Off,

    /// <summary>A "PC" panel at the end of the rotation.</summary>
    RotationPanel,

    /// <summary>A two-line column beside the AI panel, always visible.</summary>
    AlwaysBeside,

    /// <summary>Only a line on the detail card and in the tray tooltip.</summary>
    CardOnly,
}

/// <summary>Auto follows the taskbar theme; Light/Dark force the text color.</summary>
public enum TextColorMode
{
    Auto,
    Light,
    Dark,
}
