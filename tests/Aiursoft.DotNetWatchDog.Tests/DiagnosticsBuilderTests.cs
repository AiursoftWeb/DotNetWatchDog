namespace Aiursoft.DotNetWatchDog.Tests;

[TestClass]
public class DiagnosticsBuilderTests
{
    [TestMethod]
    public void TestBuildActiveErrorDiagnostics()
    {
        var now = new DateTimeOffset(2026, 11, 23, 19, 15, 0, TimeSpan.Zero);
        var counts = new ErrorCounts(3, 2, 5);
        var samples = new List<LogSample>
        {
            new(now.AddMinutes(-5), 4, "Aiursoft.BadgeProxy", "Connection error with token=secret123", "NullReferenceException at line 42")
        };

        var (context, text) = DiagnosticsBuilder.Build(
            node: "proart",
            database: "AspNetCoreLogs",
            table: "BadgeProxyLogs",
            counts: counts,
            nowUtc: now,
            publicUrl: "https://clickhouse-proart.aiursoft.com",
            samples: samples);

        Assert.AreEqual(5, (int)context["TotalCount"]!);
        Assert.AreEqual(3, (int)context["ErrorCount"]!);
        Assert.AreEqual(2, (int)context["CriticalCount"]!);
        Assert.AreEqual("sev3", (string)context["ThresholdLevel"]!);
        Assert.AreEqual(5, (int)context["TriggerThreshold"]!);

        Assert.IsTrue(text.Contains("proart / AspNetCoreLogs.BadgeProxyLogs"));
        Assert.IsTrue(text.Contains("5 errors in the last 15 minutes"));
        Assert.IsTrue(text.Contains("Error detection window"));
        Assert.IsTrue(text.Contains("ClickHouse query page: https://clickhouse-proart.aiursoft.com/play"));
        Assert.IsTrue(text.Contains("Recent examples"));
        Assert.IsTrue(text.Contains("token=[redacted]"));
        Assert.IsFalse(text.Contains("secret123"));
    }

    [TestMethod]
    public void TestBuildRecoveryDiagnostics()
    {
        var now = new DateTimeOffset(2026, 11, 23, 19, 30, 0, TimeSpan.Zero);
        var triggerEnd = now.AddMinutes(-15);
        var counts = new ErrorCounts(0, 0, 0);

        var (context, text) = DiagnosticsBuilder.Build(
            node: "proart",
            database: "AspNetCoreLogs",
            table: "BadgeProxyLogs",
            counts: counts,
            nowUtc: now,
            publicUrl: "https://clickhouse-proart.aiursoft.com",
            samples: null,
            triggerEndUtc: triggerEnd);

        Assert.AreEqual(0, (int)context["TotalCount"]!);
        Assert.IsNull(context.Value<string>("ThresholdLevel"));

        Assert.IsTrue(text.Contains("Recovery verification window"));
        Assert.IsTrue(text.Contains("Recovery confirmed. Zero errors are expected in the recovery window"));
        Assert.IsTrue(text.Contains("Original incident trigger window"));
        Assert.IsTrue(text.Contains("Verify recovery (expected total: 0; first_error and last_error: NULL)"));
    }
}
