namespace AITokenUsage.Core.Presentation;

/// <summary>A connected monitor in physical screen pixels (from System.Windows.Forms.Screen in the app).</summary>
public sealed record MonitorDescriptor(string DeviceName, int Left, int Top, int Width, int Height, bool IsPrimary);

/// <summary>One monitor drop-down item. DeviceName null = "주 모니터" (follow the primary monitor).</summary>
public sealed record MonitorChoice(string? DeviceName, string Label);

public static class MonitorChoices
{
    public const string PrimaryLabel = "주 모니터";

    /// <summary>"주 모니터", then "모니터 N (W×H, 왼쪽|오른쪽|위|아래|주)" sorted left-to-right, then a disconnected saved choice.</summary>
    public static IReadOnlyList<MonitorChoice> Build(IReadOnlyList<MonitorDescriptor> monitors, string? selectedDeviceName)
    {
        ArgumentNullException.ThrowIfNull(monitors);

        var result = new List<MonitorChoice> { new(null, PrimaryLabel) };
        MonitorDescriptor? primary = monitors.FirstOrDefault(m => m.IsPrimary);
        List<MonitorDescriptor> ordered = monitors.OrderBy(m => m.Left).ThenBy(m => m.Top).ToList();

        for (int i = 0; i < ordered.Count; i++)
        {
            MonitorDescriptor monitor = ordered[i];
            result.Add(new MonitorChoice(
                monitor.DeviceName,
                $"모니터 {i + 1} ({monitor.Width}×{monitor.Height}, {Describe(monitor, primary)})"));
        }

        bool selectedIsConnected = monitors.Any(m =>
            string.Equals(m.DeviceName, selectedDeviceName, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(selectedDeviceName) && !selectedIsConnected)
        {
            result.Add(new MonitorChoice(selectedDeviceName, $"{selectedDeviceName} (연결 안 됨)"));
        }

        return result;
    }

    /// <summary>Index of the choice for <paramref name="deviceName"/> (null = primary entry); 0 when absent.</summary>
    public static int IndexOf(IReadOnlyList<MonitorChoice> choices, string? deviceName)
    {
        ArgumentNullException.ThrowIfNull(choices);

        for (int i = 0; i < choices.Count; i++)
        {
            if (string.Equals(choices[i].DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return 0;
    }

    private static string Describe(MonitorDescriptor monitor, MonitorDescriptor? primary)
    {
        if (monitor.IsPrimary)
        {
            return "주";
        }

        if (primary is null)
        {
            return "보조";
        }

        int right = monitor.Left + monitor.Width;
        int bottom = monitor.Top + monitor.Height;
        int primaryRight = primary.Left + primary.Width;
        int primaryBottom = primary.Top + primary.Height;

        if (right <= primary.Left)
        {
            return "왼쪽";
        }

        if (monitor.Left >= primaryRight)
        {
            return "오른쪽";
        }

        if (bottom <= primary.Top)
        {
            return "위";
        }

        return monitor.Top >= primaryBottom ? "아래" : "보조";
    }
}
