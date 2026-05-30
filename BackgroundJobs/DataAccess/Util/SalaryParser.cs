// DataAccess/Util/SalaryParser.cs
// Parses free-text salary strings into structured min/max/period values.
// Used by any handler whose API returns salary as a free-text field (e.g. Remotive).
using System.Text.RegularExpressions;

namespace CareerPanda.DataAccess.Util;

public static class SalaryParser
{
    // Matches patterns like: $80k-$120k, $80,000 - $120,000, 80 to 120k, 80000–120000
    private static readonly Regex RangeRegex = new(
        @"\$?\s*([\d,]+)\s*([kK])?\s*[-–—to]+\s*\$?\s*([\d,]+)\s*([kK])?",
        RegexOptions.Compiled);

    /// <summary>
    /// Returns (min, max, period) where period is "Annual" | "Monthly" | "Hourly" | "Weekly" | null.
    /// All three can be null if the string cannot be parsed.
    /// </summary>
    public static (decimal? min, decimal? max, string? period) Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (null, null, null);

        // Detect period from keyword hints first
        var lower = raw.ToLowerInvariant();
        string? period =
            lower.Contains("year") || lower.Contains("annual") || lower.Contains("/yr") ? "Annual"  :
            lower.Contains("month") || lower.Contains("/mo")                             ? "Monthly" :
            lower.Contains("hour")  || lower.Contains("/hr")                             ? "Hourly"  :
            lower.Contains("week")  || lower.Contains("/wk")                             ? "Weekly"  : null;

        var m = RangeRegex.Match(raw);
        if (!m.Success) return (null, null, period);

        if (!decimal.TryParse(m.Groups[1].Value.Replace(",", ""), out var minV)) return (null, null, period);
        if (!decimal.TryParse(m.Groups[3].Value.Replace(",", ""), out var maxV)) return (null, null, period);

        if (m.Groups[2].Value.Equals("k", StringComparison.OrdinalIgnoreCase)) minV *= 1000;
        if (m.Groups[4].Value.Equals("k", StringComparison.OrdinalIgnoreCase)) maxV *= 1000;

        // Fallback heuristic: large numbers → Annual, small numbers → Hourly
        if (period == null) period = minV >= 10_000 ? "Annual" : minV >= 100 ? "Monthly" : "Hourly";

        return (minV, maxV, period);
    }
}
