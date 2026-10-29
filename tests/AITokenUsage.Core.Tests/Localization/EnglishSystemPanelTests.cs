using AITokenUsage.Core.Presentation;
using AITokenUsage.Core.SystemUsage;

namespace AITokenUsage.Core.Tests.Localization;

[UseCulture("en-US")]
public class EnglishSystemPanelTests
{
    private const long Gib = 1024L * 1024 * 1024;
    private static readonly SystemUsageSample Sample = new(23, (long)(19.5 * Gib), 32 * Gib, DateTimeOffset.UnixEpoch);

    [Fact]
    public void MemoryText_SameInEnglish() => Assert.Equal("19.5 / 32 GB", SystemPanel.MemoryText(Sample));

    [Fact]
    public void CardLine_English() => Assert.Equal("CPU 23% · Memory 19.5 / 32 GB", SystemPanel.CardLine(Sample));

    [Fact]
    public void TooltipLine_English() => Assert.Equal("PC CPU 23% · RAM 61%", SystemPanel.TooltipLine(Sample));

    [Fact]
    public void BuildCard_English()
    {
        DetailCardModel card = SystemPanel.BuildCard(Sample);

        Assert.Equal("Memory", card.Rows[1].Label);
        Assert.Equal(["Updates every 2 s"], card.StatusLines);
    }
}
