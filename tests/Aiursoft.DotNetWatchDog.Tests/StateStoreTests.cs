namespace Aiursoft.DotNetWatchDog.Tests;

[TestClass]
public class StateStoreTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "dotnet_watchdog_test_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private Settings CreateSettings(string node = "test-node", string db = "AspNetCoreLogs") => new(
        NodeName: node,
        Database: db,
        ConnectionString: "Host=localhost",
        AlertCenterUrl: "https://alertcenter.aiursoft.com",
        AlertCenterKey: "test-key",
        DataDirectory: _tempDir,
        PublicUrl: null,
        Source: "test-source");

    [TestMethod]
    public void TestInitializeAndScopeCheck()
    {
        using (new StateStore(CreateSettings()))
        {
            Assert.IsTrue(File.Exists(Path.Combine(_tempDir, "state.sqlite3")));
            Assert.IsTrue(File.Exists(Path.Combine(_tempDir, "monitor.lock")));
        }

        // Reopening with same settings should succeed
        using (var store2 = new StateStore(CreateSettings()))
        {
            Assert.IsNotNull(store2);
        }

        // Reopening with different scope should throw
        Assert.ThrowsExactly<InvalidOperationException>(() =>
        {
            using var _ = new StateStore(CreateSettings(node: "other-node"));
        });
    }

    [TestMethod]
    public void TestRememberAndKnownTables()
    {
        using var store = new StateStore(CreateSettings());
        Assert.AreEqual(0, store.KnownNames().Count);

        store.Remember(["TableA", "TableB"]);
        var known = store.KnownNames();
        Assert.AreEqual(2, known.Count);
        Assert.IsTrue(known.Contains("TableA"));
        Assert.IsTrue(known.Contains("TableB"));

        store.Remember(["TableB", "TableC"]);
        var updated = store.KnownNames();
        Assert.AreEqual(3, updated.Count);
        Assert.IsTrue(updated.Contains("TableC"));
    }

    [TestMethod]
    public void TestPendingManagement()
    {
        using var store = new StateStore(CreateSettings());
        Assert.IsNull(store.Pending("TableA"));
        Assert.AreEqual(0, store.PendingNames().Count);

        store.Save("TableA", "{\"ReportId\":\"123\"}");
        Assert.AreEqual("{\"ReportId\":\"123\"}", store.Pending("TableA"));
        Assert.AreEqual(1, store.PendingNames().Count);
        Assert.AreEqual("TableA", store.PendingNames()[0]);

        store.Delete("TableA");
        Assert.IsNull(store.Pending("TableA"));
        Assert.AreEqual(0, store.PendingNames().Count);
    }
}
