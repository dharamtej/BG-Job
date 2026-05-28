// DataAccess/Util/JobClassifier.cs
// Shared helper that tags a raw_job's classification flags from its description,
// structured ContractType, and company name. Called by BulkUpsertRawJobsAsync as
// the single chokepoint so every fetch handler is covered.
//
// SEMANTICS — only sets a flag when it isn't already `true`; handler-specific
// detection is preserved. Negation phrases ("do not sponsor") suppress the
// keyword-based sponsor flag.
using System.Text.RegularExpressions;
using CareerPanda.DataAccess.Entities.Api;

namespace CareerPanda.DataAccess.Util;

public static class JobClassifier
{
    // Word-boundary regex for W2 / W-2 to avoid false positives like "windows 2".
    private static readonly Regex W2Regex = new(@"\bw[-\s]?2\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static void ApplyKeywordFlags(ApiRawJob job, string? description, string? employmentType = null, string? companyName = null)
    {
        var d = description; var t = employmentType; var n = companyName;

        // Suppresses sponsor keyword matches when the posting explicitly disclaims sponsorship.
        var hasNegation = Has(d,
            "do not sponsor", "does not sponsor", "no sponsorship", "unable to sponsor",
            "cannot sponsor", "will not sponsor", "no h-1b", "no h1b", "must be authorized to work");

        // ── C2C
        if (job.IsC2C != true && Has(d,
            "c2c", "c2c only", "c2c welcome", "open to c2c", "corp to corp",
            "corp-to-corp", "corp-2-corp", "c-2-c", "corp 2 corp"))
            job.IsC2C = true;

        // ── C2H
        if (job.IsContractToHire != true && Has(d,
            "contract to hire", "contract-to-hire", "c2h", "right to hire", "right-to-hire",
            "temp to perm", "temp-to-perm", "contract then hire", "contract-then-hire"))
            job.IsContractToHire = true;

        // ── 1099 / Freelance
        if (job.IsFreelanceJob != true && Has(d,
            "1099", "1099-nec", "1099 contractor", "1099 basis",
            "freelance", "independent contractor", "freelancer", "self-employed"))
            job.IsFreelanceJob = true;

        // ── W2 — regex with word boundaries
        if (job.IsW2 != true && !string.IsNullOrEmpty(d) && W2Regex.IsMatch(d!))
            job.IsW2 = true;

        // ── Contract — description OR structured ContractType
        if (job.IsContractJob != true && (
            Has(d, "contract role", "contract position", "contractor", "contract opportunity", "contract basis", " on contract") ||
            HasType(t, "contract", "contractor", "temporary")))
            job.IsContractJob = true;

        // ── Internship → University-style entry
        if (job.IsUniversityJob != true && (
            HasType(t, "internship", "intern") ||
            Has(d, "academic position", "faculty position", "tenure-track", "tenure track", "intern role", "internship program")))
            job.IsUniversityJob = true;

        // ── Prime vendor / direct-client (staffing layer hint)
        if (job.IsPrimeVendor != true && Has(d,
            "prime vendor", "tier 1 vendor", "tier-1 vendor",
            "direct client", "direct vendor", "end client", "implementation partner"))
            job.IsPrimeVendor = true;

        // ── Staffing firm
        if (job.IsStaffing != true && (
            Has(d, "staffing firm", "staffing agency", "recruiting firm", "talent acquisition partner",
                   "consultancy", "resource augmentation") ||
            Has(n, "staffing", "consulting", "solutions", "talent partners", "resources", "infotech")))
            job.IsStaffing = true;

        // ── H1B sponsorship — explicit phrases, with negation guard
        if (!hasNegation && job.IsH1BSponsored != true && Has(d,
            "h1b", "h-1b", "h1-b", "h1b sponsor", "h-1b sponsor",
            "visa sponsor", "willing to sponsor", "will sponsor",
            "sponsorship available", "open to sponsorship",
            "stem opt", "opt/cpt", "tn visa"))
            job.IsH1BSponsored = true;

        // ── Sponsored (broader visa-friendly)
        if (!hasNegation && job.IsSponsored != true && (
            job.IsH1BSponsored == true ||
            Has(d, "visa sponsorship", "opt extension", " opt ", " cpt ", " ead ")))
            job.IsSponsored = true;

        // ── Startup
        if (job.IsStartupJob != true && Has(d,
            "series a", "series b", "series c", "seed funded", "seed-stage",
            "early-stage startup", "early stage startup", "pre-seed"))
            job.IsStartupJob = true;

        // ── Non-Profit
        if (job.IsNonProfitJob != true && Has(d, "nonprofit", "non-profit", "501(c)", "ngo", "charitable organization"))
            job.IsNonProfitJob = true;
    }

    private static bool Has(string? text, params string[] needles)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var n in needles)
            if (text.Contains(n, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Match the structured ContractType field with substring + case-insensitive.</summary>
    private static bool HasType(string? type, params string[] needles)
    {
        if (string.IsNullOrEmpty(type)) return false;
        foreach (var n in needles)
            if (type.Contains(n, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
