using CareerPanda.Framework.Util;

namespace CareerPanda.Framework;

public class FrameworkResponse
{
    private Status status = Status.Failed;

    public Status Status
    {
        get => status;
        set => status = value;
    }

    public string? ErrorCode { get; set; }

    public string? TransactionCode { get; set; }

    public string? Message { get; set; }

    public FWException? Exception { get; set; }

    public object? Entity { get; set; }

    public string? Response { get; set; }

    public int TotalRecords { get; set; }
}
