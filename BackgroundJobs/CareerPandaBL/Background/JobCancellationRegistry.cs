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

    /// <summary>Adopt an externally-owned CTS (e.g. a linked source created by a parent chain).</summary>
    public void Register(string jobId, CancellationTokenSource cts) => _tokens[jobId] = cts;

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
