using System.Collections.Concurrent;

namespace CareerPanda.BL.Background;

public class JobCancellationRegistry
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _tokens = new();

    public CancellationTokenSource Register(string jobId)
    {
        var cts = new CancellationTokenSource();
        _tokens[jobId] = cts;
        return cts;
    }

    public bool TryCancel(string jobId)
    {
        if (_tokens.TryRemove(jobId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
            return true;
        }
        return false;
    }

    public void Remove(string jobId)
    {
        if (_tokens.TryRemove(jobId, out var cts))
            cts.Dispose();
    }
}
