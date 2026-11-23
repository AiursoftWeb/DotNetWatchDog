namespace Aiursoft.DotNetWatchDog.Tests;

[TestClass]
public class RedactionHelperTests
{
    [TestMethod]
    public void TestRedactBearerToken()
    {
        var input = "Request Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.test";
        var output = RedactionHelper.Redact(input);
        Assert.AreEqual("Request Authorization: [redacted authorization]", output);
    }

    [TestMethod]
    public void TestRedactBasicToken()
    {
        var input = "Request Authorization: basic dXNlcjpwYXNz";
        var output = RedactionHelper.Redact(input);
        Assert.AreEqual("Request Authorization: [redacted authorization]", output);
    }

    [TestMethod]
    public void TestRedactPasswordAndSecrets()
    {
        var input = "password=supersecret, api_key: \"secret123\", token = abcdef";
        var output = RedactionHelper.Redact(input);
        Assert.IsTrue(output.Contains("password=[redacted]"));
        Assert.IsTrue(output.Contains("api_key: [redacted]"));
        Assert.IsTrue(output.Contains("token = [redacted]"));
        Assert.IsFalse(output.Contains("supersecret"));
        Assert.IsFalse(output.Contains("secret123"));
    }

    [TestMethod]
    public void TestRedactUrlCredentialsAndQueries()
    {
        var input = "Connecting to https://admin:secret123@api.aiursoft.com/path?foo=bar&key=123";
        var output = RedactionHelper.Redact(input);
        Assert.IsFalse(output.Contains("admin:secret123@"));
        Assert.IsTrue(output.Contains("https://[redacted]@api.aiursoft.com/path?[redacted]"));
    }

    [TestMethod]
    public void TestRedactEmail()
    {
        var input = "User email is aimer@aiursoft.com in database.";
        var output = RedactionHelper.Redact(input);
        Assert.AreEqual("User email is [redacted email] in database.", output);
    }

    [TestMethod]
    public void TestReplacesBackticks()
    {
        var input = "Syntax error near `table_name`.";
        var output = RedactionHelper.Redact(input);
        Assert.AreEqual("Syntax error near 'table_name'.", output);
    }
}
