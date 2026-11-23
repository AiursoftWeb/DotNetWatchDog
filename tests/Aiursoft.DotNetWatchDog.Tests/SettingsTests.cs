namespace Aiursoft.DotNetWatchDog.Tests;

[TestClass]
public class SettingsTests
{
    [TestMethod]
    public void TestSettingsLoadFromEnvironment()
    {
        Environment.SetEnvironmentVariable("NODE_NAME", "proart-test");
        Environment.SetEnvironmentVariable("CLICKHOUSE_DATABASE", "CustomLogs");
        Environment.SetEnvironmentVariable("ALERTCENTER_URL", "https://alertcenter.aiursoft.com");
        Environment.SetEnvironmentVariable("ALERTCENTER_API_KEY", "dummy-key-123");
        Environment.SetEnvironmentVariable("CLICKHOUSE_URL", "http://ch-server:8123");
        Environment.SetEnvironmentVariable("CLICKHOUSE_PUBLIC_URL", "https://clickhouse.aiursoft.com");
        Environment.SetEnvironmentVariable("STATE_DIR", "/tmp/watchdog-test-state");

        try
        {
            var settings = Settings.Load();
            Assert.AreEqual("proart-test", settings.NodeName);
            Assert.AreEqual("CustomLogs", settings.Database);
            Assert.AreEqual("https://alertcenter.aiursoft.com", settings.AlertCenterUrl);
            Assert.AreEqual("dummy-key-123", settings.AlertCenterKey);
            Assert.AreEqual("/tmp/watchdog-test-state", settings.DataDirectory);
            Assert.AreEqual("https://clickhouse.aiursoft.com", settings.PublicUrl);
            Assert.AreEqual("clickhouse-app-log-error-monitor", settings.Source);
            Assert.IsTrue(settings.ConnectionString.Contains("Host=ch-server;Port=8123"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NODE_NAME", null);
            Environment.SetEnvironmentVariable("CLICKHOUSE_DATABASE", null);
            Environment.SetEnvironmentVariable("ALERTCENTER_URL", null);
            Environment.SetEnvironmentVariable("ALERTCENTER_API_KEY", null);
            Environment.SetEnvironmentVariable("CLICKHOUSE_URL", null);
            Environment.SetEnvironmentVariable("CLICKHOUSE_PUBLIC_URL", null);
            Environment.SetEnvironmentVariable("STATE_DIR", null);
        }
    }

    [TestMethod]
    public void TestSettingsThrowsWhenRequiredMissing()
    {
        Environment.SetEnvironmentVariable("NODE_NAME", null);
        Environment.SetEnvironmentVariable("SERVER_NAME", null);
        Assert.ThrowsExactly<InvalidOperationException>(() => Settings.Load());
    }

    [TestMethod]
    public void TestSettingsThrowsWhenInvalidUrl()
    {
        Environment.SetEnvironmentVariable("NODE_NAME", "test-node");
        Environment.SetEnvironmentVariable("ALERTCENTER_URL", "not-a-valid-url");
        Environment.SetEnvironmentVariable("ALERTCENTER_API_KEY", "dummy-key");
        try
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => Settings.Load());
        }
        finally
        {
            Environment.SetEnvironmentVariable("NODE_NAME", null);
            Environment.SetEnvironmentVariable("ALERTCENTER_URL", null);
            Environment.SetEnvironmentVariable("ALERTCENTER_API_KEY", null);
        }
    }
}
