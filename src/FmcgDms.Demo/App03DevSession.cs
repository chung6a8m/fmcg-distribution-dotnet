using System.Text.Json;
using FmcgDms.Api.Persistence;

namespace FmcgDms.Demo;

internal sealed record App03DevHost(HttpClient Client, string WebUrl, string SourceToken, int ApiPid, int VitePid);

internal sealed class App03DevSession
{
    private const string ManifestVersion = "APP-03-session-v1";
    private const string SchemaVersion = "C5+F01+F03+F04+F05/v1";
    private const string SeedVersion = "APP-03-C2-F05-v1";
    private readonly string folder;
    private readonly string manifestPath;
    private readonly string lockPath;
    private readonly string signalName;
    private readonly string source;
    private readonly Manifest expected;

    internal DemoRun RunId { get; }
    internal bool Resume { get; }
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
            App03A1Profile.Name, SchemaVersion, SeedVersion, DevF05Evidence.FixtureUtc, source, WebPort: 0, Ready: false);
    }

    private static string Home => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FmcgDms", "App03Dev");

    internal static bool HasManifest(string id) => File.Exists(Path.Combine(Home, new DemoRun(id).Id, "session.json"));

    internal static async Task Run(string operation, string root, string source, string? id = null,
        Func<App03DevHost, Task>? observe = null)
    {
        if (operation == "Verify") { await App03DevSessionEvidence.Run(root, source); return; }
        if (operation is not ("Start" or "Resume" or "Remove") || (id is null && operation != "Start"))
            throw new ArgumentException("Expected Start, Resume or Remove. Resume and Remove require a RunId.");
        var session = new App03DevSession(id ?? Guid.NewGuid().ToString("N"), source, operation == "Resume");

        using var sessionLock = session.Acquire();
        if (operation == "Remove")
        {
            var metadata = await session.Read();
            await session.RunId.Remove(app03Session: true, allowUnbound: !metadata.Ready);
            File.Delete(session.ClockPath);
            File.Delete(session.manifestPath + ".pending");
            File.Delete(session.manifestPath);
            if (!Directory.EnumerateFileSystemEntries(session.folder).Any())
                Directory.Delete(session.folder, recursive: false);
            Console.WriteLine($"APP-03 DEV removed {session.RunId.Id}.");
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
        foreach (string tenant in DemoRun.Tenants) await RunId.Verify(tenant, app03Session: true);
        if (await File.ReadAllTextAsync(ClockPath) != metadata.ClockUtc.ToString("O"))
            throw new InvalidOperationException("APP-03 session clock does not match its manifest.");
        WebPort = metadata.WebPort;
        Console.WriteLine($"APP-03 DEV resuming {RunId.Id} from {metadata.CreatedSource}, runtime {source}.");
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
            || loaded != expected with { CreatedSource = loaded.CreatedSource, Ready = loaded.Ready, WebPort = loaded.WebPort })
            throw new InvalidOperationException("APP-03 session manifest does not match its run, bindings, profile, schema, seed or clock.");
        return loaded;
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
