using System.Collections.Generic;
using System.Linq;

namespace RimePPT.Core.Ink;

/// <summary>One isolated input session per pointer. Ending a contact never ends its neighbours.</summary>
public sealed class InkContactManager
{
    private readonly Dictionary<uint, InkInputSession> _sessions = new();
    public IEnumerable<KeyValuePair<uint, InkInputSession>> Contacts => _sessions;
    public int Count => _sessions.Count;
    public InkInputSession? Find(uint id) => _sessions.GetValueOrDefault(id);
    public bool Begin(uint id, InkSample sample, InkToolSnapshot tool, InkViewport viewport)
    {
        if (_sessions.ContainsKey(id)) return false;
        var session = new InkInputSession { Viewport = viewport };
        if (!session.Begin(id, sample, tool)) return false;
        _sessions.Add(id, session); return true;
    }
    public StrokeData? End(uint id, InkSample? tail)
    {
        if (!_sessions.Remove(id, out var session)) return null;
        return session.End(id, tail);
    }
    public uint[] Ids() => _sessions.Keys.ToArray();
}
