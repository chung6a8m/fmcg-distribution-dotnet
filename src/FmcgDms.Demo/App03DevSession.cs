using System.Text.Json;
using FmcgDms.Api.Persistence;

namespace FmcgDms.Demo;

internal sealed record App03DevHost(HttpClient Client, string WebUrl, string SourceToken, int ApiPid, int VitePid,
    string PeerToken, string FinanceToken, string ExpiredToken, string ReceiverToken, string CashierToken, string QaToken, IReadOnlyDictionary<string, string>? FinanceCredentials = null,
    IReadOnlyDictionary<string, string>? CloseCredentials = null);

internal sealed class App03DevSession
{
    private const string ManifestVersion = "APP-03-session-v1";
    private const string SchemaVersion = "C5+F01+F03+F04+F05/v1";
    private const string FieldSchemaVersion = "C5+F01+F03+F04+F05+D1+D2+Field/v1";
    private const string SettlementSchemaVersion = "C5+F01-F05+F07+D1-D3+Field+Settlement/v1";
    private const string FinanceSchemaVersion = "APP-03.6+D4+F06+F08/v1";
    private const string CloseReadinessSchemaVersion = "APP-03.7+APP-05.1/v1";
    private const string SuccessorSchemaVersion = "APP-05.2+APP-05.3/v1";
    private const string TripClosureSchemaVersion = "APP-05.1+APP-05.2/v1";
    internal static readonly DateOnly FinanceRollover = new(2026, 12, 1);
    private const string LegacySeedVersion = "APP-03-C2-F05-v1";
    private readonly string folder;
    private readonly string manifestPath;
    private readonly string lockPath;
    private readonly string signalName;
    private readonly string source;
    private readonly Manifest expected;

    internal DemoRun RunId { get; }
    internal bool Resume { get; }
    internal bool VerifyHandoff { get; private init; }
    internal bool VerifyField { get; private init; }
    internal bool SplitSuccessorLots { get; private init; }
    internal bool UsesBusinessDay { get; private set; } = true;
    internal bool UsesField { get; private set; }
    internal bool UsesSettlement { get; private set; }
    internal bool UsesFinance { get; private set; }
    internal bool UsesCloseReadiness { get; private set; }
    internal bool UsesTripClosure { get; private set; }
    internal bool UsesSuccessor { get; private set; }
    internal string Profile => UsesSuccessor ? App03A1Profile.SuccessorName
        : UsesTripClosure ? App03A1Profile.TripClosureName
        : UsesCloseReadiness ? App03A1Profile.CloseReadinessName
        : UsesFinance ? App03A1Profile.FinanceName : UsesSettlement ? App03A1Profile.SettlementName
        : UsesField ? App03A1Profile.FieldName : App03A1Profile.Name;
    private string Schema => UsesSuccessor ? SuccessorSchemaVersion
        : UsesTripClosure ? TripClosureSchemaVersion
        : UsesCloseReadiness ? CloseReadinessSchemaVersion
        : UsesFinance ? FinanceSchemaVersion : UsesSettlement ? SettlementSchemaVersion
        : UsesField ? FieldSchemaVersion : SchemaVersion;
    internal string ClockPath => Path.Combine(folder, "clock.utc");
    internal int WebPort { get; private set; }
    internal CancellationToken StopToken { get; private set; }

    private App03DevSession(string id, string source, bool resume, bool startBeforeField,
        bool settlement, bool finance, bool closeReadiness, bool tripClosure, bool successor)
    {
        this.source = source;
        Resume = resume;
        UsesField = !startBeforeField;
        UsesSuccessor = successor;
        UsesTripClosure = tripClosure || successor;
        UsesCloseReadiness = closeReadiness || UsesTripClosure;
        if (startBeforeField && UsesCloseReadiness)
            throw new ArgumentException("APP-05 requires the APP-03 finance predecessor.");
        UsesSettlement = UsesField && (settlement || UsesCloseReadiness);
        UsesFinance = UsesSettlement && (finance || UsesCloseReadiness);
        RunId = new DemoRun(id);
        string home = Directory.CreateDirectory(Home).FullName;
        folder = Path.Combine(home, id);
        manifestPath = Path.Combine(folder, "session.json");
        lockPath = Path.Combine(home, id + ".lock");
        signalName = $@"Local\FmcgDms.APP03.{id}";
        expected = new Manifest(ManifestVersion, id, DemoRun.Instance,
            new Binding("tenant-a", RunId.Name("tenant-a"), id), new Binding("tenant-b", RunId.Name("tenant-b"), id),
            Profile, Schema, App03DayBootstrap.Version,
            App03DayBootstrap.Clock(App03DayBootstrap.FirstDay), source, WebPort: 0, Ready: false);
    }

    private static string Home => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FmcgDms", "App03Dev");

    internal static bool HasManifest(string id) => File.Exists(Path.Combine(Home, new DemoRun(id).Id, "session.json"));

    internal static async Task Run(string operation, string root, string source, string? id = null,
        Func<App03DevHost, Task>? observe = null, DateOnly? businessDate = null, bool verifyHandoff = false,
        bool verifyField = false, bool startBeforeField = false, bool settlement = false, bool finance = false,
        bool closeReadiness = false, bool tripClosure = false, bool successor = false, bool splitSuccessorLots = false)
    {
        if (operation == "Verify") { await App03DevSessionEvidence.Run(root, source); return; }
        if (operation == "VerifyWorkspace") { await App03WorkspaceEvidence.Run(root, source); return; }
        if (operation == "VerifyAcceptance")
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("APP-03 requires Windows LocalDB.");
            await App03AcceptanceEvidence.Run(root, source);
            return;
        }
        if (operation == "VerifyCloseReadiness")
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("APP-05.1 requires Windows LocalDB.");
            await App05ReadinessEvidence.Run(root, source);
            return;
        }
        if (operation == "VerifyCloseReadinessUpgrade")
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("APP-05.1 requires Windows LocalDB.");
            await App05UpgradeEvidence.Run(root, source);
            return;
        }
        if (operation == "VerifyDailyClose")
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("APP-05.7 requires Windows LocalDB.");
            await App05DailyCloseEvidence.Run(root, source);
            return;
        }
        if (operation is "VerifyTripClosure" or "VerifyASuccessor")
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("APP-05.2 requires Windows LocalDB.");
            await App05TripClosureEvidence.Run(root, source, successor: operation == "VerifyASuccessor");
            return;
        }
        if (operation == "VerifyFinance") { await App03FinanceEvidence.Run(root, source); return; }
        if (operation == "VerifySettlement") { await App03WorkspaceEvidence.Run(root, source, settlement: true); return; }
        if (operation == "VerifyField") { await App03WorkspaceEvidence.Run(root, source, field: true); return; }
        if (operation == "VerifyHandoff") { await App03WorkspaceEvidence.Run(root, source, handoff: true); return; }
        if (operation is not ("Start" or "Resume" or "Remove" or "NextDay") || (id is null && operation != "Start")
            || (operation == "NextDay") != businessDate.HasValue)
            throw new ArgumentException("Expected Start, Resume, Remove or NextDay. NextDay requires the target business date.");
        var session = new App03DevSession(id ?? Guid.NewGuid().ToString("N"), source, operation == "Resume",
            startBeforeField, settlement, finance, closeReadiness, tripClosure, successor)
        {
            VerifyHandoff = verifyHandoff,
            VerifyField = verifyField,
            SplitSuccessorLots = splitSuccessorLots
        };

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
            if (UsesCloseReadiness)
                throw new InvalidOperationException("APP-05 requires the APP-03 business day seed.");
            UsesField = false;
            UsesSettlement = false;
            UsesFinance = false;
            if (await File.ReadAllTextAsync(ClockPath) != metadata.ClockUtc.ToString("O"))
                throw new InvalidOperationException("APP-03 session clock does not match its manifest.");
        }
        else
        {
            UseManifestProfile(metadata);
            // Persist the upgrade target before either owned tenant changes. If it is
            // interrupted, a plain Resume continues the same exact migration chain.
            if (UsesCloseReadiness) await PublishFieldUpgrade(metadata);
            await PrepareFieldDay(metadata.ClockUtc);
            if (!UsesCloseReadiness) await PublishFieldUpgrade(metadata);
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
        UseManifestProfile(metadata);
        var last = UsesFinance ? FinanceRollover : App03DayBootstrap.LastDay;
        if (target < current || target > last || target.DayNumber > current.DayNumber + 1)
            throw new InvalidOperationException("NextDay must name the current date (retry) or the next date in the bootstrap period.");
        await VerifyBindings();
        if (UsesCloseReadiness) await PublishFieldUpgrade(metadata);
        DateTime clock = App03DayBootstrap.Clock(target);

        // Publish the requested date before preparing either tenant. A crash can leave one tenant
        // prepared; NextDay and Resume both finish the same date before a host can serve it.
        if (target != current)
        {
            await PrepareFieldDay(metadata.ClockUtc);
            await Write(WithFieldProfile(metadata) with { ClockUtc = clock });
        }
        await PrepareFieldDay(clock);
        if (target == current) await PublishFieldUpgrade(metadata);
        Console.WriteLine($"APP-03 DEV {RunId.Id} prepared {target:yyyy-MM-dd} at 09:00 ICT.");
    }

    private async Task VerifyBindings()
    {
        foreach (string tenant in DemoRun.Tenants) await RunId.Verify(tenant, app03Session: true);
    }

    private void UseManifestProfile(Manifest value)
    {
        UsesSuccessor |= value.Profile == App03A1Profile.SuccessorName;
        UsesTripClosure |= UsesSuccessor || value.Profile == App03A1Profile.TripClosureName;
        UsesCloseReadiness |= UsesTripClosure || value.Profile == App03A1Profile.CloseReadinessName;
        UsesFinance |= UsesCloseReadiness || value.Profile == App03A1Profile.FinanceName;
        UsesSettlement |= UsesFinance || value.Profile == App03A1Profile.SettlementName;
        UsesField |= UsesSettlement || value.Profile == App03A1Profile.FieldName;
    }

    private Manifest WithFieldProfile(Manifest value) => value with
    {
        Profile = UsesField ? Profile : App03A1Profile.FieldName,
        Schema = UsesField ? Schema : FieldSchemaVersion
    };

    private Task PublishFieldUpgrade(Manifest value) =>
        value == WithFieldProfile(value) ? Task.CompletedTask : Write(WithFieldProfile(value));

    private async Task PrepareFieldDay(DateTime clock)
    {
        foreach (string tenant in DemoRun.Tenants)
        {
            var day = DateOnly.FromDateTime(clock);
            ScratchMigrator.MigrateApp03A1(RunId.Connection(tenant), tenant, RunId.Id, LogLevel.Warning, field: true,
                settlement: UsesSettlement, finance: UsesFinance, closeReadiness: UsesCloseReadiness, tripClosure: UsesTripClosure, successor: UsesSuccessor);
            if (day <= App03DayBootstrap.LastDay)
            {
                await App03DayBootstrap.PrepareDay(RunId, tenant, day, StopToken, secondTrip: UsesSuccessor);
                await App03FieldBootstrap.Prepare(RunId, tenant, day, StopToken);
            }
            if (UsesSettlement) await App03SettlementBootstrap.Prepare(RunId, tenant, StopToken);
            if (UsesFinance) await App03FinanceBootstrap.Prepare(RunId, tenant, day, StopToken);
            if (UsesCloseReadiness) await App05CloseBootstrap.Prepare(RunId, tenant, StopToken);
        }
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
            || !ValidProfile(loaded)
            || loaded != expected with { CreatedSource = loaded.CreatedSource, Ready = loaded.Ready,
                WebPort = loaded.WebPort, Seed = loaded.Seed, ClockUtc = loaded.ClockUtc,
                Profile = loaded.Profile, Schema = loaded.Schema })
            throw new InvalidOperationException("APP-03 session manifest does not match its run, bindings, profile, schema, seed or clock.");
        return loaded;
    }

    private static bool ValidProfile(Manifest value) =>
        value is { Profile: App03A1Profile.Name, Schema: SchemaVersion }
        or { Profile: App03A1Profile.FieldName, Schema: FieldSchemaVersion, Seed: App03DayBootstrap.Version }
        or { Profile: App03A1Profile.SettlementName, Schema: SettlementSchemaVersion, Seed: App03DayBootstrap.Version }
        or { Profile: App03A1Profile.FinanceName, Schema: FinanceSchemaVersion, Seed: App03DayBootstrap.Version }
        or { Profile: App03A1Profile.CloseReadinessName, Schema: CloseReadinessSchemaVersion, Seed: App03DayBootstrap.Version }
        or { Profile: App03A1Profile.TripClosureName, Schema: TripClosureSchemaVersion, Seed: App03DayBootstrap.Version }
        or { Profile: App03A1Profile.SuccessorName, Schema: SuccessorSchemaVersion, Seed: App03DayBootstrap.Version };

    private static bool ValidClock(Manifest metadata)
    {
        if (metadata.ClockUtc.Kind != DateTimeKind.Utc) return false;
        if (metadata.Seed == LegacySeedVersion) return metadata.ClockUtc == DevF05Evidence.FixtureUtc;
        var date = DateOnly.FromDateTime(metadata.ClockUtc);
        return metadata.Seed == App03DayBootstrap.Version
            && date >= App03DayBootstrap.FirstDay
            && date <= (metadata.Profile is App03A1Profile.FinanceName or App03A1Profile.CloseReadinessName or App03A1Profile.TripClosureName or App03A1Profile.SuccessorName
                ? FinanceRollover : App03DayBootstrap.LastDay)
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
