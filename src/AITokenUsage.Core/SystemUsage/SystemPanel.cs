using System.Globalization;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Presentation;
using AITokenUsage.Core.Resources;
using AITokenUsage.Core.Settings;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.SystemUsage;

/// <summary>
/// The "PC" virtual panel: CPU and memory shown like a provider (rotation panel, detail card, tooltip).
/// Values are always "used %"; the ValueMode setting applies to AI limits only.
/// </summary>
public static class SystemPanel
{
    /// <summary>Virtual provider id. Not part of ProviderIds.All.</summary>
    public const string Id = "system";

    public const string CpuKey = "cpu";
    public const string RamKey = "ram";

    private const double BytesPerGib = 1024d * 1024 * 1024;

    public static string DisplayName => Strings.SystemPanelName;

    /// <summary>A provider-shaped view (status Ok, meters "cpu" and "ram") so rotation, dots and switching work unchanged.</summary>
    public static ProviderView ToView(SystemUsageSample? sample)
    {
        UsageMeter[] meters =
        [
            new(CpuKey, Strings.SystemCpuLabel, Strings.SystemCpuLabel, sample?.CpuPercent, null),
            new(RamKey, Strings.SystemRamLabel, Strings.SystemMemoryLabel, sample?.MemoryPercent, null),
        ];

        var snapshot = new UsageSnapshot(Id, ProviderStatus.Ok, null, meters, sample?.At ?? DateTimeOffset.UnixEpoch);
        return new ProviderView(Id, DisplayName, snapshot, snapshot);
    }

    /// <summary>Widget panel: row 1 CPU, row 2 RAM, no reset-time column.</summary>
    public static WidgetPanelModel BuildWidgetModel(SystemUsageSample? sample, int dotCount, int dotIndex) =>
        WidgetPanelBuilder.Build(ToView(sample), CpuKey, RamKey, ValueMode.Used, dotCount, dotIndex, DateTimeOffset.UnixEpoch);

    /// <summary>Detail card: title "PC", CPU row, memory row (with "19.5 / 32 GB" under it) and a refresh note.</summary>
    public static DetailCardModel BuildCard(SystemUsageSample? sample)
    {
        ProviderView view = ToView(sample);
        UsageMeter cpu = view.DisplayMeters[0];
        UsageMeter ram = view.DisplayMeters[1];

        CardRow[] rows =
        [
            ToCardRow(cpu, ""),
            ToCardRow(ram, sample is null || sample.MemoryTotalBytes <= 0 ? "" : MemoryText(sample)),
        ];

        return new DetailCardModel(Id, DisplayName, null, rows, null, [Strings.SystemCardStatus], false);
    }

    /// <summary>
    /// "19.5 / 32 GB": used with one decimal, total rounded to a whole number (GiB).
    /// A dash when the total is unknown (zero or less), never "0.0 / 0 GB".
    /// </summary>
    public static string MemoryText(SystemUsageSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (sample.MemoryTotalBytes <= 0)
        {
            return MeterSelector.MissingValue;
        }

        string used = (sample.MemoryUsedBytes / BytesPerGib).ToString("0.0", CultureInfo.InvariantCulture);
        string total = Math.Round(sample.MemoryTotalBytes / BytesPerGib, MidpointRounding.AwayFromZero)
            .ToString("0", CultureInfo.InvariantCulture);
        return string.Format(CultureInfo.CurrentCulture, Strings.SystemMemoryText, used, total);
    }

    /// <summary>One-line card footer: "CPU 23% · 메모리 19.5 / 32 GB". Missing values show a dash.</summary>
    public static string CardLine(SystemUsageSample? sample) =>
        string.Format(
            CultureInfo.CurrentCulture,
            Strings.SystemCardLine,
            PercentText(sample?.CpuPercent),
            sample is null ? MeterSelector.MissingValue : MemoryText(sample));

    /// <summary>Tray tooltip line: "PC CPU 23% · RAM 61%".</summary>
    public static string TooltipLine(SystemUsageSample? sample) =>
        string.Format(
            CultureInfo.CurrentCulture,
            Strings.SystemTooltipLine,
            PercentText(sample?.CpuPercent),
            PercentText(sample?.MemoryPercent));

    /// <summary>75 % and above is a warning, 90 % and above critical (the same rule as AI limits). Null is normal.</summary>
    public static Severity SeverityFor(double? percent) =>
        SeverityRules.Effective(new UsageMeter("", "", "", percent, null));

    private static CardRow ToCardRow(UsageMeter meter, string resetText) =>
        new(
            meter.LongLabel,
            MeterFormatter.BarFraction(meter, ValueMode.Used),
            MeterFormatter.ValueText(meter, ValueMode.Used),
            resetText,
            SeverityFor(meter.UsedPercent),
            false,
            false);

    private static string PercentText(double? percent) =>
        SeverityRules.Clamp(percent) is double value
            ? string.Create(CultureInfo.InvariantCulture, $"{Math.Round(value, MidpointRounding.AwayFromZero):0}%")
            : MeterSelector.MissingValue;
}
