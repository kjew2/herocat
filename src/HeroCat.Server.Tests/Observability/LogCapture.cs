using System.Text.Json;
using HeroCat.Server.Observability;

namespace HeroCat.Server.Tests.Observability;

/// <summary>Collects the structured log's finished lines instead of writing them to stdout.</summary>
sealed class LogCapture : ILogSink
{
    readonly List<string> _lines = new();

    public void WriteLine(string line)
    {
        lock (_lines) _lines.Add(line);
    }

    public IReadOnlyList<string> Lines
    {
        get { lock (_lines) return _lines.ToList(); }
    }

    public IEnumerable<JsonElement> Json => Lines.Select(l => JsonDocument.Parse(l).RootElement.Clone());

    public string All => string.Join("\n", Lines);
}
