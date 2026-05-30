// CareerPandaBL/Background/Handlers/JobFetchHelpers.cs
// Shared static helpers for all job fetch handlers — both base-handler subclasses
// and standalone IJobHandler implementations (ATS boards, Remotive, WWR).
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CareerPanda.BL.Background.Handlers;

internal static class JobFetchHelpers
{
    internal static bool ContainsAny(string? text, params string[] keywords) =>
        !string.IsNullOrWhiteSpace(text) &&
        keywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));

    internal static string? NormalizeSalaryPeriod(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var v = raw.Trim().ToUpperInvariant();
        if (v.Contains("YEAR") || v.Contains("ANNUAL") || v == "PA") return "Annual";
        if (v.Contains("MONTH"))                                        return "Monthly";
        if (v.Contains("HOUR") || v == "PH")                           return "Hourly";
        if (v.Contains("WEEK"))                                         return "Weekly";
        return null;
    }

    internal static string? BuildSalaryRangeText(decimal? min, decimal? max, string? normalizedPeriod)
    {
        if (!min.HasValue || !max.HasValue) return null;
        var suffix = normalizedPeriod switch
        {
            "Annual"  => "/yr",
            "Monthly" => "/mo",
            "Hourly"  => "/hr",
            "Weekly"  => "/wk",
            _         => string.Empty
        };
        return $"${min:N0}–${max:N0}{suffix}";
    }

    private static readonly Regex HtmlTagRegex    = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s{2,}", RegexOptions.Compiled);

    internal static string? StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var stripped = HtmlTagRegex.Replace(html, " ");
        stripped = System.Net.WebUtility.HtmlDecode(stripped);
        return WhitespaceRegex.Replace(stripped, " ").Trim();
    }

    internal static string? NormalizeJobLevel(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var v = raw.Trim().ToLowerInvariant();
        if (v.Contains("intern") || v.Contains("co-op") || v.Contains("coop") || v.Contains("trainee")) return "Entry";
        if (v.Contains("entry") || v.Contains("junior") || v.Contains("associate") ||
            v == "1" || v.Contains("level 1") || v == "i") return "Entry";
        if (v.Contains("mid") || v.Contains("intermediate") || v == "2" || v.Contains("level 2") || v == "ii") return "Mid";
        if (v.Contains("staff") || v.Contains("principal") ||
            (v.Contains("lead") && !v.Contains("manager") && !v.Contains("director"))) return "Lead";
        if (v.Contains("senior") || v.Contains("sr.") || v == "sr" || v == "3" || v.Contains("level 3") || v == "iii") return "Senior";
        if (v.Contains("manager") || v.Contains("mgr") || v.Contains("supervisor")) return "Manager";
        if (v.Contains("director") || v.Contains("head of")) return "Director";
        if (v.Contains("vp") || v.Contains("vice president") || v.Contains("c-level") ||
            v.Contains("chief") || v.Contains("president") || v.Contains("executive") || v.Contains("partner")) return "Executive";
        if (v.Contains("full") || v == "fulltime" || v == "full-time" || v == "full time") return null;
        return null;
    }

    // Normalizes a raw state string to 2-letter US abbreviation.
    // Passes through values already in abbreviation form (2 chars, uppercase).
    internal static string? NormalizeState(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        // Already a 2-letter code
        if (trimmed.Length == 2) return trimmed.ToUpperInvariant();
        return StateNameToAbbr.TryGetValue(trimmed, out var abbr) ? abbr : trimmed;
    }

    private static readonly Dictionary<string, string> StateNameToAbbr =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { "Alabama",              "AL" }, { "Alaska",               "AK" },
            { "Arizona",             "AZ" }, { "Arkansas",             "AR" },
            { "California",          "CA" }, { "Colorado",             "CO" },
            { "Connecticut",         "CT" }, { "Delaware",             "DE" },
            { "Florida",             "FL" }, { "Georgia",              "GA" },
            { "Hawaii",              "HI" }, { "Idaho",                "ID" },
            { "Illinois",            "IL" }, { "Indiana",              "IN" },
            { "Iowa",                "IA" }, { "Kansas",               "KS" },
            { "Kentucky",            "KY" }, { "Louisiana",            "LA" },
            { "Maine",               "ME" }, { "Maryland",             "MD" },
            { "Massachusetts",       "MA" }, { "Michigan",             "MI" },
            { "Minnesota",           "MN" }, { "Mississippi",          "MS" },
            { "Missouri",            "MO" }, { "Montana",              "MT" },
            { "Nebraska",            "NE" }, { "Nevada",               "NV" },
            { "New Hampshire",       "NH" }, { "New Jersey",           "NJ" },
            { "New Mexico",          "NM" }, { "New York",             "NY" },
            { "North Carolina",      "NC" }, { "North Dakota",         "ND" },
            { "Ohio",                "OH" }, { "Oklahoma",             "OK" },
            { "Oregon",              "OR" }, { "Pennsylvania",         "PA" },
            { "Rhode Island",        "RI" }, { "South Carolina",       "SC" },
            { "South Dakota",        "SD" }, { "Tennessee",            "TN" },
            { "Texas",               "TX" }, { "Utah",                 "UT" },
            { "Vermont",             "VT" }, { "Virginia",             "VA" },
            { "Washington",          "WA" }, { "West Virginia",        "WV" },
            { "Wisconsin",           "WI" }, { "Wyoming",              "WY" },
            { "District of Columbia","DC" }, { "Washington DC",        "DC" },
            { "Washington D.C.",     "DC" }, { "D.C.",                 "DC" },
            { "Puerto Rico",         "PR" }, { "Guam",                 "GU" },
            { "Virgin Islands",      "VI" }, { "American Samoa",       "AS" },
        };

    // Extracts qualifications from JSearch job_highlights into a plain-text Requirements string.
    // job_highlights is an object: { "Qualifications": [...], "Responsibilities": [...], "Benefits": [...] }
    internal static string? ExtractJobHighlights(JsonElement item)
    {
        if (!item.TryGetProperty("job_highlights", out var h) || h.ValueKind != JsonValueKind.Object)
            return null;

        var lines = new List<string>();

        void AddSection(string key)
        {
            if (!h.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
            foreach (var el in arr.EnumerateArray())
            {
                var s = el.ValueKind == JsonValueKind.String ? el.GetString() : null;
                if (!string.IsNullOrWhiteSpace(s)) lines.Add(s!);
            }
        }

        AddSection("Qualifications");
        AddSection("Responsibilities");

        return lines.Count == 0 ? null : string.Join("\n", lines);
    }
}
