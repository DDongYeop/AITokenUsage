using AITokenUsage.Core.Taskbar;
using Xunit;

namespace AITokenUsage.Core.Tests.Taskbar;

public class PixelRectTests
{
    [Fact]
    public void SizeEmptyOffsetInside()
    {
        var r = new PixelRect(-1920, 1032, 0, 1080);

        Assert.Equal(1920, r.Width);
        Assert.Equal(48, r.Height);
        Assert.False(r.IsEmpty);
        Assert.True(new PixelRect(5, 5, 5, 10).IsEmpty);
        Assert.Equal(new PixelRect(0, 0, 1920, 48), r.Offset(1920, -1032));
        Assert.True(new PixelRect(8, 4, 184, 44).IsInside(new PixelRect(0, 0, 1920, 48)));
        Assert.False(new PixelRect(-1, 4, 184, 44).IsInside(new PixelRect(0, 0, 1920, 48)));
    }

    [Fact]
    public void Contains_LeftTopInclusive_RightBottomExclusive()
    {
        var r = new PixelRect(-1920, 1032, 0, 1080);

        Assert.True(r.Contains(new PixelPoint(-1920, 1032)));
        Assert.True(r.Contains(new PixelPoint(-1, 1079)));
        Assert.False(r.Contains(new PixelPoint(0, 1050)));
        Assert.False(r.Contains(new PixelPoint(-100, 1080)));
        Assert.False(r.Contains(new PixelPoint(-1921, 1050)));
        Assert.False(r.Contains(new PixelPoint(-100, 1031)));
    }
}
