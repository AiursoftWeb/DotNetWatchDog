using System.Text.RegularExpressions;

namespace Aiursoft.DotNetWatchDog;

internal static partial class RedactionHelper
{
    [GeneratedRegex(@"(?i)\b(?:bearer|basic)\s+[A-Za-z0-9._~+/=-]+")]
    private static partial Regex BearerAuthRegex();

    [GeneratedRegex(@"(?i)([\w.-]*(?:password|passwd|secret|token|api[_-]?key|authorization|cookie)[\w.-]*[""\s]*[:=]\s*)(?!\[redacted)(""""[^""""]*""""|'[^']*'|[^\s;,]+)")]
    private static partial Regex SecretParamRegex();

    [GeneratedRegex(@"(https?://)[^\s/@]+:[^\s/@]+@")]
    private static partial Regex UrlUserPassRegex();

    [GeneratedRegex(@"(https?://[^\s?#]+)[?#][^\s]*")]
    private static partial Regex UrlQueryRegex();

    [GeneratedRegex(@"\b[A-Za-z0-9.!#$%&'*+/=?^_`{|}~-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b")]
    private static partial Regex EmailRegex();

    internal static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var text = value;
        text = BearerAuthRegex().Replace(text, "[redacted authorization]");
        text = SecretParamRegex().Replace(text, "$1[redacted]");
        text = UrlUserPassRegex().Replace(text, "$1[redacted]@");
        text = UrlQueryRegex().Replace(text, "$1?[redacted]");
        text = EmailRegex().Replace(text, "[redacted email]");
        return text.Replace('`', '\'');
    }
}
