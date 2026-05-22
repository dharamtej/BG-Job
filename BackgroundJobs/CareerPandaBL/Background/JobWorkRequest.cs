namespace CareerPanda.BL.Background;

public class JobWorkRequest
{
    public string JobId { get; set; } = string.Empty;

    public string UserId { get; set; } = string.Empty;

    public string JobType { get; set; } = "Default";

    public string? InputPayload { get; set; }
}
