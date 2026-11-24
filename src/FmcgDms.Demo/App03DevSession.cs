using System.Text.Json;
using FmcgDms.Api.Persistence;

namespace FmcgDms.Demo;

internal sealed record App03DevHost(HttpClient Client, string WebUrl, string SourceToken, int ApiPid, int VitePid,
    string PeerToken, string FinanceToken, string ExpiredToken, string ReceiverToken);

internal sealed class App03DevSession
{
    private const string ManifestVersion = "APP-03-session-v1";
    private const string SchemaVersion = "C5+F01+F03+F04+F05/v1";
    private const string LegacySeedVersion = "APP-03-C2-F05-v1";
    private readonly string folder;
    private readonly string manifestPath;
    private readonly string lockPath;
    private readonly string signalName;
    private readonly string source;
    private readonly Manifest expected;

    internal DemoRun RunId { get; }
    internal bool Resume { get; }
    internal bool UsesBusinessDay { get; private set; } = true;
    internal string ClockPath => Path.Combine(folder, "clock.utc");
    internal int WebPort { get; private set; }
    internal CancellationToken StopToken { get; private set; }

    private App03DevSession(string id, string source, bool resume)
    {
        this.source = source;
        Resume = resume;
        RunId = new DemoRun(id);
        string home = Directory.CreateDirectory(Home).FullName;
        folder = Path.Combine(home, id);
        manifestPath = Path.Combine(folder, "session.json");
        lockPath = Path.Combine(home, id + ".lock");
        signalName = $@"Local\FmcgDms.APP03.{id}";
        expected = new Manifest(ManifestVersion, id, DemoRun.Instance,
            new Binding("tenant-a", RunId.Name("tenant-a"), id), new Binding("tenant-b", RunId.Name("tenant-b"), id),
            App03A1Profile.Name, SchemaVersion, App03DayBootstrap.Version,
            App03DayBootstrap.Clock(App03DayBootstrap.FirstDay), source, WebPort: 0, Ready: false);
    }

    private static string Home => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FmcgDms", "App03Dev");

    internal static bool HasManifest(string id) => File.Exists(Path.Combine(Home, new DemoRun(id).Id, "session.json"));

    internal static async Task Run(string operation, string root, string source, string? id = null,
        Func<App03DevHost, Task>? observe = null, DateOnly? businessDate = null)
    {
        if (operation == "Verify") { await App03DevSessionEvidence.Run(root, source); return; }
        if (operation == "VerifyWorkspace") { await App03WorkspaceEvidence.Run(root, source); return; }
        if (operation == "VerifyHandoff") { await App03WorkspaceEvidence.Run(root, source, handoff: true); return; }
        if (operation is not ("Start" or "Resume" or "Remove" or "NextDay") || (id is null && operation != "Start")
            || (operation == "NextDay") != businessDate.HasValue)
            throw new ArgumentException("Expected Start, Resume, Remove or NextDay. NextDay requires the target business date.");
        var session = new App03DevSession(id ?? Guid.NewGuid().ToString("N"), source, operation == "Resume");

        using var sessionLock = session.Acquire();
        if (operation == "Remove")
        {
            var metadata = await session.Read();
            await session.RunId.Remove(app03Session: true, allowUnbound: !metadata.Ready);
            File.Delete(session.ClockPath);
            File.Delete(session.ClockPath + ".pending");
            File.Delete(session.manifestPath + ".pending");
            File.Delete(session.manifestPath);
            if (!Directory.EnumerateFileSystemEntries(session.folder).Any())
                Directory.Delete(session.folder, recursive: false);
            Console.WriteLine($"APP-03 DEV removed {session.RunId.Id}.");
            return;
        }
        if (operation == "NextDay")
        {
            await session.Advance(businessDate!.Value);
            return;
        }

        using var stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, session.signalName, out bool created);
        if (!created) throw new InvalidOperationException("APP-03 Stop signal is already in use.");
        using var stop = new CancellationTokenSource();
        using var finished = new ManualResetEvent(false);
        var watchStop = Task.Run(() =>
        {
            if (WaitHandle.WaitAny([stopEvent, finished]) == 0) stop.Cancel();
        });
        try
        {
            session.StopToken = stop.Token;
            if (operation == "Start") await session.Begin();
            else await session.Verify();
            await App02c1Dev.Run("Interactive", root, source, "App03A1", session, observe);
        }
        finally
        {
            finished.Set();
            await watchStop;
        }
    }

    internal Task MarkReady() => Write(expected with { WebPort = WebPort, Ready = true });

    private async Task Begin()
    {
        if (Directory.Exists(folder))
            throw new InvalidOperationException("APP-03 session directory already exists.");
        foreach (string tenant in DemoRun.Tenants)
            if (await RunId.Exists(tenant))
                throw new InvalidOperationException("An APP-03 session target database already exists.");
        Directory.CreateDirectory(folder);
        WebPort = App02c1Dev.FreePort();
        await Write(expected with { WebPort = WebPort });
        await File.WriteAllTextAsync(ClockPath, expected.ClockUtc.ToString("O"));
        Console.WriteLine($"APP-03 DEV RunId {RunId.Id}");
    }

    private async Task Verify()
    {
        var metadata = await Read();
        if (!metadata.Ready)
            throw new InvalidOperationException("APP-03 session is not ready. Remove the incomplete synthetic session before starting a new one.");
        await VerifyBindings();
        UsesBusinessDay = metadata.Seed == App03DayBootstrap.Version;
        if (!UsesBusinessDay)
        {
            if (await File.ReadAllTextAsync(ClockPath) != metadata.ClockUtc.ToString("O"))
                throw new InvalidOperationException("APP-03 session clock does not match its manifest.");
        }
        else
        {
            await PrepareDay(metadata.ClockUtc);
        }
        WebPort = metadata.WebPort;
        Console.WriteLine($"APP-03 DEV resuming {RunId.Id} from {metadata.CreatedSource}, runtime {source}.");
    }

    private async Task Advance(DateOnly target)
    {
        var metadata = await Read();
        if (!metadata.Ready || metadata.Seed != App03DayBootstrap.Version)
            throw new InvalidOperationException("NextDay requires a ready APP-03.2 bootstrap session.");
        var current = DateOnly.FromDateTime(metadata.ClockUtc);
        if (target < current || target > App03DayBootstrap.LastDay || target.DayNumber > current.DayNumber + 1)
            throw new InvalidOperationException("NextDay must name the current date (retry) or the next date in the bootstrap period.");
        await VerifyBindings();
        DateTime clock = App03DayBootstrap.Clock(target);

        // Publish the requested date before preparing either tenant. A crash can leave one tenant
        // prepared; NextDay and Resume both finish the same date before a host can serve it.
        if (target != current)
        {
            await PrepareDay(metadata.ClockUtc);
            await Write(metadata with { ClockUtc = clock });
        }
        await PrepareDay(clock);
        Console.WriteLine($"APP-03 DEV {RunId.Id} prepared {target:yyyy-MM-dd} at 09:00 ICT.");
    }

    private async Task VerifyBindings()
    {
        foreach (string tenant in DemoRun.Tenants) await RunId.Verify(tenant, app03Session: true);
    }

    private async Task PrepareDay(DateTime clock)
    {
        foreach (string tenant in DemoRun.Tenants)
            await App03DayBootstrap.PrepareDay(RunId, tenant, DateOnly.FromDateTime(clock), StopToken);
        string value = clock.ToString("O");
        if (!File.Exists(ClockPath) || await File.ReadAllTextAsync(ClockPath) != value)
        {
            await File.WriteAllTextAsync(ClockPath + ".pending", value);
            File.Move(ClockPath + ".pending", ClockPath, overwrite: true);
        }
    }

    private async Task<Manifest> Read()
    {
        if (!File.Exists(manifestPath))
            throw new InvalidOperationException("APP-03 session manifest does not exist.");
        if (File.GetAttributes(folder).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("APP-03 session directory cannot be a reparse point.");
        var loaded = JsonSerializer.Deserialize<Manifest>(await File.ReadAllTextAsync(manifestPath));
        if (loaded is null || string.IsNullOrWhiteSpace(loaded.CreatedSource)
            || loaded.WebPort is < 1024 or > 65535 or App02c1Dev.ApiPort
            || !ValidClock(loaded)
            || loaded != expected with { CreatedSource = loaded.CreatedSource, Ready = loaded.Ready,
                WebPort = loaded.WebPort, Seed = loaded.Seed, ClockUtc = loaded.ClockUtc })
            throw new InvalidOperationException("APP-03 session manifest does not match its run, bindings, profile, schema, seed or clock.");
        return loaded;
    }

    private static bool ValidClock(Manifest metadata)
    {
        if (metadata.ClockUtc.Kind != DateTimeKind.Utc) return false;
        if (metadata.Seed == LegacySeedVersion) return metadata.ClockUtc == DevF05Evidence.FixtureUtc;
        var date = DateOnly.FromDateTime(metadata.ClockUtc);
        return metadata.Seed == App03DayBootstrap.Version
            && date >= App03DayBootstrap.FirstDay && date <= App03DayBootstrap.LastDay
            && (metadata.Ready || date == App03DayBootstrap.FirstDay)
            && metadata.ClockUtc == App03DayBootstrap.Clock(date);
    }

    private async Task Write(Manifest value)
    {
        string pending = manifestPath + ".pending";
        await File.WriteAllTextAsync(pending, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(pending, manifestPath, overwrite: true);
    }

    private FileStream Acquire()
    {
        try { return OpenLock(); }
        catch (IOException ex) { throw new InvalidOperationException("APP-03 session is in use. Stop it before Resume or Remove.", ex); }
    }

    private FileStream OpenLock() => File.Open(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private sealed record Binding(string TenantId, string Database, string Nonce);
    private sealed record Manifest(string Format, string RunId, string Instance, Binding TenantA, Binding TenantB,
        string Profile, string Schema, string Seed, DateTime ClockUtc, string CreatedSource, int WebPort, bool Ready);
}
