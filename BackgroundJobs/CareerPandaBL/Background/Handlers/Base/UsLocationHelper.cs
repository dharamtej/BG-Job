// CareerPandaBL/Background/Handlers/UsLocationHelper.cs
// Shared US-location reference data + filter used by all job handlers.
namespace CareerPanda.BL.Background.Handlers;

internal static class UsLocationHelper
{
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

    // ── IsUs ─────────────────────────────────────────────────────────────────────
    // True when the location is recognizably US. A null/empty country is NOT treated
    // as US — the caller must provide a positive signal (country or a US state).
    public static bool IsUs(string? country, string? state)
    {
        if (!string.IsNullOrEmpty(country) && CountryVariants.Contains(country)) return true;
        if (!string.IsNullOrEmpty(country) && (StateAbbrs.Contains(country) || StateNames.Contains(country))) return true;
        if (!string.IsNullOrEmpty(state)   && (StateAbbrs.Contains(state)   || StateNames.Contains(state)))   return true;
        return false;
    }

    // ── NormalizeToUs ────────────────────────────────────────────────────────────
    // Normalizes (country, state) in-place and returns true when the job is US.
    // Handles all edge cases across every handler:
    //   - US country variants ("United States", "USA") → country = "US"
    //   - State name/abbr in country field ("Texas", "CA") → moves to state, country = "US"
    //   - Known US state in state field with null country → country = "US"
    //   - Anything else → returns false (caller should reject the job)
    public static bool NormalizeToUs(ref string? country, ref string? state)
    {
        // Country is a known US variant
        if (!string.IsNullOrEmpty(country) && CountryVariants.Contains(country))
        {
            country = "US";
            return true;
        }

        // Country field contains a US state name or abbreviation — move it to state
        if (!string.IsNullOrEmpty(country) && (StateNames.Contains(country) || StateAbbrs.Contains(country)))
        {
            if (string.IsNullOrEmpty(state)) state = country;
            country = "US";
            return true;
        }

        // Country is null/empty but state identifies a US location
        if (string.IsNullOrEmpty(country) && !string.IsNullOrEmpty(state) &&
            (StateAbbrs.Contains(state) || StateNames.Contains(state)))
        {
            country = "US";
            return true;
        }

        // country = "US" already (fast path after prior normalization)
        if (string.Equals(country, "US", StringComparison.OrdinalIgnoreCase))
        {
            country = "US";
            return true;
        }

        return false;
    }

    // ── ParseLocation ────────────────────────────────────────────────────────────
    // Central comma-split location parser shared by all ATS + API handlers.
    // Handles: "City, ST", "City, State", "City, ST, Country", "Remote", "Hybrid, NY", etc.
    // Always sets country = "US" when the location is recognizably US.
    // Sets country to the raw foreign string otherwise (caller should call NormalizeToUs
    // and reject when it returns false).
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
            // Pure "Remote" with no comma — no city/state to parse
            if (!lower.Contains(',')) return;
        }
        else if (lower.Contains("hybrid"))
        {
            workType    = "Hybrid";
            jobWorkMode = "Hybrid";
        }

        var parts = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        // Single-part: "United States" / "California" / "London"
        if (parts.Length == 1)
        {
            var only = parts[0];
            if (CountryVariants.Contains(only))  return;                   // "United States" → country stays "US"
            if (StateNames.Contains(only) || StateAbbrs.Contains(only))
                { state = only; return; }                                  // "Texas" → state
            country = only;                                                // foreign or bare city — caller filters
            city    = null;
            return;
        }

        city = parts[0];

        if (parts.Length >= 2)
        {
            var part2 = parts[1];
            if      (StateAbbrs.Contains(part2))          state = part2;  // "NY"
            else if (StateNames.Contains(part2))          state = part2;  // "New York"
            else if (CountryVariants.Contains(part2))     { /* country stays "US" */ }
            else if (part2.Length > 2)                    country = part2; // foreign country
            // 1–2 char tokens that aren't a known US abbr are ignored
        }

        if (parts.Length >= 3)
        {
            var part3 = parts[2];
            country = CountryVariants.Contains(part3) ? "US"
                    : (StateNames.Contains(part3) || StateAbbrs.Contains(part3)) ? "US"
                    : part3;
            // If part3 resolved to "US" and part2 wasn't caught as state, promote city→city, part2→state
            if (country == "US" && state == null && parts.Length >= 2)
                state = parts[1];
        }

        // Final safety: if country ended up as a state name/abbr, move it
        if (country != null && country != "US" &&
            (StateNames.Contains(country) || StateAbbrs.Contains(country)))
        {
            if (string.IsNullOrEmpty(state)) state = country;
            country = "US";
        }

        // Normalize country variants to "US"
        if (country != null && CountryVariants.Contains(country))
            country = "US";
    }

    // ── IsUsFriendlyLocation ─────────────────────────────────────────────────────
    // True when a free-text location string is US-eligible:
    // null/empty (fully remote), explicit "United States", "Remote", "Anywhere", or contains
    // a known US state abbreviation in ", XX" form (e.g. "New York, NY").
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
    // Used by RemoteOK: accepts empty/null (worldwide remote), explicit US signals,
    // and rejects known foreign country keywords.
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

        var parts = location.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && StateAbbrs.Contains(parts[^1])) return true;
        if (parts.Length >= 2 && StateNames.Contains(parts[^1])) return true;

        foreach (var k in ForeignKeywords)
            if (upper.Contains(k)) return false;

        return true; // unknown — allow through (remote boards often have sparse location data)
    }

    // ── ParseUsLocation ──────────────────────────────────────────────────────────
    // Used by RemoteOK: parses a free-text location into (city, state, country).
    public static (string? city, string? state, string? country) ParseUsLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location)) return (null, null, null);

        var parts = location.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length >= 2)
        {
            var last = parts[^1];
            if (StateAbbrs.Contains(last) || StateNames.Contains(last))
                return (parts[0], last, "US");
            if (CountryVariants.Contains(last))
                return (parts[0], parts.Length >= 3 ? parts[1] : null, "US");
        }

        if (parts.Length == 1)
        {
            var only = parts[0];
            if (CountryVariants.Contains(only))                           return (null, null, "US");
            if (StateNames.Contains(only) || StateAbbrs.Contains(only))  return (null, only, "US");
        }

        return (null, null, null);
    }
}
