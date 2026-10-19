using System.Diagnostics;
namespace Loomi.BrowserAutomation;
/// <summary>Where a run's time went, phase by phase, for one log line at its end. Each phase is the time since the one before it; nothing the person
/// wrote is in it. Only the first time a phase is reached counts, so a loop passing the same point again does not move it.</summary>
public sealed class RunClock
{
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly List<(string Phase, double At)> marks = [];
    public void Mark(string phase) { if (!marks.Exists(m => m.Phase == phase)) marks.Add((phase, clock.Elapsed.TotalSeconds)); }
    public double Seconds => clock.Elapsed.TotalSeconds;
    public override string ToString()
    {
        var last = 0.0;
        var parts = marks.Select(m => { var span = m.At - last; last = m.At; return $"{m.Phase} {span:F1}s"; });
        return string.Join(", ", parts.Append($"total {clock.Elapsed.TotalSeconds:F1}s"));
    }
}
