namespace VigilWatt.ProtocolCapture;

public sealed class JsonLinesWriter : IAsyncDisposable
{
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonLinesWriter(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read));
    }

    public async ValueTask WriteAsync(CaptureRecord record, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await _writer.WriteLineAsync(record.ToJsonLine());
            await _writer.FlushAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try { await _writer.DisposeAsync(); }
        finally { _gate.Release(); _gate.Dispose(); }
    }
}
