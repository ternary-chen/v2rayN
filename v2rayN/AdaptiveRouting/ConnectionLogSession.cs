using System.Threading.Channels;

namespace AdaptiveRouting;

/// <summary>Bounded background diagnostics. Never waits in the core's stdout handler or the proxy forwarding path.</summary>
public sealed class ConnectionLogSession : IAsyncDisposable
{
    private readonly ConnectionLogFormatter _formatter;
    private readonly Func<string, Task> _write;
    private readonly Func<IReadOnlySet<int>, CancellationToken, Task<IReadOnlyList<TcpProcessOwner>>> _capture;
    private readonly Channel<XrayAccessEntry> _queue = Channel.CreateBounded<XrayAccessEntry>(new BoundedChannelOptions(512) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;

    public ConnectionLogSession(ConnectionLogFormatter formatter, Func<string, Task> write,
        Func<IReadOnlySet<int>, CancellationToken, Task<IReadOnlyList<TcpProcessOwner>>>? capture = null)
    {
        _formatter = formatter; _write = write; _capture = capture ?? TcpProcessSnapshot.CaptureAsync;
        _worker = Task.Run(WorkAsync);
    }

    public void Post(string raw)
    {
        var entry = ConnectionLogFormatter.Parse(raw);
        if (entry == null) { _ = WriteSafe(raw); return; }
        if (!_queue.Writer.TryWrite(entry)) _ = WriteSafe(_formatter.Format(entry, [], "日志查询繁忙或已停止"));
    }

    private async Task WriteSafe(string line)
    {
        try { await _write(line).WaitAsync(TimeSpan.FromSeconds(2), _stop.Token); } catch { /* Logging must not stop the core or forwarding. */ }
    }

    private async Task WorkAsync()
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(_stop.Token))
            {
                // Batch macOS lsof invocations during bursts instead of spawning one command per line.
                if (OperatingSystem.IsMacOS()) await Task.Delay(250, _stop.Token);
                var batch = new List<XrayAccessEntry>();
                while (batch.Count < 64 && _queue.Reader.TryRead(out var entry)) batch.Add(entry);
                IReadOnlyList<TcpProcessOwner> owners = [];
                string? reason = null;
                try { owners = await _capture(_formatter.Ports, _stop.Token).WaitAsync(TimeSpan.FromSeconds(3), _stop.Token); }
                catch { reason = "系统连接表查询失败或超时"; }
                foreach (var entry in batch) await WriteSafe(_formatter.Format(entry, owners, reason));
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            while (_queue.Reader.TryRead(out var entry)) await WriteSafe(_formatter.Format(entry, [], "核心正在停止"));
        }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        _stop.CancelAfter(TimeSpan.FromSeconds(2));
        await _worker;
        _stop.Dispose();
    }
}
