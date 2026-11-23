namespace Aiursoft.DotNetWatchDog.Tests;

[TestClass]
public class InvestigationQueriesTests
{
    [TestMethod]
    public void TestEscapeIdent()
    {
        Assert.AreEqual("`normal`", InvestigationQueries.EscapeIdent("normal"));
        Assert.AreEqual("`table\\`name`", InvestigationQueries.EscapeIdent("table`name"));
        Assert.AreEqual("`path\\\\test`", InvestigationQueries.EscapeIdent("path\\test"));
    }

    [TestMethod]
    public void TestBuildQueries()
    {
        var end = new DateTimeOffset(2026, 11, 23, 19, 0, 0, TimeSpan.Zero);
        var result = InvestigationQueries.BuildQueries("AspNetCoreLogs", "BadgeProxyLogs", end);

        Assert.IsTrue(result.CountSql.Contains("`AspNetCoreLogs`.`BadgeProxyLogs`"));
        Assert.IsTrue(result.CountSql.Contains("countIf(toInt32(LogLevel) = 4) AS errors"));
        Assert.IsTrue(result.CountSql.Contains("countIf(toInt32(LogLevel) = 5) AS critical"));
        Assert.IsTrue(result.WhereClause.Contains("2026-11-23 18:45:00"));
        Assert.IsTrue(result.WhereClause.Contains("2026-11-23 19:00:00"));
        Assert.IsTrue(result.DetailsSql.Contains("ORDER BY EventTime DESC"));
        Assert.IsTrue(result.DetailsSql.Contains("LIMIT 20"));
    }

    [TestMethod]
    public void TestBuildInvestigateUrl()
    {
        Assert.IsNull(InvestigationQueries.BuildInvestigateUrl(null));
        Assert.IsNull(InvestigationQueries.BuildInvestigateUrl(""));

        var urlWithoutQuery = InvestigationQueries.BuildInvestigateUrl("https://clickhouse.aiursoft.com");
        Assert.AreEqual("https://clickhouse.aiursoft.com/play", urlWithoutQuery);

        var urlWithQuery = InvestigationQueries.BuildInvestigateUrl("https://clickhouse.aiursoft.com", "SELECT 1;");
        Assert.AreEqual("https://clickhouse.aiursoft.com/play?query=SELECT%201%3B", urlWithQuery);
    }
}
