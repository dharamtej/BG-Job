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
            "cannot sponsor", "will not sponsor", "no h-1b", "no h1b", "must be authorized to work",
            "no green card", "no gc sponsor", "no opt", "no cpt", "no tn", "no j-1", "no e-3");

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

        // ── H-1B sponsorship — explicit phrases, with negation guard
        if (!hasNegation && job.IsH1BSponsored != true && Has(d,
            "h1b", "h-1b", "h1-b", "h1b sponsor", "h-1b sponsor",
            "visa sponsor", "willing to sponsor", "will sponsor",
            "sponsorship available", "open to sponsorship"))
            job.IsH1BSponsored = true;

        // ── OPT / CPT (F-1 students)
        if (!hasNegation && job.IsOptCpt != true && Has(d,
            " opt ", "opt/cpt", "opt / cpt", "stem opt", "opt extension",
            "f-1 visa", "f1 visa", " cpt ", "optional practical training",
            "curricular practical training"))
            job.IsOptCpt = true;

        // ── TN Visa (Canada / Mexico professionals under USMCA)
        if (!hasNegation && job.IsTnVisa != true && Has(d,
            "tn visa", "tn-1", "tn-2", "nafta visa", "usmca", "trade nafta",
            "tn status", "tn work authorization"))
            job.IsTnVisa = true;

        // ── E-3 Visa (Australian specialty occupation)
        if (!hasNegation && job.IsE3Visa != true && Has(d,
            "e-3", "e3 visa", "e-3 visa", "australian visa",
            "australian specialty occupation"))
            job.IsE3Visa = true;

        // ── J-1 Visa (Exchange visitors)
        if (!hasNegation && job.IsJ1Visa != true && Has(d,
            "j-1", "j1 visa", "j-1 visa", "exchange visitor",
            "exchange program visa", "cultural exchange visa"))
            job.IsJ1Visa = true;

        // ── Green Card / Permanent Residency sponsorship
        if (!hasNegation && job.IsGreenCard != true && Has(d,
            "green card", "gc sponsor", "perm filing", "permanent resident",
            "employment-based green card", "employment based green card",
            "eb-2", "eb-3", "eb2", "eb3", "labor certification", "perm labor",
            "permanent residency sponsor", "green card sponsor"))
            job.IsGreenCard = true;

        // ── Sponsored (broader: any visa-friendly signal across all types)
        if (!hasNegation && job.IsSponsored != true && (
            job.IsH1BSponsored == true || job.IsOptCpt    == true ||
            job.IsTnVisa       == true || job.IsE3Visa    == true ||
            job.IsJ1Visa       == true || job.IsGreenCard == true ||
            Has(d, "visa sponsorship", " ead ")))
            job.IsSponsored = true;

        // ── Security Clearance required
        if (job.IsSecurityClearanceRequired != true && Has(d,
            "security clearance", "secret clearance", "top secret", "ts/sci", "ts sci",
            "public trust", "suitability clearance", "dod clearance", "government clearance",
            "clearance required", "must have clearance", "active clearance"))
            job.IsSecurityClearanceRequired = true;

        // ── Startup
        if (job.IsStartupJob != true && Has(d,
            "series a", "series b", "series c", "seed funded", "seed-stage",
            "early-stage startup", "early stage startup", "pre-seed"))
            job.IsStartupJob = true;

        // ── Non-Profit
        if (job.IsNonProfitJob != true && Has(d, "nonprofit", "non-profit", "501(c)", "ngo", "charitable organization"))
            job.IsNonProfitJob = true;

        // ── JobLevel fallback from job title when handler didn't populate it
        if (string.IsNullOrWhiteSpace(job.JobLevel))
        {
            var title = job.JobTitle ?? "";
            var tl    = title.ToLowerInvariant();
            if (tl.Contains("intern") || tl.Contains("co-op") || tl.Contains("trainee") || tl.Contains("apprentice"))
                job.JobLevel = "Entry";
            else if (tl.Contains("junior") || tl.Contains("jr.") || tl.Contains(" jr ") || tl.Contains("associate ") || tl.Contains("entry level"))
                job.JobLevel = "Entry";
            else if (tl.Contains("principal") || tl.Contains("staff "))
                job.JobLevel = "Lead";
            else if (tl.StartsWith("lead ") || tl.Contains(" lead ") || tl.EndsWith(" lead"))
                job.JobLevel = "Lead";
            else if (tl.Contains("senior") || tl.Contains("sr.") || tl.Contains(" sr ") || tl.EndsWith(" sr"))
                job.JobLevel = "Senior";
            else if (tl.Contains("director") || tl.Contains("head of "))
                job.JobLevel = "Director";
            else if (tl.Contains(" vp") || tl.Contains("vice president") || tl.Contains("chief ") || tl.Contains("cto") || tl.Contains("cfo") || tl.Contains("ceo") || tl.Contains("coo"))
                job.JobLevel = "Executive";
            else if (tl.Contains("manager") || tl.Contains("supervisor"))
                job.JobLevel = "Manager";
        }

        // ── Veterans eligible (preference or open announcement)
        if (job.IsVeteransEligible != true && Has(d,
            "veterans preference", "veteran preference", "veterans' preference",
            "hiring preference for veterans", "veteran hiring", "vets-4212",
            "disabled veteran", "service-disabled veteran", "military veteran",
            "veteran status", "encourage veterans to apply"))
            job.IsVeteransEligible = true;
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
