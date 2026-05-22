using System.Collections;

namespace CareerPanda.Framework;

[Serializable]
public class FWException : Exception
{
    private const string Format = "{0}{1}{2}";
    private string? innerStackTrace;

    public FWException()
    {
    }

    public FWException(string message, Exception ex) : base(message, ex)
    {
        if (ex != null)
            innerStackTrace = ex.StackTrace;
    }

    public IDictionary? DataObject { get; set; }

    public FWException(string message) : base(message)
    {
    }

    public override string StackTrace =>
        string.Format(Format, innerStackTrace, Environment.NewLine, base.StackTrace);

    public string? ErrorCode { get; set; }

    public int RowNumber { get; set; }

    public string? ErrorMessage { get; set; }
}
