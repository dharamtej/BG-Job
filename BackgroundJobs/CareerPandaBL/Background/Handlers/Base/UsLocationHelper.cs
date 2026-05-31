// CareerPandaBL/Background/Handlers/UsLocationHelper.cs
// Shared US-location reference data + normalisation used by all 16 job handlers.
using System.Text.RegularExpressions;
namespace CareerPanda.BL.Background.Handlers;

internal static partial class UsLocationHelper
{
    // ── Reference sets ────────────────────────────────────────────────────────────

    public static readonly HashSet<string> StateAbbrs = new(StringComparer.OrdinalIgnoreCase)
    {
        "AL","AK","AZ","AR","CA","CO","CT","DE","FL","GA","HI","ID","IL","IN","IA",
        "KS","KY","LA","ME","MD","MA","MI","MN","MS","MO","MT","NE","NV","NH","NJ",
        "NM","NY","NC","ND","OH","OK","OR","PA","RI","SC","SD","TN","TX","UT","VT",
        "VA","WA","WV","WI","WY","DC","PR","GU","VI","AS","MP"
    };

    public static readonly HashSet<string> StateNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Alabama","Alaska","Arizona","Arkansas","California","Colorado","Connecticut",
        "Delaware","Florida","Georgia","Hawaii","Idaho","Illinois","Indiana","Iowa",
        "Kansas","Kentucky","Louisiana","Maine","Maryland","Massachusetts","Michigan",
        "Minnesota","Mississippi","Missouri","Montana","Nebraska","Nevada",
        "New Hampshire","New Jersey","New Mexico","New York","North Carolina",
        "North Dakota","Ohio","Oklahoma","Oregon","Pennsylvania","Rhode Island",
        "South Carolina","South Dakota","Tennessee","Texas","Utah","Vermont",
        "Virginia","Washington","West Virginia","Wisconsin","Wyoming",
        "District of Columbia","Washington DC","Washington D.C.","D.C.",
        "Puerto Rico","Guam","Virgin Islands","American Samoa"
    };

    public static readonly HashSet<string> CountryVariants = new(StringComparer.OrdinalIgnoreCase)
    {
        "US", "USA", "U.S.", "U.S.A.", "United States", "United States of America"
    };

    // State name → 2-letter abbreviation (for normalising state field to canonical code)
    private static readonly Dictionary<string, string> StateNameToAbbr =
        new(StringComparer.OrdinalIgnoreCase)
        {
            {"Alabama","AL"},{"Alaska","AK"},{"Arizona","AZ"},{"Arkansas","AR"},
            {"California","CA"},{"Colorado","CO"},{"Connecticut","CT"},{"Delaware","DE"},
            {"Florida","FL"},{"Georgia","GA"},{"Hawaii","HI"},{"Idaho","ID"},
            {"Illinois","IL"},{"Indiana","IN"},{"Iowa","IA"},{"Kansas","KS"},
            {"Kentucky","KY"},{"Louisiana","LA"},{"Maine","ME"},{"Maryland","MD"},
            {"Massachusetts","MA"},{"Michigan","MI"},{"Minnesota","MN"},{"Mississippi","MS"},
            {"Missouri","MO"},{"Montana","MT"},{"Nebraska","NE"},{"Nevada","NV"},
            {"New Hampshire","NH"},{"New Jersey","NJ"},{"New Mexico","NM"},{"New York","NY"},
            {"North Carolina","NC"},{"North Dakota","ND"},{"Ohio","OH"},{"Oklahoma","OK"},
            {"Oregon","OR"},{"Pennsylvania","PA"},{"Rhode Island","RI"},
            {"South Carolina","SC"},{"South Dakota","SD"},{"Tennessee","TN"},{"Texas","TX"},
            {"Utah","UT"},{"Vermont","VT"},{"Virginia","VA"},{"Washington","WA"},
            {"West Virginia","WV"},{"Wisconsin","WI"},{"Wyoming","WY"},
            {"District of Columbia","DC"},{"Washington DC","DC"},{"Washington D.C.","DC"},{"D.C.","DC"},
            {"Puerto Rico","PR"},{"Guam","GU"},{"Virgin Islands","VI"},{"American Samoa","AS"},
        };

    // ── IsUs ─────────────────────────────────────────────────────────────────────
    public static bool IsUs(string? country, string? state)
    {
        if (!string.IsNullOrEmpty(country) && CountryVariants.Contains(country)) return true;
        if (!string.IsNullOrEmpty(country) && (StateAbbrs.Contains(country) || StateNames.Contains(country))) return true;
        if (!string.IsNullOrEmpty(state)   && (StateAbbrs.Contains(state)   || StateNames.Contains(state)))   return true;
        return false;
    }

    // ── NormalizeStateCode ────────────────────────────────────────────────────────
    // Converts any state name or abbr to the canonical 2-letter code ("California" → "CA").
    public static string? NormalizeStateCode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = raw.Trim();
        if (t.Length == 2 && StateAbbrs.Contains(t)) return t.ToUpperInvariant();
        return StateNameToAbbr.TryGetValue(t, out var abbr) ? abbr : t;
    }

    // ── NormalizeToUs ────────────────────────────────────────────────────────────
    // Normalizes (country, state) in-place; returns true when the job is US.
    // Always sets country = "US" on success. Normalizes state to 2-letter code.
    public static bool NormalizeToUs(ref string? country, ref string? state)
    {
        if (!string.IsNullOrEmpty(country))
        {
            var trimmed = country.Trim();
            var upper   = trimmed.ToUpperInvariant();

            // Exact US country variant
            if (CountryVariants.Contains(trimmed))
                return SetUs(ref country, ref state, null);

            // Exact US state name or abbreviation in country field → move to state
            if (StateNames.Contains(trimmed) || StateAbbrs.Contains(trimmed))
                return SetUs(ref country, ref state, trimmed);

            // US zip code alone ("10950", "94130") → clearly US, no state info
            if (UsZipRegex().IsMatch(trimmed))
                return SetUs(ref country, ref state, null);

            // Compound US signals in country field:
            //   "United States; San Francisco", "Georgia - USA", "US; Remote",
            //   "Remote - US", "CA USA", "US HQ", "NY • United States", etc.
            if (upper.Contains("UNITED STATES") ||
                upper.Contains("U.S.A")         ||
                upper.StartsWith("US;")         ||
                upper.StartsWith("US ")         ||
                upper.StartsWith("US/")         ||
                upper.StartsWith("USA;")        ||
                upper.StartsWith("USA ")        ||
                upper.StartsWith("USA-")        ||
                upper.EndsWith(" US")           ||
                upper.EndsWith("-US")           ||
                upper.EndsWith(";US")           ||
                upper.EndsWith("(US)")          ||
                upper.EndsWith("- US")          ||
                UsaWordRegex().IsMatch(upper))
            {
                // Try to recover the state from the compound string
                var extracted = ExtractStateFromCompound(trimmed);
                return SetUs(ref country, ref state, extracted);
            }

            // Compound state signal — "CA; Seattle", "Texas or Remote", "NY | Chicago",
            // "California (In-Office)", "Indiana 46544"
            // Split on any non-letter/digit boundary and check the first token.
            var firstToken = LocationDelimiterRegex().Split(trimmed)[0].Trim();
            if (!string.IsNullOrEmpty(firstToken))
            {
                if (StateAbbrs.Contains(firstToken))
                    return SetUs(ref country, ref state, firstToken);
                if (StateNames.Contains(firstToken))
                    return SetUs(ref country, ref state, firstToken);
                // 2-word state names as prefix: "New York ..." → "New York"
                var words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length >= 2 && StateNames.Contains(words[0] + " " + words[1]))
                    return SetUs(ref country, ref state, words[0] + " " + words[1]);
            }
        }

        // Country null/empty — check if state already identifies a US location
        if (string.IsNullOrEmpty(country) && !string.IsNullOrEmpty(state))
        {
            if (StateAbbrs.Contains(state) || StateNames.Contains(state))
            {
                country = "US";
                state   = NormalizeStateCode(state);
                return true;
            }
        }

        // Already "US" fast-path
        if (string.Equals(country, "US", StringComparison.OrdinalIgnoreCase))
        {
            country = "US";
            state   = NormalizeStateCode(state);
            return true;
        }

        return false;
    }

    // ── ParseLocation ────────────────────────────────────────────────────────────
    // Central location parser used by all ATS + API handlers.
    // Splits on both commas AND semicolons (Greenhouse uses both).
    // Always produces country = "US" when US signal found, normalised state code, clean city.
    public static void ParseLocation(
        string raw,
        out string? city, out string? state, out string? country,
        out string workType, out string jobWorkMode)
    {
        city        = null;
        state       = null;
        country     = "US";
        workType    = "OnSite";
        jobWorkMode = "OnSite";

        if (string.IsNullOrWhiteSpace(raw)) return;

        var lower = raw.ToLowerInvariant();

        if (lower.Contains("remote"))
        {
            workType    = "Remote";
            jobWorkMode = "Remote";
            // Pure "Remote" / "Remote - US" with no commas/semicolons — no city to parse
            if (!lower.Contains(',') && !lower.Contains(';'))
            {
                // Still run NormalizeToUs in case it's "Remote - US" — country already "US"
                return;
            }
        }
        else if (lower.Contains("hybrid"))
        {
            workType    = "Hybrid";
            jobWorkMode = "Hybrid";
        }

        // Split by BOTH comma and semicolon — Greenhouse uses semicolons heavily
        var parts = raw.Split(new[] { ',', ';' },
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0) return;

        string? cityCandidate = null;
        bool    hasUsSignal   = false;

        foreach (var part in parts)
        {
            var t = part.Trim();
            if (string.IsNullOrEmpty(t)) continue;
            var u = t.ToUpperInvariant();

            // Exact US country variant: "United States", "USA", "US", "U.S.", etc.
            if (CountryVariants.Contains(t))
            {
                hasUsSignal = true;
                continue;
            }

            // Exact US state name: "California", "New York", "Texas", etc.
            if (StateNames.Contains(t))
            {
                state ??= NormalizeStateCode(t);
                hasUsSignal = true;
                continue;
            }

            // Exact 2-letter US state abbr: "CA", "NY", "TX", etc.
            if (t.Length == 2 && StateAbbrs.Contains(t))
            {
                state ??= t.ToUpperInvariant();
                hasUsSignal = true;
                continue;
            }

            // "NY 10036", "CA 90210" — state abbr + zip code
            var spaceTokens = t.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (spaceTokens.Length == 2 &&
                spaceTokens[0].Length == 2 &&
                StateAbbrs.Contains(spaceTokens[0]) &&
                UsZipRegex().IsMatch(spaceTokens[1]))
            {
                state ??= spaceTokens[0].ToUpperInvariant();
                hasUsSignal = true;
                continue;
            }

            // "California (In-Office)", "Texas or Remote", "Indiana 46544"
            // — state name as first word of the token
            if (spaceTokens.Length >= 1 && StateNames.Contains(spaceTokens[0]))
            {
                state ??= NormalizeStateCode(spaceTokens[0]);
                hasUsSignal = true;
                continue;
            }

            // "New York City", "New Hampshire (Remote)" — 2-word state name as prefix
            var words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length >= 2 && StateNames.Contains(words[0] + " " + words[1]))
            {
                state ??= NormalizeStateCode(words[0] + " " + words[1]);
                hasUsSignal = true;
                continue;
            }

            // Pure US zip code: "10950", "94130"
            if (UsZipRegex().IsMatch(t))
            {
                hasUsSignal = true;
                continue;
            }

            // Compound US signal within part: "NY • United States", "Georgia - USA"
            if (u.Contains("UNITED STATES") ||
                u.Contains("U.S.A")         ||
                UsaWordRegex().IsMatch(u))
            {
                hasUsSignal = true;
                if (state == null) state = ExtractStateFromCompound(t);
                continue;
            }

            // Unrecognized — treat as city candidate (first one wins)
            cityCandidate ??= t;
        }

        if (hasUsSignal)
        {
            city    = cityCandidate;
            country = "US";
            state   = NormalizeStateCode(state);
            return;
        }

        // ── No US signal found — parse as potentially foreign ──────────────────
        if (parts.Length == 1)
        {
            // Single unrecognized value — could be a foreign country or a bare city.
            // Set as country so NormalizeToUs can do a final compound check.
            country = parts[0];
            city    = null;
            return;
        }

        // Multi-part with no direct US signal: treat as "City, Country" or "City, State, Country"
        city = parts[0];

        if (parts.Length >= 2)
        {
            var p2 = parts[1];
            if      (StateAbbrs.Contains(p2))      { state = p2.ToUpperInvariant(); country = "US"; }
            else if (StateNames.Contains(p2))      { state = NormalizeStateCode(p2); country = "US"; }
            else if (CountryVariants.Contains(p2)) { /* country stays "US" */ }
            else if (p2.Length > 2)                country = p2;
        }

        if (parts.Length >= 3)
        {
            var p3 = parts[2];
            if (CountryVariants.Contains(p3) || StateNames.Contains(p3) || StateAbbrs.Contains(p3))
            {
                country = "US";
                if (state == null) state = NormalizeStateCode(parts[1]);
            }
            else
            {
                country = p3;
            }
        }

        // Safety: if country ended up as a state name/abbr, move it
        if (country != null && country != "US" &&
            (StateNames.Contains(country) || StateAbbrs.Contains(country)))
        {
            state ??= NormalizeStateCode(country);
            country = "US";
        }

        // Normalize country variants
        if (country != null && CountryVariants.Contains(country))
            country = "US";

        if (country == "US")
            state = NormalizeStateCode(state);
    }

    // ── IsUsFriendlyLocation ─────────────────────────────────────────────────────
    public static bool IsUsFriendlyLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location)) return true;
        if (location.Contains("United States", StringComparison.OrdinalIgnoreCase)) return true;
        if (location.Contains("Remote",        StringComparison.OrdinalIgnoreCase)) return true;
        if (location.Contains("Anywhere",      StringComparison.OrdinalIgnoreCase)) return true;
        if (CountryVariants.Any(v => location.Contains(v, StringComparison.OrdinalIgnoreCase))) return true;
        foreach (var abbr in StateAbbrs)
        {
            if (location.Contains(", " + abbr, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // ── IsUsOrRemoteLocation ─────────────────────────────────────────────────────
    private static readonly string[] ForeignKeywords =
    [
        "CANADA", "UNITED KINGDOM", "GERMANY", "INDIA", "MEXICO", "AUSTRALIA",
        "FRANCE", "SPAIN", "NETHERLANDS", "BRAZIL", "ARGENTINA", "COLOMBIA",
        "PHILIPPINES", "PAKISTAN", "UKRAINE", "POLAND", "ROMANIA", "PORTUGAL",
        "ITALY", "SINGAPORE", "JAPAN", "CHINA", "NIGERIA", "KENYA"
    ];

    public static bool IsUsOrRemoteLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location)) return true;

        var upper = location.ToUpperInvariant();

        foreach (var v in CountryVariants)
            if (upper.Contains(v.ToUpperInvariant())) return true;

        var parts = location.Split(new[] { ',', ';' },
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && (StateAbbrs.Contains(parts[^1]) || StateNames.Contains(parts[^1]))) return true;

        foreach (var k in ForeignKeywords)
            if (upper.Contains(k)) return false;

        return true;
    }

    // ── ParseUsLocation ──────────────────────────────────────────────────────────
    public static (string? city, string? state, string? country) ParseUsLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location)) return (null, null, null);

        var parts = location.Split(new[] { ',', ';' },
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length >= 2)
        {
            var last = parts[^1];
            if (StateAbbrs.Contains(last))
                return (parts[0], last.ToUpperInvariant(), "US");
            if (StateNames.Contains(last))
                return (parts[0], NormalizeStateCode(last), "US");
            if (CountryVariants.Contains(last))
                return (parts[0], parts.Length >= 3 ? NormalizeStateCode(parts[1]) : null, "US");
        }

        if (parts.Length == 1)
        {
            var only = parts[0];
            if (CountryVariants.Contains(only))                           return (null, null, "US");
            if (StateNames.Contains(only) || StateAbbrs.Contains(only))  return (null, NormalizeStateCode(only), "US");
        }

        return (null, null, null);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    // Sets country = "US", normalises state, optionally overwrites state from stateHint.
    private static bool SetUs(ref string? country, ref string? state, string? stateHint)
    {
        country = "US";
        if (stateHint != null && string.IsNullOrEmpty(state))
            state = NormalizeStateCode(stateHint);
        else
            state = NormalizeStateCode(state);
        return true;
    }

    // Scans a compound string like "NY • United States" or "Georgia - USA" for a state token.
    private static string? ExtractStateFromCompound(string text)
    {
        // Check each whitespace/punctuation-separated token
        var tokens = NonAlphanumericRegex().Split(text);
        foreach (var tok in tokens)
        {
            var t = tok.Trim();
            if (t.Length == 2 && StateAbbrs.Contains(t)) return t.ToUpperInvariant();
            if (StateNames.Contains(t))                  return NormalizeStateCode(t);
        }
        // Check 2-word combos: "New York", "North Carolina", etc.
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length - 1; i++)
        {
            var two = words[i] + " " + words[i + 1];
            if (StateNames.Contains(two)) return NormalizeStateCode(two);
        }
        return null;
    }

    // ── Source-generated regexes ──────────────────────────────────────────────────

    // Matches \bUSA\b (word-boundary) case-insensitively — avoids false positives like "Lausanne"
    [GeneratedRegex(@"\bUSA\b", RegexOptions.IgnoreCase)]
    private static partial Regex UsaWordRegex();

    // Splits a compound location on any non-letter/digit boundary: ";", ",", "/", "|", "-", "•", spaces
    [GeneratedRegex(@"[;,/|\-\s•·]+")]
    private static partial Regex LocationDelimiterRegex();

    // Matches a US 5-digit zip code (with optional -4 extension)
    [GeneratedRegex(@"^\d{5}(-\d{4})?$")]
    private static partial Regex UsZipRegex();

    // Splits on any run of non-alphanumeric characters (for compound state extraction)
    [GeneratedRegex(@"[^a-zA-Z0-9]+")]
    private static partial Regex NonAlphanumericRegex();
}
