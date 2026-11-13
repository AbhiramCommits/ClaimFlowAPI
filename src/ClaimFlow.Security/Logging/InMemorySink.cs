using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

namespace ClaimFlow.Security.Logging;

/// <summary>Serilog sink used by the PHI leak tests and available for diagnostics.</summary>
public sealed class InMemorySink : ILogEventSink
{
    private readonly ConcurrentQueue<LogEvent> _events = new();

    public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);

    public IReadOnlyList<LogEvent> Events => _events.ToArray();

    public IReadOnlyList<string> RenderedMessages =>
        Events.Select(e => e.RenderMessage()).ToArray();

    /// <summary>Rendered messages plus every property value, for substring leak assertions.</summary>
    public string AllText => string.Join(
        Environment.NewLine,
        Events.Select(e =>
            e.RenderMessage() + " " +
            string.Join(" ", e.Properties.Select(p => p.Key + "=" + p.Value))));

    public void Clear()
    {
        while (_events.TryDequeue(out _))
        {
        }
    }
}
