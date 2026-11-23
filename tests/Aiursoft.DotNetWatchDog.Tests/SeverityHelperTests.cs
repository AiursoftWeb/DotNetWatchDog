namespace Aiursoft.DotNetWatchDog.Tests;

[TestClass]
public class SeverityHelperTests
{
    [TestMethod]
    [DataRow(0, null)]
    [DataRow(1, null)]
    [DataRow(2, null)]
    [DataRow(3, "sev4")]
    [DataRow(4, "sev4")]
    [DataRow(5, "sev3")]
    [DataRow(14, "sev3")]
    [DataRow(15, "sev2")]
    [DataRow(100, "sev2")]
    public void TestSeverityLevels(int count, string? expected)
    {
        var actual = SeverityHelper.GetSeverity(count);
        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    public void TestSeverityOrdering()
    {
        Assert.IsTrue(SeverityHelper.IsHigherSeverity("sev2", "sev3"));
        Assert.IsTrue(SeverityHelper.IsHigherSeverity("sev3", "sev4"));
        Assert.IsTrue(SeverityHelper.IsHigherSeverity("sev2", "sev4"));
        Assert.IsFalse(SeverityHelper.IsHigherSeverity("sev4", "sev3"));
        Assert.IsFalse(SeverityHelper.IsHigherSeverity("sev3", "sev2"));
        Assert.IsFalse(SeverityHelper.IsHigherSeverity("sev3", "sev3"));
    }
}
