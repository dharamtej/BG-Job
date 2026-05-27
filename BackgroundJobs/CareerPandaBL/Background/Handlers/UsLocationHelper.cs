// CareerPandaBL/Background/Handlers/UsLocationHelper.cs
// Shared US-location reference data + filter used by ATS handlers.
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

    // True when the location is recognizably US. A null/empty country is NOT treated
    // as US — the caller must provide a positive signal (country or a US state).
    public static bool IsUs(string? country, string? state)
    {
        if (!string.IsNullOrEmpty(country) && CountryVariants.Contains(country)) return true;
        if (!string.IsNullOrEmpty(state) && (StateAbbrs.Contains(state) || StateNames.Contains(state))) return true;
        return false;
    }
}
