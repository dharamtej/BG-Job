// CareerPandaBL/Background/Handlers/JobValidationGate.cs
// Centralized "should this row be stored?" gate.
// A handler's MapJob should return null when this gate rejects the row.
//
// Rules (drop the row if ANY is violated):
//   - Source, SourceId, JobTitle, JobLink, CompanyName, JobDescription must all be non-empty
//   - Country must resolve to US (or recognized US state)
//   - WorkType must be one of {OnSite, Remote, Hybrid}
//   - ContractType must be one of {FullTime, PartTime, Contract, Temporary, Internship}
//   - PostDate must be non-null
//
// Handlers that don't natively populate ContractType (e.g. Ashby/Lever/Greenhouse)
// can call DeriveContractType(isContract, isInternship) before invoking the gate.
using CareerPanda.DataAccess.Entities.Api;
using Microsoft.Extensions.Logging;

namespace CareerPanda.BL.Background.Handlers;

internal static class JobValidationGate
{
    private static readonly HashSet<string> ValidWorkTypes =
        new(StringComparer.OrdinalIgnoreCase) { "OnSite", "Remote", "Hybrid" };

    private static readonly HashSet<string> ValidContractTypes =
        new(StringComparer.OrdinalIgnoreCase) { "FullTime", "PartTime", "Contract", "Temporary", "Internship" };

    // Returns true if the job should be stored; false (with reason) otherwise.
    // Also normalizes country/state in-place via NormalizeToUs so the stored value is always canonical.
    public static bool TryAccept(ApiRawJob job, out string? rejectReason)
    {
        if (string.IsNullOrWhiteSpace(job.Source))          { rejectReason = "Source missing";          return false; }
        if (string.IsNullOrWhiteSpace(job.SourceId))        { rejectReason = "SourceId missing";        return false; }
        if (string.IsNullOrWhiteSpace(job.JobTitle))        { rejectReason = "JobTitle missing";        return false; }
        if (string.IsNullOrWhiteSpace(job.JobLink))         { rejectReason = "JobLink missing";         return false; }
        if (string.IsNullOrWhiteSpace(job.CompanyName))     { rejectReason = "CompanyName missing";     return false; }
        if (string.IsNullOrWhiteSpace(job.JobDescription))  { rejectReason = "JobDescription missing";  return false; }

        // Normalize country/state in-place: US variants → "US", state-in-country → moved to state.
        // Null country (worldwide remote from Arbeitnow/Jobicy) is allowed through.
        if (job.Country != null)
        {
            var country = job.Country;
            var state   = job.State;
            if (!UsLocationHelper.NormalizeToUs(ref country, ref state))
            {
                rejectReason = $"Country not US: '{job.Country}'";
                return false;
            }
            job.Country = country;
            job.State   = state;
        }

        if (string.IsNullOrWhiteSpace(job.WorkType) || !ValidWorkTypes.Contains(job.WorkType))
        {
            rejectReason = $"WorkType invalid: '{job.WorkType}'";
            return false;
        }

        if (string.IsNullOrWhiteSpace(job.ContractType) || !ValidContractTypes.Contains(job.ContractType))
        {
            rejectReason = $"ContractType invalid: '{job.ContractType}'";
            return false;
        }

        if (job.PostDate is null)
        {
            rejectReason = "PostDate missing";
            return false;
        }

        rejectReason = null;
        return true;
    }

    // Convenience: returns the job if accepted, null if rejected.
    public static ApiRawJob? AcceptOrNull(ApiRawJob job, ILogger? logger = null, string? handlerTag = null)
    {
        if (TryAccept(job, out var reason)) return job;
        logger?.LogDebug("{Tag} dropped {Company}/{Title}: {Reason}",
            handlerTag ?? "[Validation]", job.CompanyName, job.JobTitle, reason);
        return null;
    }

    // Filters a job list through TryAccept before any BulkUpsertRawJobsAsync call.
    // TryAccept also normalizes country/state in-place, so stored values are always canonical.
    // Call this in every handler right before BulkUpsertRawJobsAsync — no non-US job reaches the DB.
    public static List<ApiRawJob> FilterValid(IEnumerable<ApiRawJob> jobs, ILogger? logger = null, string? tag = null)
    {
        var result = new List<ApiRawJob>();
        foreach (var job in jobs)
        {
            if (TryAccept(job, out var reason))
                result.Add(job);
            else
                logger?.LogDebug("{Tag} dropped {Company}/{Title}: {Reason}",
                    tag ?? "[Gate]", job.CompanyName, job.JobTitle, reason);
        }
        return result;
    }

    // Derive ContractType from boolean flags when the source API doesn't expose it directly.
    // Order matters: Internship beats Contract beats default FullTime.
    public static string DeriveContractType(bool isContract, bool isInternship, bool isPartTime = false)
    {
        if (isInternship) return "Internship";
        if (isContract)   return "Contract";
        if (isPartTime)   return "PartTime";
        return "FullTime";
    }
}
