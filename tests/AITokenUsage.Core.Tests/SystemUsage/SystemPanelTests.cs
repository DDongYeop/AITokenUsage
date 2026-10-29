using AITokenUsage.Core.Models;
using AITokenUsage.Core.Presentation;
using AITokenUsage.Core.SystemUsage;

namespace AITokenUsage.Core.Tests.SystemUsage;

public class SystemPanelTests
{
    private const long Gib = 1024L * 1024 * 1024;
    private static readonly DateTimeOffset At = new(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);

    // 19.5 / 32 GiB = 60.9375 % -> "61%"
    private static SystemUsageSample Sample(double? cpu = 23, double usedGib = 19.5, double totalGib = 32) =>
        new(cpu, (long)(usedGib * Gib), (long)(totalGib * Gib), At);

    [Fact]
    public void Constants_AreStable()
    {
        Assert.Equal("system", SystemPanel.Id);
        Assert.Equal("PC", SystemPanel.DisplayName);
    }

    [Fact]
    public void ToView_HasOkStatusAndCpuRamMeters()
    {
        ProviderView view = SystemPanel.ToView(Sample());

        Assert.Equal("system", view.ProviderId);
        Assert.Equal("PC", view.DisplayName);
        Assert.Equal(ProviderStatus.Ok, view.Current.Status);
        Assert.False(view.IsStale);
        Assert.Collection(view.DisplayMeters,
            cpu =>
            {
                Assert.Equal("cpu", cpu.Key);
                Assert.Equal("CPU", cpu.ShortLabel);
                Assert.Equal(23, cpu.UsedPercent);
                Assert.Null(cpu.ResetsAt);
            },
            ram =>
            {
                Assert.Equal("ram", ram.Key);
                Assert.Equal("RAM", ram.ShortLabel);
                Assert.Equal(60.9375, ram.UsedPercent);
            });
    }

    [Fact]
    public void ToView_NullSample_KeepsMetersWithoutValues()
    {
        ProviderView view = SystemPanel.ToView(null);

        Assert.Equal(ProviderStatus.Ok, view.Current.Status);
        Assert.Equal(2, view.DisplayMeters.Count);
        Assert.All(view.DisplayMeters, m => Assert.Null(m.UsedPercent));
    }

    [Fact]
    public void BuildWidgetModel_ShowsCpuAndRamRows()
    {
        WidgetPanelModel model = SystemPanel.BuildWidgetModel(Sample(), 3, 2);

        Assert.Equal("system", model.ProviderId);
        Assert.Equal("PC", model.DisplayName);
        Assert.False(model.IsStale);
        Assert.Equal(3, model.DotCount);
        Assert.Equal(2, model.DotIndex);

        Assert.Equal(WidgetRowKind.Meter, model.Row1.Kind);
        Assert.Equal("CPU", model.Row1.Label);
        Assert.Equal("23%", model.Row1.Value);
        Assert.Equal(0.23, model.Row1.Fraction!.Value, 6);
        Assert.Equal("", model.Row1.ResetText);
        Assert.Equal(Severity.Normal, model.Row1.Severity);

        Assert.NotNull(model.Row2);
        Assert.Equal("RAM", model.Row2.Label);
        Assert.Equal("61%", model.Row2.Value);
        Assert.Equal("", model.Row2.ResetText);
    }

    [Fact]
    public void BuildWidgetModel_NullSample_ShowsMissingValueAndNoBar()
    {
        WidgetPanelModel model = SystemPanel.BuildWidgetModel(null, 1, 0);

        Assert.Equal(MeterSelector.MissingValue, model.Row1.Value);
        Assert.Null(model.Row1.Fraction);
        Assert.Equal(MeterSelector.MissingValue, model.Row2!.Value);
        Assert.Null(model.Row2.Fraction);
        Assert.Equal(Severity.Normal, model.Row1.Severity);
    }

    [Fact]
    public void BuildWidgetModel_FirstSampleWithoutCpu_ShowsMissingCpuButRealRam()
    {
        WidgetPanelModel model = SystemPanel.BuildWidgetModel(Sample(cpu: null), 2, 1);

        Assert.Equal(MeterSelector.MissingValue, model.Row1.Value);
        Assert.Equal("61%", model.Row2!.Value);
    }

    [Theory]
    [InlineData(74, Severity.Normal)]
    [InlineData(75, Severity.Warning)]
    [InlineData(89.9, Severity.Warning)]
    [InlineData(90, Severity.Critical)]
    [InlineData(100, Severity.Critical)]
    public void WidgetModel_CpuSeverity_UsesSharedThresholds(double cpu, Severity expected)
    {
        Assert.Equal(expected, SystemPanel.BuildWidgetModel(Sample(cpu), 1, 0).Row1.Severity);
        Assert.Equal(expected, SystemPanel.SeverityFor(cpu));
    }

    [Fact]
    public void SeverityFor_Null_IsNormal() => Assert.Equal(Severity.Normal, SystemPanel.SeverityFor(null));

    [Fact]
    public void BuildCard_HasTitleCpuMemoryRowsAndRefreshLine()
    {
        DetailCardModel card = SystemPanel.BuildCard(Sample());

        Assert.Equal("system", card.ProviderId);
        Assert.Equal("PC", card.DisplayName);
        Assert.Null(card.Plan);
        Assert.Null(card.ExtraLine);
        Assert.Null(card.SystemLine);
        Assert.False(card.IsStale);
        Assert.Equal(["2초마다 갱신"], card.StatusLines);

        Assert.Equal(2, card.Rows.Count);
        CardRow cpu = card.Rows[0];
        Assert.Equal("CPU", cpu.Label);
        Assert.Equal("23%", cpu.Value);
        Assert.Equal(0.23, cpu.Fraction!.Value, 6);
        Assert.Equal("", cpu.ResetText);
        Assert.False(cpu.IsText);

        CardRow memory = card.Rows[1];
        Assert.Equal("메모리", memory.Label);
        Assert.Equal("61%", memory.Value);
        Assert.Equal("19.5 / 32 GB", memory.ResetText);
        Assert.False(memory.IsText);
    }

    [Fact]
    public void BuildCard_NullSample_ShowsMissingValues()
    {
        DetailCardModel card = SystemPanel.BuildCard(null);

        Assert.All(card.Rows, r =>
        {
            Assert.Equal(MeterSelector.MissingValue, r.Value);
            Assert.Null(r.Fraction);
            Assert.Equal("", r.ResetText);
        });
    }

    [Fact]
    public void MemoryText_FormatsGibWithOneDecimalAndRoundedTotal()
    {
        Assert.Equal("19.5 / 32 GB", SystemPanel.MemoryText(Sample()));
        Assert.Equal("7.9 / 16 GB", SystemPanel.MemoryText(Sample(usedGib: 7.94, totalGib: 15.9)));
    }

    [Fact]
    public void CardLine_Korean()
    {
        Assert.Equal("CPU 23% · 메모리 19.5 / 32 GB", SystemPanel.CardLine(Sample()));
    }

    [Fact]
    public void CardLine_NullSample_UsesMissingValues()
    {
        Assert.Equal("CPU — · 메모리 —", SystemPanel.CardLine(null));
    }

    [Fact]
    public void MemoryText_UnknownTotal_ShowsMissingValue()
    {
        var unknown = new SystemUsageSample(23, 0, 0, At);

        Assert.Equal(MeterSelector.MissingValue, SystemPanel.MemoryText(unknown));
        Assert.Equal("CPU 23% · 메모리 —", SystemPanel.CardLine(unknown));
        Assert.Equal(MeterSelector.MissingValue, SystemPanel.MemoryText(new SystemUsageSample(23, 5 * Gib, -1, At)));
    }

    [Fact]
    public void BuildCard_UnknownTotal_LeavesMemoryDetailEmpty()
    {
        CardRow memory = SystemPanel.BuildCard(new SystemUsageSample(23, 0, 0, At)).Rows[1];

        Assert.Equal(MeterSelector.MissingValue, memory.Value);
        Assert.Equal("", memory.ResetText);
    }

    [Fact]
    public void TooltipLine_ShowsCpuAndRamPercent()
    {
        Assert.Equal("PC CPU 23% · RAM 61%", SystemPanel.TooltipLine(Sample()));
        Assert.Equal("PC CPU — · RAM —", SystemPanel.TooltipLine(null));
        Assert.Equal("PC CPU — · RAM 61%", SystemPanel.TooltipLine(Sample(cpu: null)));
    }
}
