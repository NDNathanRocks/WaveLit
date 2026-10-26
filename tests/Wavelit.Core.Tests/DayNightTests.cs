using Wavelit.Core.Desktop;
using Wavelit.Core.Interop;
using Wavelit.Core.Playback;

namespace Wavelit.Core.Tests;

public class SunTimesTests
{
    // Reference values from NOAA's solar calculator (UTC), within a few minutes.
    [Theory]
    [InlineData(2026, 6, 21, 51.51, -0.13, "03:43", "20:21")]   // London, midsummer
    [InlineData(2026, 12, 21, 40.71, -74.01, "12:16", "21:32")] // New York, midwinter
    [InlineData(2026, 3, 20, -33.87, 151.21, "20:02", "08:10")] // Sydney (sunset on the next UTC day)
    public void Matches_reference_sunrise_and_sunset(int y, int m, int d, double lat, double lon, string rise, string set)
    {
        var t = SunTimes.Calculate(new DateOnly(y, m, d), lat, lon)!.Value;
        Assert.InRange(Math.Abs((t.Sunrise.TimeOfDay - TimeSpan.Parse(rise)).TotalMinutes), 0, 6);
        Assert.InRange(Math.Abs((t.Sunset.TimeOfDay - TimeSpan.Parse(set)).TotalMinutes), 0, 6);
        Assert.True(t.Sunset > t.Sunrise);
    }

    [Fact]
    public void Day_and_night_in_new_york()
    {
        Assert.True(SunTimes.IsDaytime(new DateTime(2026, 10, 25, 17, 0, 0, DateTimeKind.Utc), 40.71, -74.01));  // 1 pm local
        Assert.False(SunTimes.IsDaytime(new DateTime(2026, 10, 26, 3, 0, 0, DateTimeKind.Utc), 40.71, -74.01));  // 11 pm local
    }

    [Fact]
    public void Polar_summer_is_day() =>
        Assert.True(SunTimes.IsDaytime(new DateTime(2026, 6, 21, 0, 0, 0, DateTimeKind.Utc), 78.22, 15.65)); // Svalbard

    [Fact]
    public void City_list_is_valid() => Assert.All(SunTimes.Cities, c =>
    {
        Assert.InRange(c.Latitude, -90, 90);
        Assert.InRange(c.Longitude, -180, 180);
    });
}

public class SetupMemoryTests
{
    private static readonly MonitorInfo A = new("A", "a", 0, new RECT(0, 0, 1920, 1080), new RECT(0, 0, 1920, 1040), true);
    private static readonly MonitorInfo B = new("B", "b", 1, new RECT(1920, 0, 3840, 1080), new RECT(1920, 0, 3840, 1040), false);

    [Fact]
    public void Signature_ignores_order_but_not_arrangement()
    {
        Assert.Equal(SetupMemory.Signature([A, B]), SetupMemory.Signature([B, A]));
        Assert.NotEqual(SetupMemory.Signature([A, B]), SetupMemory.Signature([A]));
        var moved = B with { Bounds = new RECT(-1920, 0, 0, 1080) };
        Assert.NotEqual(SetupMemory.Signature([A, B]), SetupMemory.Signature([A, moved]));
    }
}
