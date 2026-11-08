using NightLoop.Contracts.MacroDocument;
using NightLoop.Engine.Runtime;
using Xunit;

namespace NightLoop.Engine.Tests;

public class BlockWaitsTapTests
{
    [Theory]
    [InlineData(BlockTypes.Tap)]
    [InlineData(BlockTypes.LongPress)]
    [InlineData(BlockTypes.Swipe)]
    public void Phone_touches_use_wait_after_clicks(string type)
    {
        var waits = new BlockWaits { AfterClicksMs = 300, AfterBlocksMs = 100 };
        Assert.Equal(400, waits.After(new Block { Type = type }, found: true));
    }
}
