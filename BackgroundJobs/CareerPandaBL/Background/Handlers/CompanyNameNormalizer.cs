// CareerPandaBL/Background/Handlers/CompanyNameNormalizer.cs
// Shared company-name normalization + H1B sponsor-set builder used by ATS handlers.
using System.Text.RegularExpressions;

namespace CareerPanda.BL.Background.Handlers;

internal static partial class CompanyNameNormalizer
{
    private static readonly string[] LegalSuffixes =
        ["INCORPORATED", "CORPORATION", "LIMITED", "INC", "LLC", "CORP", "LTD", "CO", "LP", "LLP", "PLLC", "PC"];

    [GeneratedRegex(@"[^\w\s]")]
    private static partial Regex NonWordChars();

    public static string Normalize(string name)
    {
        var stripped = NonWordChars().Replace(name.ToUpperInvariant(), " ");
        var parts    = stripped.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int count    = parts.Length;
        while (count > 1 && LegalSuffixes.Contains(parts[count - 1])) count--;
        return string.Join(' ', parts, 0, count);
    }

    public static HashSet<string> BuildSponsorSet(List<string> names)
    {
        var set = new HashSet<string>(names.Count * 2, StringComparer.OrdinalIgnoreCase);
        foreach (var n in names) { set.Add(n); set.Add(Normalize(n)); }
        return set;
    }

    public static bool IsH1BSponsored(string companyName, HashSet<string> sponsors) =>
        sponsors.Contains(companyName) || sponsors.Contains(Normalize(companyName));
}
