using Sync.Core.Time;

namespace Sync.Core.Tests;

public class TimeTests
{
    [Fact]
    public void LogicalClock_TicksMonotonically()
    {
        var clock = new LogicalClock(5);

        Assert.Equal(6, clock.Tick());
        Assert.Equal(7, clock.Tick());
        Assert.Equal(7, clock.Now);
    }

    [Fact]
    public void LogicalClock_AdvanceToNeverGoesBack()
    {
        var clock = new LogicalClock(10);

        clock.AdvanceTo(3);
        Assert.Equal(10, clock.Now);

        clock.AdvanceTo(20);
        Assert.Equal(20, clock.Now);
    }

    [Fact]
    public void DeterministicGuids_SameSeedGivesSameSequence()
    {
        var first = DeterministicGuids.FromSeed(42);
        var second = DeterministicGuids.FromSeed(42);

        var a = Enumerable.Range(0, 5).Select(_ => first()).ToList();
        var b = Enumerable.Range(0, 5).Select(_ => second()).ToList();

        Assert.Equal(a, b);
        Assert.Equal(5, a.Distinct().Count());
        Assert.All(a, g => Assert.Equal('4', g.ToString("D")[14]));
    }

    [Fact]
    public void DeterministicGuids_DifferentSeedsDiffer()
    {
        Assert.NotEqual(DeterministicGuids.FromSeed(1)(), DeterministicGuids.FromSeed(2)());
    }
}
