namespace FmcgDms.Demo;

// Child output is untrusted. Persist only a bounded set of exact startup milestones;
// never keep any raw stdout/stderr, arguments, paths, SQL or exception messages.
internal sealed class App03SqlObserverStartupWitness
{
    private const string Prefix = "APP03_SQL_OBSERVER_STARTUP_V1 ";
    private static readonly string[] Allowed =
    [
        "entered", "guarded", "parent-open", "pipe-open", "file-open",
        "connection-open", "ownership-verified", "visibility-verified", "sample-loop"
    ];
    private readonly object gate = new();
    private readonly List<string> stages = [];
    private bool stderrObserved;

    internal void Stdout(string? line)
    {
        if (line is null || !line.StartsWith(Prefix, StringComparison.Ordinal)) return;
        string stage = line[Prefix.Length..];
        if (!Allowed.Contains(stage, StringComparer.Ordinal)) return;
        lock (gate)
        {
            if (!stages.Contains(stage, StringComparer.Ordinal)) stages.Add(stage);
        }
    }

    internal void Stderr(string? line)
    {
        if (string.IsNullOrEmpty(line)) return;
        lock (gate) stderrObserved = true;
    }

    internal (string[] Stages, bool StderrObserved) Snapshot()
    {
        lock (gate) return (stages.ToArray(), stderrObserved);
    }
}
