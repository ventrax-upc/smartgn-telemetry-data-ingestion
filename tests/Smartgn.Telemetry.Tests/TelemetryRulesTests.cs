using Smartgn.Telemetry.Domain;

namespace Smartgn.Telemetry.Tests;

public class TelemetryRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EvaluateFreshness_WithinThreshold_IsCurrent()
    {
        var freshness = TelemetryRules.EvaluateFreshness(Now - TimeSpan.FromSeconds(29), Now);

        Assert.Equal(TelemetryFreshness.Current, freshness);
    }

    [Fact]
    public void EvaluateFreshness_BeyondThreshold_IsDelayed()
    {
        var freshness = TelemetryRules.EvaluateFreshness(Now - TimeSpan.FromSeconds(31), Now);

        Assert.Equal(TelemetryFreshness.Delayed, freshness);
    }

    [Theory]
    [InlineData(5, false)]
    [InlineData(6, true)]
    [InlineData(60, true)]
    public void IsFromTheFuture_RespectsTheFiveSecondTolerance(int secondsAhead, bool expected)
    {
        var measuredAt = Now + TimeSpan.FromSeconds(secondsAhead);

        Assert.Equal(expected, TelemetryRules.IsFromTheFuture(measuredAt, Now));
    }

    [Theory]
    [InlineData("meter-001", true)]
    [InlineData("meter_001", true)]
    [InlineData("meter.001", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("with space", false)]
    [InlineData("quote\"inside", false)]
    [InlineData("way-too-long-device-id-way-too-long-device-id-way-too-long-device", false)]
    public void IsValidIdentifier_ChecksPatternAndLength(string? value, bool expected)
    {
        Assert.Equal(expected, TelemetryRules.IsValidIdentifier(value));
    }

    [Fact]
    public void IsValidIdentifier_HonoursCustomMaxLength()
    {
        Assert.True(TelemetryRules.IsValidIdentifier("abc", maxLength: 3));
        Assert.False(TelemetryRules.IsValidIdentifier("abcd", maxLength: 3));
    }

    [Fact]
    public void Thresholds_MatchTheReportedQualityAttributeScenarios()
    {
        Assert.Equal(30, TelemetryRules.StalenessThreshold.TotalSeconds);
        Assert.Equal(360, TelemetryRules.EventBufferCapacity);
        Assert.Equal(60, TelemetryRules.EventBufferMaxAge.TotalMinutes);
        Assert.Equal(30, TelemetryRules.MaxResendInterval.TotalSeconds);
        Assert.Equal(2, TelemetryRules.RequestTimeout.TotalSeconds);
        Assert.Equal(5, TelemetryRules.CircuitFailureThreshold);
        Assert.Equal(30, TelemetryRules.CircuitProbeInterval.TotalSeconds);
        Assert.Equal(30, TelemetryRules.RetentionDetail.TotalDays);
    }
}
