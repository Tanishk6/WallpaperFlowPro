using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using WallpaperFlow.Core.Infrastructure;

namespace WallpaperFlow.Core.Rendering;

/// <summary>
/// JSON IPC client for mpv's --input-ipc-server named pipe. One JSON object per line in both
/// directions; replies are matched by request_id, events and property changes are raised.
/// </summary>
public sealed class MpvIpcClient : IAsyncDisposable
{
    private readonly string _pipeName;
    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement?>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private long _nextId;
    private Task? _reader;

    public event Action<string, JsonElement?>? PropertyChanged;
    public event Action<string, JsonElement>? EventReceived;
    public event Action? Disconnected;
    public bool IsConnected => _pipe?.IsConnected == true;

    public MpvIpcClient(string pipeName) => _pipeName = pipeName;

    public async Task ConnectAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(250, ct).ConfigureAwait(false);
                _pipe = pipe;
                _writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                _reader = Task.Run(ReadLoop);
                return;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                last = ex;
                await Task.Delay(60, ct).ConfigureAwait(false);
            }
        }
        throw new TimeoutException($"mpv IPC pipe '{_pipeName}' did not become available", last);
    }

    private async Task ReadLoop()
    {
        try
        {
            using var reader = new StreamReader(_pipe!, Encoding.UTF8, false, 4096, leaveOpen: true);
            while (!_cts.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                if (line == null) break;
                if (line.Length == 0) continue;
                Dispatch(line);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Debug("mpv IPC read ended: " + ex.Message); }
        finally
        {
            foreach (var kv in _pending) kv.Value.TrySetException(new IOException("mpv disconnected"));
            _pending.Clear();
            Disconnected?.Invoke();
        }
    }

    private void Dispatch(string line)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); } catch { return; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.TryGetProperty("request_id", out var rid) && rid.ValueKind == JsonValueKind.Number)
            {
                if (_pending.TryRemove(rid.GetInt64(), out var tcs))
                {
                    var ok = root.TryGetProperty("error", out var err) && err.GetString() == "success";
                    if (ok) tcs.TrySetResult(root.TryGetProperty("data", out var d) ? d.Clone() : null);
                    else tcs.TrySetException(new InvalidOperationException("mpv: " + (err.ValueKind == JsonValueKind.String ? err.GetString() : "error")));
                }
                return;
            }
            if (!root.TryGetProperty("event", out var ev)) return;
            var name = ev.GetString() ?? "";
            if (name == "property-change")
            {
                var prop = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                JsonElement? data = root.TryGetProperty("data", out var dv) ? dv.Clone() : null;
                PropertyChanged?.Invoke(prop, data);
            }
            else EventReceived?.Invoke(name, root.Clone());
        }
    }

    /// <summary>Sends a command; returns mpv's "data" member (or null).</summary>
    public async Task<JsonElement?> CommandAsync(params object?[] args)
    {
        if (_writer == null || !IsConnected) throw new IOException("mpv IPC not connected");
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var json = JsonSerializer.Serialize(new Dictionary<string, object> { ["command"] = args, ["request_id"] = id });
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try { await _writer.WriteLineAsync(json).ConfigureAwait(false); }
        finally { _writeLock.Release(); }

        var done = await Task.WhenAny(tcs.Task, Task.Delay(3000)).ConfigureAwait(false);
        if (done != tcs.Task)
        {
            _pending.TryRemove(id, out _);
            throw new TimeoutException("mpv did not answer: " + args.FirstOrDefault());
        }
        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>Fire-and-forget variant for high-rate updates (audio reactive zoom etc.); errors are ignored.</summary>
    public void Send(params object?[] args)
    {
        if (_writer == null || !IsConnected) return;
        var json = JsonSerializer.Serialize(new Dictionary<string, object> { ["command"] = args });
        if (!_writeLock.Wait(0)) return; // drop frame rather than queue up
        try { _writer.WriteLine(json); }
        catch { }
        finally { _writeLock.Release(); }
    }

    public Task SetAsync(string property, object? value) => CommandAsync("set_property", property, value);
    public Task ObserveAsync(int id, string property) => CommandAsync("observe_property", id, property);

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _pipe?.Dispose(); } catch { }
        if (_reader != null) { try { await _reader.ConfigureAwait(false); } catch { } }
        _cts.Dispose();
        _writeLock.Dispose();
    }
}
