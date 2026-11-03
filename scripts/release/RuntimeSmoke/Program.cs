using System.Net.Http.Json;
using System.Text.Json;
using Curs.Services.LocalSetup;
using KanbanContracts;

// This executable is deliberately restricted to a disposable GitHub-hosted Linux runner.
if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" || args.Length != 2)
    throw new InvalidOperationException("Run only on an isolated GitHub Actions Linux runner with metadata and archive paths.");
var runnerTemporary = Path.GetFullPath(Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? throw new InvalidOperationException("RUNNER_TEMP required."));
var folder = Path.Combine(runnerTemporary, "kanban-runtime-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
using var metadata = JsonDocument.Parse(File.ReadAllText(args[0]));
var data = metadata.RootElement;
var version = data.GetProperty("version").GetString()!;
var manifest = new RuntimeManifest
{
    Version = version,
    ApiImage = data.GetProperty("apiImage").GetString()!,
    ServerArchiveSha256 = data.GetProperty("sha256").GetString()!,
    ServerArchiveUrl = $"https://github.com/kuum-oss/Curs/releases/download/v{version}/{data.GetProperty("archive").GetString()}",
    SqlImage = "mcr.microsoft.com/mssql/server:2022-latest@sha256:4402d880dd4c34bfa7d8705e56a86cd6c88da80a1f6bbbe741f999e76264a090"
};
if (!manifest.ApiImage.EndsWith("-amd64")) throw new InvalidOperationException("The smoke runner requires the amd64 image.");
File.Copy(Path.GetFullPath(args[1]), Path.Combine(folder, "server-amd64.tar.gz"));
var platform = new CiPlatform();
var coordinator = new LocalSetupCoordinator(manifest, folder, platform);
string? project = null;
try
{
    var first = await coordinator.EnsureReadyAsync(new ConsoleProgress(), CancellationToken.None);
    using (var state = JsonDocument.Parse(File.ReadAllText(coordinator.InstallationFile)))
        project = "kanbanboard-desktop-" + state.RootElement.GetProperty("installationId").GetGuid().ToString("N")[..12];
    var installationBefore = File.ReadAllBytes(coordinator.InstallationFile);
    var environmentBefore = File.ReadAllBytes(Path.Combine(folder, ".env"));
    using var http = new HttpClient { BaseAddress = first.ApiUri };
    var register = await http.PostAsJsonAsync("api/auth/register", new { email = "release-smoke-" + Guid.NewGuid().ToString("N") + "@example.test", name = "Release smoke", password = "Test." + Guid.NewGuid().ToString("N") + "!" });
    register.EnsureSuccessStatusCode();
    using var auth = JsonDocument.Parse(await register.Content.ReadAsStringAsync());
    http.DefaultRequestHeaders.Authorization = new("Bearer", auth.RootElement.GetProperty("accessToken").GetString());
    var create = await http.PostAsJsonAsync("api/boards", new { name = "Збереження після повторного запуску", description = "Disposable CI only" });
    create.EnsureSuccessStatusCode();
    var board = (await create.Content.ReadFromJsonAsync<BoardDetailsDto>())!;
    var composePrefix = new[] { "compose", "--project-name", project, "--env-file", Path.Combine(folder, ".env"), "--file", Path.Combine(folder, "compose.yaml") };
    var stop = await platform.RunAsync(platform.FindDocker()!, composePrefix.Concat(new[] { "stop" }).ToArray(), folder, TimeSpan.FromMinutes(2), CancellationToken.None);
    if (stop.ExitCode != 0) throw new InvalidOperationException("Could not stop the isolated smoke stack.");
    var second = await coordinator.EnsureReadyAsync(new ConsoleProgress(), CancellationToken.None);
    if (first.ApiUri != second.ApiUri || !installationBefore.SequenceEqual(File.ReadAllBytes(coordinator.InstallationFile)) || !environmentBefore.SequenceEqual(File.ReadAllBytes(Path.Combine(folder, ".env"))))
        throw new InvalidOperationException("Restart changed persistent installation settings.");
    var recovered = await http.GetFromJsonAsync<BoardDetailsDto>($"api/boards/{board.Id}");
    if (recovered?.Name != board.Name) throw new InvalidOperationException("Restart did not preserve the board.");
    Console.WriteLine("Runtime smoke passed: verified archive → load → isolated compose → migration → register → board → stop/start → same settings and board.");
}
finally
{
    // The ownership identity comes from our own temporary state, never from a user's runtime folder.
    if (project is not null && folder.StartsWith(Path.Combine(runnerTemporary, "kanban-runtime-smoke-"), StringComparison.Ordinal))
    {
        var cleanup = await platform.RunAsync(platform.FindDocker()!, ["compose", "--project-name", project, "--env-file", Path.Combine(folder, ".env"), "--file", Path.Combine(folder, "compose.yaml"), "down", "--volumes"], folder, TimeSpan.FromMinutes(2), CancellationToken.None);
        if (cleanup.ExitCode != 0) Console.Error.WriteLine("Temporary smoke cleanup failed; the hosted runner will be discarded.");
    }
}

sealed class ConsoleProgress : IProgress<SetupProgress>
{
    public void Report(SetupProgress value) => Console.WriteLine(value.Title);
}
sealed class CiPlatform : ISetupPlatform
{
    private readonly SetupPlatform native = new();
    // Docker command execution is identical on the supported desktop hosts; no UI is invoked here.
    public string OS => "windows";
    public string Architecture => "amd64";
    public string? FindDocker() => native.FindDocker();
    public int GetAvailablePort() => native.GetAvailablePort();
    public Task<SetupCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken token) => native.RunAsync(executable, arguments, workingDirectory, timeout, token);
    public Task OpenDockerAsync(CancellationToken token) => throw new InvalidOperationException("The hosted runner must provide its own Docker daemon.");
    public Task OpenFileAsync(string path, CancellationToken token) => throw new InvalidOperationException("Installer UI must not run in the CI smoke harness.");
}
