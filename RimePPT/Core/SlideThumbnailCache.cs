using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RimePPT.Core;

public sealed class SlideThumbnailCache : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "RimePPT", "thumbnails", Guid.NewGuid().ToString("N"));
    private readonly IPresentationController _controller;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly Dictionary<int, string> _files = new();
    private readonly Queue<int> _order = new();
    private bool _disposed;
    public SlideThumbnailCache(IPresentationController controller) { _controller = controller; Directory.CreateDirectory(_directory); }
    public async Task<string> GetAsync(int page, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_files.TryGetValue(page, out var cached)) return cached;
            var path = Path.Combine(_directory, page + ".png");
            await _controller.ExportSlideThumbnailAsync(page, path, 240, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(_disposed, this);
            _files.Add(page, path); _order.Enqueue(page);
            long Bytes() { long total = 0; foreach (var file in _files.Values) total += new FileInfo(file).Length; return total; }
            while (_files.Count > 48 || Bytes() > 32L * 1024 * 1024)
            { int old = _order.Dequeue(); if (_files.Remove(old, out var file)) File.Delete(file); }
            return path;
        }
        finally { _gate.Release(); if (_disposed) Cleanup(); }
    }
    private void Cleanup() { try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    public void Dispose() { _disposed = true; if (_gate.CurrentCount == 1) Cleanup(); }
}
