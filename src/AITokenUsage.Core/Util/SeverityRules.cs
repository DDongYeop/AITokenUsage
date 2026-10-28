using AITokenUsage.Core.Models;

namespace AITokenUsage.Core.Util;

public static class SeverityRules
{
    public const double WarningPercent = 75, CriticalPercent = 90;

    /// <summary>The provider's severity wins; otherwise used ≥ 90 % is critical and ≥ 75 % is warning.</summary>
    public static Severity Effective(UsageMeter meter)
    {
        if (meter.ServerSeverity is Severity serverSeverity)
        {
            return serverSeverity;
        }

        double? used = Clamp(meter.UsedPercent);
        if (used is null)
        {
            return Severity.Normal;
        }

        if (used >= CriticalPercent)
        {
            return Severity.Critical;
        }

        return used >= WarningPercent ? Severity.Warning : Severity.Normal;
    }

    /// <summary>Clamps to 0..100. NaN becomes null.</summary>
    public static double? Clamp(double? percent)
    {
        if (percent is not double value || double.IsNaN(value))
        {
            return null;
        }

        return Math.Clamp(value, 0, 100);
    }
}
