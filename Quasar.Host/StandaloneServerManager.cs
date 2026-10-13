using System.Diagnostics;
using System.IO.Compression;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text.Json;
using Quasar.Host.Contract.V1;

namespace Quasar.Host;

/// <summary>
/// Host-local, isolated standalone server workspace. Never uses AMP, Torch or existing
/// Dedicated Server directories. The Host, not the TX web worker, owns the process.
/// </summary>
internal sealed class StandaloneServerManager : IDisposable
{
    private readonly string _root;
    private readonly string _runtime;
    private readonly Dictionary<string, Process> _processes = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public StandaloneServerManager(string stateDirectory)
    {
        _root = Path.Combine(Path.GetFullPath(stateDirectory), "mayhem-standalone", "servers");
        _runtime = Path.Combine(Path.GetFullPath(stateDirectory), "mayhem-standalone", "runtime");
        Directory.CreateDirectory(_root);
    }

    private string Root(string id)
    {
        if (!StandaloneServerSpec.ValidId(id)) throw new ArgumentException("Invalid server ID.");
        return Path.Combine(_root, id);
    }

    private string Config(string id) => Path.Combine(Root(id), "server.json");

    private StandaloneServerSpec Read(string id) =>
        File.Exists(Config(id))
            ? JsonSerializer.Deserialize<StandaloneServerSpec>(File.ReadAllText(Config(id)), Json)
                ?? throw new InvalidDataException("Server configuration is empty.")
            : throw new InvalidOperationException("Unknown standalone server: " + id);

    public StandaloneServerStatus[] List() =>
        Directory.EnumerateDirectories(_root)
            .Where(dir => File.Exists(Path.Combine(dir, "server.json")))
            .Select(dir => Status(Path.GetFileName(dir)))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private bool TryAdoptExistingProcess(string id)
    {
        if (_processes.TryGetValue(id, out var known) && !known.HasExited) return true;
        string pidFile = Path.Combine(Root(id), "magnetar-config", "magnetar.pid");
        if (!File.Exists(pidFile)) return false;
        var lines = File.ReadAllLines(pidFile);
        if (lines.Length < 2 || !int.TryParse(lines[0].Trim(), out int pid))
            throw new InvalidOperationException("Invalid Magnetar PID file; refusing to launch a duplicate.");
        if (!Path.GetFullPath(lines[1].Trim()).Equals(Path.Combine(Root(id), "data"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Magnetar PID file belongs to another instance.");
        try
        {
            var process = Process.GetProcessById(pid);
            string? actual = process.MainModule?.FileName;
            string? expected = FindMagnetar();
            if (actual is null || expected is null ||
                !Path.GetFullPath(actual).Equals(Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
            {
                process.Dispose();
                throw new InvalidOperationException("Magnetar PID could not be validated. Refusing to launch duplicate.");
            }
            _processes[id] = process;
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false; // PID file outlived the exited Magnetar process.
        }
    }

    public StandaloneServerStatus Status(string id)
    {
        var spec = Read(id);
        var serverRoot = Root(id);
        var world = Path.Combine(serverRoot, "data", "Saves", spec.WorldName);
        bool provisioned = File.Exists(FindMagnetar() ?? "") &&
                           Directory.Exists(Path.Combine(_runtime, "DedicatedServer", "DedicatedServer64"));
        bool worldReady = Directory.Exists(world) &&
                          File.Exists(Path.Combine(serverRoot, "data", "SpaceEngineers-Dedicated.cfg"));
        bool running = TryAdoptExistingProcess(id);
        _processes.TryGetValue(id, out var process);
        return new(spec.ServerId, spec.Name, spec.WorldName, spec.Port, running,
            provisioned, worldReady, spec.AutoStart, running ? process!.Id : null,
            running ? "Running on this Host" : !provisioned ? "Runtime not provisioned" :
            !worldReady ? "Copy a test world and Dedicated Server config into the isolated instance folder" : "Stopped");
    }

    public async Task<StandaloneServerStatus> CreateAsync(StandaloneServerSpec spec, CancellationToken cancellationToken)
    {
        StandaloneServerSpec.Validate(spec);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(Config(spec.ServerId))) throw new InvalidOperationException("This server ID already exists.");
            int[] requestedPorts = [spec.Port, spec.Port + 1000, spec.Port + 2000];
            if (List().Any(s => new[] { s.Port, s.Port + 1000, s.Port + 2000 }
                .Intersect(requestedPorts).Any()))
                throw new InvalidOperationException("The requested game, Steam or Remote API ports overlap another managed server on this Host.");
            var occupied = IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners().Select(ep => ep.Port)
                .Concat(IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(ep => ep.Port)).ToHashSet();
            if (requestedPorts.Any(occupied.Contains))
                throw new InvalidOperationException("The requested game, Steam or Remote API port is in use on this Host.");
            // Prevent creating a server that starts automatically before its world is prepared.
            spec = spec with { AutoStart = false };
            Directory.CreateDirectory(Path.Combine(Root(spec.ServerId), "data", "Saves"));
            Directory.CreateDirectory(Path.Combine(Root(spec.ServerId), "magnetar-config"));
            await File.WriteAllTextAsync(Config(spec.ServerId), JsonSerializer.Serialize(spec, Json), cancellationToken);
            return Status(spec.ServerId);
        }
        finally { _gate.Release(); }
    }

    public async Task<StandaloneServerStatus> ProvisionAsync(string id, CancellationToken cancellationToken)
    {
        _ = Read(id);
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Remote standalone provisioning currently requires Windows.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_runtime);
            string steamcmdDir = Path.Combine(_runtime, "SteamCMD");
            string steamcmdExe = Path.Combine(steamcmdDir, "steamcmd.exe");
            if (!File.Exists(steamcmdExe))
            {
                Directory.CreateDirectory(steamcmdDir);
                string zip = Path.Combine(_runtime, "steamcmd.zip");
                await DownloadAsync("https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip", zip, cancellationToken);
                ZipFile.ExtractToDirectory(zip, steamcmdDir, overwriteFiles: true);
                File.Delete(zip);
            }

            string dsDir = Path.Combine(_runtime, "DedicatedServer");
            Directory.CreateDirectory(dsDir);
            await RunToolAsync(steamcmdExe,
                ["+force_install_dir", dsDir, "+login", "anonymous", "+app_update", "298740", "validate", "+quit"],
                steamcmdDir, cancellationToken, TimeSpan.FromHours(2));

            string magnetarDir = Path.Combine(_runtime, "Magnetar");
            if (FindMagnetar() is null)
            {
                Directory.CreateDirectory(magnetarDir);
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Mayhem-Quasar-Host/1.0");
                using var release = JsonDocument.Parse(await http.GetStringAsync(
                    "https://api.github.com/repos/CometWorks/magnetar/releases/latest", cancellationToken));
                var asset = release.RootElement.GetProperty("assets").EnumerateArray()
                    .FirstOrDefault(item => item.GetProperty("name").GetString() is { } name &&
                        name.StartsWith("MagnetarForWindows-", StringComparison.OrdinalIgnoreCase) &&
                        name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase));
                if (asset.ValueKind == JsonValueKind.Undefined)
                    throw new InvalidOperationException("The latest Magnetar release has no Windows .7z asset.");
                string archive = Path.Combine(_runtime, "magnetar.7z");
                await DownloadAsync(asset.GetProperty("browser_download_url").GetString()!, archive, cancellationToken);
                if (asset.TryGetProperty("digest", out var digest) &&
                    digest.ValueKind == JsonValueKind.String &&
                    digest.GetString() is { } expected && expected.StartsWith("sha256:", StringComparison.Ordinal))
                {
                    await using var input = File.OpenRead(archive);
                    string actual = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken)).ToLowerInvariant();
                    if (actual != expected["sha256:".Length..].ToLowerInvariant())
                        throw new InvalidDataException("Magnetar release SHA256 mismatch.");
                }
                // Windows 10/Server's bsdtar supports 7z. We refuse to execute unverified
                // arbitrary extractors; if tar lacks 7z support the operator sees the error.
                await RunToolAsync("tar.exe", ["-xf", archive, "-C", magnetarDir],
                    magnetarDir, cancellationToken, TimeSpan.FromMinutes(15));
                File.Delete(archive);
                if (FindMagnetar() is null)
                    throw new InvalidDataException("Magnetar archive did not contain MagnetarInterim.exe.");
            }
            return Status(id);
        }
        finally { _gate.Release(); }
    }

    private static async Task DownloadAsync(string url, string destination, CancellationToken token)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromHours(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mayhem-Quasar-Host/1.0");
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var output = File.Create(destination + ".partial");
        await response.Content.CopyToAsync(output, token);
        await output.FlushAsync(token);
        File.Move(destination + ".partial", destination, true);
    }

    private string? FindMagnetar()
    {
        string dir = Path.Combine(_runtime, "Magnetar");
        return Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "MagnetarInterim.exe", SearchOption.AllDirectories).FirstOrDefault()
            : null;
    }

    private static async Task RunToolAsync(string file, IEnumerable<string> args, string workDir,
        CancellationToken cancellationToken, TimeSpan limit)
    {
        var start = new ProcessStartInfo(file)
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Failed to start " + file);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(limit);
        Task<string> stdout = ReadTailAsync(process.StandardOutput);
        Task<string> stderr = ReadTailAsync(process.StandardError);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
        string error = await stderr;
        _ = await stdout;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(Path.GetFileName(file) + " failed (exit " + process.ExitCode + "): " + error);
    }

    private static async Task<string> ReadTailAsync(StreamReader reader)
    {
        char[] buffer = new char[8192];
        string tail = "";
        int n;
        while ((n = await reader.ReadAsync(buffer)) != 0)
        {
            tail += new string(buffer, 0, n);
            if (tail.Length > 12000) tail = tail[^12000..];
        }
        return tail;
    }

    public async Task<StandaloneServerStatus> PrepareWorldAsync(string id, CancellationToken cancellationToken)
    {
        var spec = Read(id);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            string serverRoot = Root(id);
            string data = Path.Combine(serverRoot, "data");
            string saves = Path.Combine(data, "Saves");
            string world = Path.Combine(saves, spec.WorldName);
            if (Directory.Exists(world))
                throw new InvalidOperationException("World folder already exists; do not overwrite an existing save.");
            string contentRoot = Path.Combine(_runtime, "DedicatedServer", "Content", "CustomWorlds");
            if (!Directory.Exists(contentRoot))
                throw new DirectoryNotFoundException("Install Dedicated Server runtime before creating a world.");
            var template = Directory.EnumerateDirectories(contentRoot, "*", SearchOption.AllDirectories)
                .Where(dir => File.Exists(Path.Combine(dir, "Sandbox.sbc")) &&
                    File.Exists(Path.Combine(dir, "Sandbox_config.sbc")))
                .OrderByDescending(dir => Path.GetFileName(dir).Contains("Star", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
            if (template is null)
                throw new InvalidOperationException("No usable stock world with Sandbox.sbc and Sandbox_config.sbc was found.");
            string staging = Path.Combine(saves, ".creating-" + spec.ServerId);
            Directory.CreateDirectory(saves);
            if (Directory.Exists(staging)) throw new InvalidOperationException("An interrupted world creation exists.");
            try
            {
                CopyTree(template, staging);
                var configFile = Path.Combine(staging, "Sandbox_config.sbc");
                var worldConfig = System.Xml.Linq.XDocument.Load(configFile);
                var sessionName = worldConfig.Descendants().FirstOrDefault(x => x.Name.LocalName == "SessionName");
                if (sessionName is not null) sessionName.Value = spec.WorldName;
                worldConfig.Save(configFile);
                Directory.Move(staging, world);
            }
            catch
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
                throw;
            }

            // Follow upstream Quasar's dedicated configuration + last-session structure.
            System.Xml.Linq.XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
            System.Xml.Linq.XNamespace xsd = "http://www.w3.org/2001/XMLSchema";
            var cfg = new System.Xml.Linq.XDocument(
                new System.Xml.Linq.XDeclaration("1.0", "utf-8", null),
                new System.Xml.Linq.XElement("MyConfigDedicated",
                    new System.Xml.Linq.XAttribute(System.Xml.Linq.XNamespace.Xmlns + "xsi", xsi),
                    new System.Xml.Linq.XAttribute(System.Xml.Linq.XNamespace.Xmlns + "xsd", xsd),
                    new System.Xml.Linq.XElement("SessionSettings",
                        new System.Xml.Linq.XElement("OnlineMode", "PUBLIC"),
                        new System.Xml.Linq.XElement("MaxPlayers", "8"),
                        new System.Xml.Linq.XElement("GameMode", "Survival")),
                    new System.Xml.Linq.XElement("IgnoreLastSession", "false"),
                    new System.Xml.Linq.XElement("ServerPort", spec.Port),
                    new System.Xml.Linq.XElement("SteamPort", spec.Port + 1000),
                    new System.Xml.Linq.XElement("RemoteApiPort", spec.Port + 2000),
                    new System.Xml.Linq.XElement("IP", "0.0.0.0"),
                    new System.Xml.Linq.XElement("ServerName", spec.Name),
                    new System.Xml.Linq.XElement("WorldName", spec.WorldName)));
            cfg.Save(Path.Combine(data, "SpaceEngineers-Dedicated.cfg"));
            var last = new System.Xml.Linq.XDocument(
                new System.Xml.Linq.XDeclaration("1.0", "utf-8", null),
                new System.Xml.Linq.XElement("MyObjectBuilder_LastSession",
                    new System.Xml.Linq.XAttribute(System.Xml.Linq.XNamespace.Xmlns + "xsi", xsi),
                    new System.Xml.Linq.XAttribute(System.Xml.Linq.XNamespace.Xmlns + "xsd", xsd),
                    new System.Xml.Linq.XElement("Path", world),
                    new System.Xml.Linq.XElement("IsContentWorlds", "false"),
                    new System.Xml.Linq.XElement("IsOnline", "false"),
                    new System.Xml.Linq.XElement("IsLobby", "false"),
                    new System.Xml.Linq.XElement("GameName", spec.WorldName),
                    new System.Xml.Linq.XElement("RelativePath", spec.WorldName)));
            last.Save(Path.Combine(saves, "LastSession.sbl"));
            return Status(id);
        }
        finally { _gate.Release(); }
    }

    private static void CopyTree(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyTree(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }

    public async Task<StandaloneServerStatus> StartAsync(string id, CancellationToken cancellationToken)
    {
        var spec = Read(id);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (TryAdoptExistingProcess(id)) return Status(id);
            var status = Status(id);
            if (!status.Provisioned || !status.WorldReady)
                throw new InvalidOperationException("Provision Magnetar/DS and prepare the isolated test world/config before starting.");
            int[] requiredPorts = [spec.Port, spec.Port + 1000, spec.Port + 2000];
            var used = IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners().Select(ep => ep.Port)
                .Concat(IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(ep => ep.Port)).ToHashSet();
            if (requiredPorts.Any(used.Contains))
                throw new InvalidOperationException("A game, Steam or Remote API port is occupied on the selected Host.");
            string root = Root(id);
            string executable = FindMagnetar()!;
            string ds64 = Path.Combine(_runtime, "DedicatedServer", "DedicatedServer64");
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
            };
            foreach (string arg in new[] {
                "-noconsole", "-consent", "deny",
                "-path", Path.Combine(root, "data"),
                "-config", Path.Combine(root, "magnetar-config"),
                "-ds64", ds64,
                "-session:" + Path.Combine(root, "data", "Saves", spec.WorldName)
            }) start.ArgumentList.Add(arg);
            // No shell expansion; no user-supplied executable or generic command arguments.
            var process = Process.Start(start) ?? throw new IOException("Magnetar start returned no process.");
            _processes[id] = process;
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            return Status(id);
        }
        finally { _gate.Release(); }
    }

    public async Task<StandaloneServerStatus> StopAsync(string id, CancellationToken cancellationToken)
    {
        _ = Read(id);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_processes.TryGetValue(id, out var process) && !process.HasExited)
            {
                // The initial test implementation avoids forcibly killing unknown
                // or adopted processes. Magnetar stop/save integration is required
                // before exposing Stop in production.
                throw new InvalidOperationException("Safe in-game save/shutdown command is not integrated yet; stop this test instance using the in-game admin command.");
            }
            return Status(id);
        }
        finally { _gate.Release(); }
    }

    public StandaloneServerLog Logs(string id)
    {
        _ = Read(id);
        string folder = Path.Combine(Root(id), "magnetar-config");
        string? latest = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.log", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
        if (latest is null) return new(id, "No Magnetar log yet.");
        using var file = new FileStream(latest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        file.Seek(-Math.Min(file.Length, 32768), SeekOrigin.End);
        using var reader = new StreamReader(file);
        return new(id, reader.ReadToEnd());
    }

    public void Dispose()
    {
        // Do not kill child game servers when the Host restarts.
        foreach (var process in _processes.Values) process.Dispose();
        _gate.Dispose();
    }
}
