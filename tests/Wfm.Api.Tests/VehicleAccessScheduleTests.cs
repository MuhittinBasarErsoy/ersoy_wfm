using Wfm.Application;
using Wfm.Application.Contracts;

namespace Wfm.Api.Tests;

public class VehicleAccessScheduleTests
{
    [Theory]
    [InlineData("2026-11-23T04:59:59Z", false)] // Monday 07:59 Türkiye
    [InlineData("2026-11-23T05:00:00Z", true)]
    [InlineData("2026-11-23T14:59:59Z", true)]
    [InlineData("2026-11-23T15:00:00Z", false)] // End is exclusive
    [InlineData("2026-11-24T09:00:00Z", false)]
    public void Uses_Turkey_clock_and_exact_boundaries(string utc, bool allowed)
    {
        var json = VehicleAccessScheduleRules.Write([new(DayOfWeek.Monday, 480, 1080)]);
        Assert.Equal(allowed, VehicleAccessScheduleRules.Allows(json, DateTimeOffset.Parse(utc)));
    }

    [Theory]
    [InlineData("2026-11-29T18:59:59Z", false)] // Sunday 21:59
    [InlineData("2026-11-29T19:00:00Z", true)] // Sunday 22:00
    [InlineData("2026-11-29T22:30:00Z", true)] // Monday 01:30
    [InlineData("2026-11-29T23:00:00Z", false)] // Monday 02:00
    public void Overnight_window_crosses_week_boundary(string utc, bool allowed)
    {
        var json = VehicleAccessScheduleRules.Write([new(DayOfWeek.Sunday, 1320, 120)]);
        Assert.Equal(allowed, VehicleAccessScheduleRules.Allows(json, DateTimeOffset.Parse(utc)));
    }

    [Fact]
    public void Null_is_unrestricted_but_malformed_restricted_data_fails_closed()
    {
        Assert.True(VehicleAccessScheduleRules.Allows(null, DateTimeOffset.UtcNow));
        foreach (var json in new[] { "[]", "null", "bad json", "[null]", "[{\"Day\":99,\"StartMinute\":0,\"EndMinute\":60}]" })
            Assert.False(VehicleAccessScheduleRules.Allows(json, DateTimeOffset.UtcNow));
        Assert.False(VehicleAccessScheduleRules.IsValid([new(DayOfWeek.Monday, 0, 0)]));
        Assert.False(VehicleAccessScheduleRules.IsValid([new(DayOfWeek.Monday, -1, 60)]));
        Assert.False(VehicleAccessScheduleRules.IsValid([new(DayOfWeek.Monday, 0, 60), new(DayOfWeek.Monday, 120, 180)]));
    }
}
