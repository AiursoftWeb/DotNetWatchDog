using System.Globalization;

namespace Aiursoft.DotNetWatchDog;

internal static class SeverityHelper
{
    internal static string? GetSeverity(int count) => count switch
    {
        >= 15 => "sev2",
        >= 5 => "sev3",
        >= 3 => "sev4",
        _ => null
    };

    internal static int? GetThreshold(string? level) => level switch
    {
        "sev2" => 15,
        "sev3" => 5,
        "sev4" => 3,
        _ => null
    };

    internal static bool IsHigherSeverity(string levelA, string levelB)
    {
        if (levelA.StartsWith("sev", StringComparison.OrdinalIgnoreCase) &&
            levelB.StartsWith("sev", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(levelA.AsSpan(3), CultureInfo.InvariantCulture, out var rankA) &&
            int.TryParse(levelB.AsSpan(3), CultureInfo.InvariantCulture, out var rankB))
        {
            return rankA < rankB; // sev2 < sev3 means sev2 is higher priority
        }
        return false;
    }
}
