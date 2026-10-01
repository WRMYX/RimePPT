using System;
using System.Collections.Generic;
using System.Linq;

namespace RimePPT.Core.Ink;

/// <summary>Snapshots share immutable committed strokes; live previews must never be recorded.</summary>
public sealed class InkHistory
{
    private sealed record Change(int Page, StrokeData[] Before, StrokeData[] After);
    private readonly LinkedList<Change> _undo = new();
    private readonly List<Change> _redo = new();
    private readonly Dictionary<int, List<StrokeData>> _pages;
    public InkHistory(Dictionary<int, List<StrokeData>> pages) => _pages = pages;
    public StrokeData[] Snapshot(int page) => _pages.TryGetValue(page, out var list) ? list.ToArray() : Array.Empty<StrokeData>();
    public bool CanUndo(int page) => _undo.Any(c => c.Page == page);
    public bool CanRedo(int page) => _redo.Any(c => c.Page == page);
    public void Record(int page, StrokeData[] before)
    {
        var after = Snapshot(page);
        if (before.SequenceEqual(after)) return;
        _redo.RemoveAll(c => c.Page == page);
        _undo.AddLast(new Change(page, before, after));
        while (_undo.Count(c => c.Page == page) > 100)
        { var node = _undo.First; while (node is not null && node.Value.Page != page) node = node.Next; if (node is not null) _undo.Remove(node); }
        while (RetainedBytes() > 64L * 1024 * 1024 && _undo.First is not null) _undo.RemoveFirst();
    }
    public long RetainedBytes() => _undo.Concat(_redo).SelectMany(c => c.Before.Concat(c.After)).Distinct()
        .Sum(s => 128L + s.Dots.Count * 32L);
    public bool Undo(int page)
    {
        var node = _undo.Last; while (node is not null && node.Value.Page != page) node = node.Previous;
        if (node is null) return false;
        var change = node.Value; _undo.Remove(node); _pages[page] = new(change.Before); _redo.Add(change); return true;
    }
    public bool Redo(int page)
    {
        int i = _redo.FindLastIndex(c => c.Page == page); if (i < 0) return false;
        var change = _redo[i]; _redo.RemoveAt(i); _pages[page] = new(change.After); _undo.AddLast(change); return true;
    }
    public bool Clear(int page)
    {
        var before = Snapshot(page); if (before.Length == 0) return false;
        _pages[page] = new(); Record(page, before); return true;
    }
    public void Reset() { _undo.Clear(); _redo.Clear(); }
}
