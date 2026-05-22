// CareerPandaBL/Background/Handlers/JobFetchInput.cs
namespace CareerPanda.BL.Background.Handlers;

public class JobFetchInput
{
    /// <summary>How many hours back to fetch jobs. Default 24.</summary>
    public int HoursBack { get; set; } = 24;

    /// <summary>Maximum API pages to fetch per run. Default 5 (~50-100 jobs).</summary>
    public int MaxPages { get; set; } = 5;

    /// <summary>Optional additional keyword filter sent to the API.</summary>
    public string? SearchQuery { get; set; }

    /// <summary>Location filter. Default "United States".</summary>
    public string? Location { get; set; } = "United States";
}
