using Conversia.Infrastructure.Persistence;

namespace Conversia.Infrastructure.UnitTests.Persistence;

public class DatabaseTimeTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(9)]
    public void Truncate_CutsTheValueToWholeMicroseconds_KeepingTheKind(long extraTicks)
    {
        var value = new DateTime(2026, 10, 18, 12, 30, 45, DateTimeKind.Utc).AddTicks(1_234_560 + extraTicks);

        var truncated = DatabaseTime.Truncate(value);

        Assert.Equal(0, truncated.Ticks % 10);
        Assert.Equal(DateTimeKind.Utc, truncated.Kind);
        Assert.Equal(value.AddTicks(-extraTicks), truncated);
    }
}
