// CareerPandaBL/Logic/JobFetchBL.cs
using System.Text.Json;
using CareerPanda.BL.Background;
using CareerPanda.BL.Background.Handlers;
using CareerPanda.DataAccess.DA;
using CareerPanda.DataAccess.Entities.Api;
using CareerPanda.Framework;
using CareerPanda.Framework.Util;
using Microsoft.Extensions.Logging;

namespace CareerPanda.BL.Logic;

public class JobFetchBL
{
    // Maps URL-friendly category keys → handler JobType strings
    public static readonly IReadOnlyDictionary<string, string> CategoryMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "alljobs",              "AllJobs"              },
            { "startupjobs",          "StartupJobs"          },
            { "universityjobs",       "UniversityJobs"       },
            { "nonprofitjobs",        "NonProfitJobs"        },
            { "contractjobs",         "ContractJobs"         },
            { "h1bjobs",              "H1BJobs"              },
            { "primevendorjobs",      "PrimeVendorJobs"      },
            { "remoteoknobs",         "RemoteOkJobs"         },
            { "jobicyjobs",           "JobicyJobs"           },
            { "adzunajobs",           "AdzunaJobs"           },
            { "h1bsponorenrichment",  "H1BSponsorEnrichment" }
        };

    private readonly JobBL _jobBl;
    private readonly IJobFetchDA _fetchDa;
    private readonly ILogger<JobFetchBL> _logger;

    public JobFetchBL(JobBL jobBl, IJobFetchDA fetchDa, ILogger<JobFetchBL> logger)
    {
        _jobBl   = jobBl;
        _fetchDa = fetchDa;
        _logger  = logger;
    }

    // ── Trigger ───────────────────────────────────────────────────────────────

    public async Task<FrameworkResponse> TriggerFetchAsync(
        string category, JobFetchInput input, string userId)
    {
        var resp = new FrameworkResponse { Status = Status.Failed };

        if (!CategoryMap.TryGetValue(category, out var jobType))
        {
            resp.Message = $"Unknown category '{category}'. Valid: {string.Join(", ", CategoryMap.Keys)}";
            return resp;
        }

        var task = new BackgroundTask
        {
            Name        = $"Fetch {jobType} — {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC",
            Description = JsonSerializer.Serialize(input),   // consumed by handler as InputPayload
            JobType     = jobType
        };

        resp = await _jobBl.QueueBackgroundJobAsync(task, userId);
        if (resp.Status == Status.Success)
            _logger.LogInformation("Triggered {JobType} fetch, RunId={Id}", jobType, task.Id);

        return resp;
    }

    // ── Fetch run status ──────────────────────────────────────────────────────

    public Task<FrameworkResponse> GetFetchRunAsync(string runId) =>
        _fetchDa.GetFetchRunAsync(runId);

    public Task<FrameworkResponse> GetFetchRunsAsync(
        int pageNumber, int pageSize,
        string? category = null, string? status = null)
    {
        var stored = category != null && CategoryMap.TryGetValue(category, out var jt) ? jt : category;
        return _fetchDa.GetFetchRunsAsync(pageNumber, pageSize, stored, status);
    }
}
