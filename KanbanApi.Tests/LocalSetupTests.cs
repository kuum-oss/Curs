using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Curs.Services.LocalSetup;
using Xunit;

namespace KanbanApi.Tests;

public sealed class LocalSetupTests
{
    private static readonly byte[] Archive = Encoding.UTF8.GetBytes("local setup fixture image archive");
    private static RuntimeManifest ValidManifest() => new()
    {
        Version = "0.2.0",
        ApiImage = "kanbanboard-api:0.2.0-amd64",
        ServerArchiveUrl = "https://github.com/kuum-oss/Curs/releases/download/v0.2.0/kanbanboard-server-linux-amd64.tar.gz",
        ServerArchiveSha256 = Convert.ToHexString(SHA256.HashData(Archive)).ToLowerInvariant(),
        SqlImage = "mcr.microsoft.com/mssql/server:2022-latest@sha256:4402d880dd4c34bfa7d8705e56a86cd6c88da80a1f6bbbe741f999e76264a090"
    };

    private sealed class DirectoryFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "kanban-setup-test-" + Guid.NewGuid().ToString("N"));
        public DirectoryFixture() => Directory.CreateDirectory(Root);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    [Fact]
    public void DevelopmentAppWithoutPackagedManifestDoesNotEnableProvisioning()
    {
        using var directory = new DirectoryFixture();
        Assert.Null(RuntimeManifest.Load(directory.Root));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Root));
    }

    [Fact]
    public void PackagedManifestLoadsWithTheExactReleaseIdentity()
    {
        using var directory = new DirectoryFixture();
        Directory.CreateDirectory(Path.Combine(directory.Root, "setup"));
        File.WriteAllText(Path.Combine(directory.Root, "setup", "runtime.json"),
            JsonSerializer.Serialize(ValidManifest(), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var manifest = RuntimeManifest.Load(directory.Root);
        Assert.NotNull(manifest);
        Assert.Equal("0.2.0", manifest.Version);
        Assert.Equal(ValidManifest().ApiImage, manifest.ApiImage);
        Assert.Equal(ValidManifest().ServerArchiveSha256, manifest.ServerArchiveSha256);
    }

    [Theory]
    [InlineData("http://github.com/kuum-oss/Curs/releases/download/v0.2.0/kanbanboard-server-linux-amd64.tar.gz")]
    [InlineData("https://attacker.example/kanbanboard-server-linux-amd64.tar.gz")]
    [InlineData("file:///tmp/kanbanboard-server-linux-amd64.tar.gz")]
    public void UntrustedArchiveLocationsAreRejectedBeforeAnyCommandRuns(string address)
    {
        var manifest = ValidManifest() with { ServerArchiveUrl = address };
        Assert.Throws<SetupException>(() => manifest.Validate());
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("000000000000000000000000000000000000000000000000000000000000000g")]
    public void ReleaseArchiveRequiresACompleteSha256(string hash)
    {
        var manifest = ValidManifest() with { ServerArchiveSha256 = hash };
        Assert.Throws<SetupException>(() => manifest.Validate());
    }

    [Theory]
    [InlineData("kanbanboard-api:0.2.0-amd64 --privileged")]
    [InlineData("kanbanboard-api:0.2.0-amd64\nother")]
    public void ImageNamesCannotSmuggleCommandArguments(string image)
    {
        var manifest = ValidManifest() with { ApiImage = image };
        Assert.Throws<SetupException>(() => manifest.Validate());
    }

    [Theory]
    [InlineData("Version")]
    [InlineData("ApiImage")]
    [InlineData("ServerArchiveUrl")]
    [InlineData("ServerArchiveSha256")]
    [InlineData("SqlImage")]
    public void ExplicitNullManifestFieldsProduceASetupError(string field)
    {
        var manifest = ValidManifest();
        manifest = field switch
        {
            "Version" => manifest with { Version = null! },
            "ApiImage" => manifest with { ApiImage = null! },
            "ServerArchiveUrl" => manifest with { ServerArchiveUrl = null! },
            "ServerArchiveSha256" => manifest with { ServerArchiveSha256 = null! },
            _ => manifest with { SqlImage = null! }
        };
        Assert.Throws<SetupException>(() => manifest.Validate());
    }

    [Fact]
    public void BrokenManifestReturnsAnActionableErrorWithoutChangingTheFile()
    {
        using var directory = new DirectoryFixture();
        var setup = Path.Combine(directory.Root, "setup");
        Directory.CreateDirectory(setup);
        var path = Path.Combine(setup, "runtime.json");
        File.WriteAllText(path, "{ truncated manifest");
        var error = Assert.Throws<SetupException>(() => RuntimeManifest.Load(directory.Root));
        Assert.Equal(SetupAction.None, error.Action);
        Assert.Equal("{ truncated manifest", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("http://127.0.0.1/image.tar.gz")]
    [InlineData("https://untrusted.example/image.tar.gz")]
    [InlineData("file:///tmp/image.tar.gz")]
    public async Task DownloaderRejectsUntrustedOriginsBeforeCreatingAFile(string source)
    {
        using var directory = new DirectoryFixture();
        var destination = Path.Combine(directory.Root, "image.tar.gz");
        var error = await Assert.ThrowsAsync<SetupException>(() =>
            new SetupDownload().DownloadAsync(new Uri(source), destination, null, CancellationToken.None));
        Assert.Equal(SetupAction.None, error.Action);
        Assert.False(File.Exists(destination));
    }

    [Theory]
    [InlineData("https://127.0.0.1/")]
    [InlineData("http://example.test/")]
    public async Task ReadinessProbeNeverContactsNonLocalOrUnexpectedSchemes(string address)
    {
        Assert.False(await new SetupHttpProbe().IsReadyAsync(new Uri(address), CancellationToken.None));
    }

    [Fact]
    public async Task FirstStartupCreatesAnIsolatedLoopbackStackWithPrivatePersistentCredentials()
    {
        using var directory = new DirectoryFixture();
        var platform = new Platform(); var download = new Download(); var probe = new Probe();
        var coordinator = Coordinator(directory, platform, download, probe);
        var result = await coordinator.EnsureReadyAsync();
        Assert.Equal(new Uri("http://127.0.0.1:55188/"), result.ApiUri);
        Assert.Equal(1, download.Calls);
        var state = ReadState(directory);
        var yaml = File.ReadAllText(Path.Combine(directory.Root, "compose.yaml"));
        var environment = File.ReadAllText(Path.Combine(directory.Root, ".env"));
        var sqlPassword = state.GetProperty("sqlPassword").GetString()!;
        var jwtKey = state.GetProperty("jwtKey").GetString()!;
        Assert.True(sqlPassword.Length >= 32);
        Assert.True(jwtKey.Length >= 32);
        Assert.NotEqual(sqlPassword, jwtKey);
        Assert.Contains(sqlPassword, environment);
        Assert.Contains(jwtKey, environment);
        Assert.DoesNotContain(sqlPassword, yaml);
        Assert.DoesNotContain(jwtKey, yaml);
        Assert.Contains("127.0.0.1:${API_PORT}:8080", yaml);
        Assert.DoesNotContain("build:", yaml);
        Assert.DoesNotContain("1433:1433", yaml);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(yaml, @"(?m)^    ports:"));
        var project = "kanbanboard-desktop-" + state.GetProperty("installationId").GetGuid().ToString("N")[..12];
        Assert.Contains(platform.Commands, c => c.Arguments.Contains(project));
        Assert.DoesNotContain(platform.Commands, c => c.Arguments.Any(a => a is "down" or "prune" or "rm" or "--volumes"));
        Assert.All(probe.Addresses, address => Assert.True(address.IsLoopback));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(directory.Root, ".env")));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(coordinator.InstallationFile));
        }
    }

    [Fact]
    public async Task RestartReusesItsOwnReadyStackWithoutDownloadsOrCredentialRotation()
    {
        using var directory = new DirectoryFixture();
        var platform = new Platform(); var download = new Download(); var probe = new Probe();
        var first = await Coordinator(directory, platform, download, probe).EnsureReadyAsync();
        var settings = File.ReadAllText(Path.Combine(directory.Root, "installation.json"));
        var environment = File.ReadAllText(Path.Combine(directory.Root, ".env"));
        platform.Commands.Clear();
        var second = await Coordinator(directory, platform, download, probe).EnsureReadyAsync();
        Assert.Equal(first.ApiUri, second.ApiUri);
        Assert.Equal(settings, File.ReadAllText(Path.Combine(directory.Root, "installation.json")));
        Assert.Equal(environment, File.ReadAllText(Path.Combine(directory.Root, ".env")));
        Assert.Equal(1, download.Calls);
        Assert.Equal(1, platform.PortRequests);
        Assert.DoesNotContain(platform.Commands, c => c.Arguments.Any(a => a is "load" or "pull" or "up"));
    }

    [Fact]
    public async Task MissingDockerStopsBeforeCredentialsDownloadsOrProcessesAreCreated()
    {
        using var directory = new DirectoryFixture();
        var platform = new Platform { HasDocker = false }; var download = new Download();
        var error = await Assert.ThrowsAsync<SetupException>(() => Coordinator(directory, platform, download).EnsureReadyAsync());
        Assert.Equal(SetupAction.InstallDocker, error.Action);
        Assert.False(File.Exists(Path.Combine(directory.Root, "installation.json")));
        Assert.False(File.Exists(Path.Combine(directory.Root, ".env")));
        Assert.Equal(0, download.Calls);
        Assert.Empty(platform.Commands);
    }

    [Fact]
    public async Task DamagedArchiveIsRejectedBeforeDockerLoadOrServerStart()
    {
        using var directory = new DirectoryFixture();
        var platform = new Platform(); var download = new Download { Bytes = Encoding.UTF8.GetBytes("damaged image") }; var probe = new Probe();
        var error = await Assert.ThrowsAsync<SetupException>(() => Coordinator(directory, platform, download, probe).EnsureReadyAsync());
        Assert.Contains("сума", error.Message);
        Assert.DoesNotContain(platform.Commands, c => c.Arguments.Any(a => a is "load" or "up"));
        Assert.Empty(probe.Addresses);
        Assert.False(File.Exists(Path.Combine(directory.Root, "server-amd64.tar.gz.partial")));
        Assert.False(File.Exists(Path.Combine(directory.Root, "server-amd64.tar.gz")));
    }

    [Fact]
    public async Task CancellationBeforeLoadKeepsTheServerStoppedAndReleasesTheInstallationLock()
    {
        using var directory = new DirectoryFixture();
        var platform = new Platform(); var download = new Download { Started = new(), Continue = new() };
        using var cancellation = new CancellationTokenSource();
        var first = Coordinator(directory, platform, download).EnsureReadyAsync(token: cancellation.Token);
        await download.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.DoesNotContain(platform.Commands, c => c.Arguments.Any(a => a is "load" or "up"));
        Assert.False(File.Exists(Path.Combine(directory.Root, "server-amd64.tar.gz.partial")));
        download.Continue.TrySetResult();
        var retried = await Coordinator(directory, platform, download).EnsureReadyAsync();
        Assert.True(retried.ApiUri.IsLoopback);
    }

    [Fact]
    public async Task ParallelPreparationCannotWriteTheSameInstallationTwice()
    {
        using var directory = new DirectoryFixture();
        var platform = new Platform(); var download = new Download { Started = new(), Continue = new() };
        var first = Coordinator(directory, platform, download).EnsureReadyAsync();
        await download.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var error = await Assert.ThrowsAsync<SetupException>(() => Coordinator(directory, platform, download).EnsureReadyAsync());
            Assert.Contains("іншому", error.Message);
            Assert.Equal(1, download.Calls);
        }
        finally { download.Continue.TrySetResult(); }
        await first;
    }

    [Fact]
    public async Task ForeignVolumeIsNeverAdoptedReplacedOrStarted()
    {
        using var directory = new DirectoryFixture();
        var platform = new Platform { VolumeExists = true, ForeignVolume = true }; var download = new Download();
        var error = await Assert.ThrowsAsync<SetupException>(() => Coordinator(directory, platform, download).EnsureReadyAsync());
        Assert.Equal(SetupAction.None, error.Action);
        Assert.Equal(0, download.Calls);
        Assert.DoesNotContain(platform.Commands, c => c.Arguments.Any(a => a is "up" or "load" or "down" or "rm"));
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("public-api")]
    [InlineData("published-sql")]
    public async Task AHealthyHttpEndpointCannotOverrideUnexpectedContainerIdentityOrPorts(string problem)
    {
        using var directory = new DirectoryFixture();
        var platform = new Platform { StackExists = true, ForeignContainer = problem == "owner", ApiBindingIp = problem == "public-api" ? "0.0.0.0" : "127.0.0.1", PublishSql = problem == "published-sql" };
        var download = new Download(); var probe = new Probe();
        var error = await Assert.ThrowsAsync<SetupException>(() => Coordinator(directory, platform, download, probe).EnsureReadyAsync());
        Assert.Equal(SetupAction.None, error.Action);
        Assert.Empty(probe.Addresses);
        Assert.Equal(0, download.Calls);
        Assert.DoesNotContain(platform.Commands, c => c.Arguments.Contains("up"));
    }

    [Fact]
    public async Task ExistingDamagedSettingsArePreservedAndCannotLeakCredentialsInTheError()
    {
        using var directory = new DirectoryFixture();
        var platform = new Platform(); var download = new Download();
        await Coordinator(directory, platform, download).EnsureReadyAsync();
        var stateBefore = File.ReadAllText(Path.Combine(directory.Root, "installation.json"));
        var password = ReadState(directory).GetProperty("sqlPassword").GetString()!;
        var editedEnvironment = File.ReadAllText(Path.Combine(directory.Root, ".env")) + "UNEXPECTED_SETTING=do-not-erase\n";
        File.WriteAllText(Path.Combine(directory.Root, ".env"), editedEnvironment);
        var error = await Assert.ThrowsAsync<SetupException>(() => Coordinator(directory, platform, download).EnsureReadyAsync());
        Assert.Equal(SetupAction.None, error.Action);
        Assert.DoesNotContain(password, error.Message);
        Assert.Equal(editedEnvironment, File.ReadAllText(Path.Combine(directory.Root, ".env")));
        Assert.Equal(stateBefore, File.ReadAllText(Path.Combine(directory.Root, "installation.json")));
        Assert.Equal(1, download.Calls);
    }

    [Theory]
    [InlineData("sqlPassword")]
    [InlineData("jwtKey")]
    public async Task NullStoredCredentialsFailSafelyWithoutReinitializingData(string field)
    {
        using var directory = new DirectoryFixture();
        var platform = new Platform(); var download = new Download();
        var coordinator = Coordinator(directory, platform, download);
        await coordinator.EnsureReadyAsync();
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(coordinator.InstallationFile))!.AsObject();
        node[field] = null;
        var damaged = node.ToJsonString();
        File.WriteAllText(coordinator.InstallationFile, damaged);
        var error = await Assert.ThrowsAsync<SetupException>(() => Coordinator(directory, platform, download).EnsureReadyAsync());
        Assert.Equal(SetupAction.None, error.Action);
        Assert.Equal(damaged, File.ReadAllText(coordinator.InstallationFile));
        Assert.Equal(1, download.Calls);
    }

    [Fact]
    public async Task RawProcessOutputWithSecretsIsNotExposedToTheScreen()
    {
        using var directory = new DirectoryFixture();
        var platform = new Platform { FailLoad = true }; var progress = new ProgressLog();
        var error = await Assert.ThrowsAsync<SetupException>(() => Coordinator(directory, platform, new Download()).EnsureReadyAsync(progress));
        var password = ReadState(directory).GetProperty("sqlPassword").GetString()!;
        Assert.DoesNotContain(password, error.Message);
        Assert.DoesNotContain(Platform.FailureSentinel, error.Message);
        Assert.DoesNotContain(progress.Items, row => row.Detail.Contains(password, StringComparison.Ordinal) || row.Detail.Contains(Platform.FailureSentinel, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AVerifiedDownloadIsReusedAfterARecoverableDockerLoadFailure()
    {
        using var directory = new DirectoryFixture();
        var platform = new Platform { FailLoad = true }; var download = new Download();
        await Assert.ThrowsAsync<SetupException>(() => Coordinator(directory, platform, download).EnsureReadyAsync());
        Assert.True(File.Exists(Path.Combine(directory.Root, "server-amd64.tar.gz")));
        var password = ReadState(directory).GetProperty("sqlPassword").GetString();
        platform.FailLoad = false;
        await Coordinator(directory, platform, download).EnsureReadyAsync();
        Assert.Equal(1, download.Calls);
        Assert.Equal(password, ReadState(directory).GetProperty("sqlPassword").GetString());
    }

    [Fact]
    public async Task AnArchiveWithTheWrongImageArchitectureIsNotStartedOrMarkedTrusted()
    {
        using var directory = new DirectoryFixture();
        var platform = new Platform { LoadedImageArchitecture = "arm64" };
        var error = await Assert.ThrowsAsync<SetupException>(() => Coordinator(directory, platform, new Download()).EnsureReadyAsync());
        Assert.Equal(SetupAction.None, error.Action);
        Assert.DoesNotContain(platform.Commands, c => c.Arguments.Contains("up"));
        Assert.Equal(JsonValueKind.Null, ReadState(directory).GetProperty("apiImageId").ValueKind);
    }

    [Theory]
    [InlineData("linux", "amd64")]
    [InlineData("windows", "arm64")]
    [InlineData("macos", "unsupported")]
    [InlineData("macos", "arm64")]
    public async Task AnUnsupportedOrMismatchedPackageDoesNotTouchDocker(string os, string architecture)
    {
        using var directory = new DirectoryFixture();
        var platform = new Platform { OS = os, Architecture = architecture }; var download = new Download();
        var error = await Assert.ThrowsAsync<SetupException>(() => Coordinator(directory, platform, download).EnsureReadyAsync());
        Assert.Equal(SetupAction.None, error.Action);
        Assert.Empty(platform.Commands);
        Assert.Equal(0, download.Calls);
        Assert.False(File.Exists(Path.Combine(directory.Root, "installation.json")));
    }

    [Fact]
    public async Task InstalledDockerDesktopCanBeStartedWithoutOpeningAnInstaller()
    {
        using var directory = new DirectoryFixture();
        var platform = new Platform { DockerRunning = false };
        var result = await Coordinator(directory, platform, new Download()).EnsureReadyAsync();
        Assert.True(result.ApiUri.IsLoopback);
        Assert.Equal(1, platform.OpenDockerCalls);
        Assert.Empty(platform.OpenedFiles);
    }

    [Theory]
    [InlineData("macos", "amd64", "https://desktop.docker.com/mac/main/amd64/Docker.dmg")]
    [InlineData("macos", "arm64", "https://desktop.docker.com/mac/main/arm64/Docker.dmg")]
    [InlineData("windows", "amd64", "https://desktop.docker.com/win/main/amd64/Docker%20Desktop%20Installer.exe")]
    public async Task DockerInstallationOpensTheOfficialInstallerWithoutAcceptingAgreementsOrRunningCommands(string os, string architecture, string expectedSource)
    {
        using var directory = new DirectoryFixture();
        var platform = new Platform { OS = os, Architecture = architecture }; var download = new Download();
        var manifest = ValidManifest() with { ApiImage = "kanbanboard-api:0.2.0-" + architecture };
        var coordinator = new LocalSetupCoordinator(manifest, directory.Root, platform, download, new Probe());
        await coordinator.DownloadAndOpenDockerInstallerAsync();
        Assert.Equal(expectedSource, Assert.Single(download.Sources).AbsoluteUri);
        Assert.True(File.Exists(Assert.Single(platform.OpenedFiles)));
        Assert.Empty(platform.Commands);
        Assert.False(File.Exists(coordinator.InstallationFile));
    }

    private static LocalSetupCoordinator Coordinator(DirectoryFixture directory, Platform platform, Download download, Probe? probe = null) =>
        new(ValidManifest(), directory.Root, platform, download, probe ?? new Probe());

    private static JsonElement ReadState(DirectoryFixture directory)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.Root, "installation.json")));
        return document.RootElement.Clone();
    }

    private sealed class ProgressLog : IProgress<SetupProgress>
    {
        public List<SetupProgress> Items { get; } = [];
        public void Report(SetupProgress value) => Items.Add(value);
    }

    private sealed record Command(IReadOnlyList<string> Arguments, string WorkingDirectory);
    private sealed class Platform : ISetupPlatform
    {
        public const string FailureSentinel = "raw-process-error-never-show";
        public string OS { get; set; } = "macos";
        public string Architecture { get; set; } = "amd64";
        public bool HasDocker { get; set; } = true;
        public bool DockerRunning { get; set; } = true;
        public bool ApiImagePresent { get; set; }
        public bool SqlImagePresent { get; set; }
        public bool StackExists { get; set; }
        public bool VolumeExists { get; set; }
        public bool ForeignVolume { get; set; }
        public bool ForeignContainer { get; set; }
        public bool PublishSql { get; set; }
        public bool FailLoad { get; set; }
        public string ApiBindingIp { get; set; } = "127.0.0.1";
        public string? LoadedImageArchitecture { get; set; }
        public int PortRequests { get; private set; }
        public int OpenDockerCalls { get; private set; }
        public List<string> OpenedFiles { get; } = [];
        public List<Command> Commands { get; } = [];
        public string? FindDocker() => HasDocker ? "/fixture/docker" : null;
        public int GetAvailablePort() { PortRequests++; return 55188; }
        public Task OpenDockerAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); OpenDockerCalls++; DockerRunning = true; return Task.CompletedTask; }
        public Task OpenFileAsync(string path, CancellationToken token) { token.ThrowIfCancellationRequested(); OpenedFiles.Add(path); return Task.CompletedTask; }
        public Task<SetupCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Commands.Add(new(arguments.ToArray(), workingDirectory));
            SetupCommandResult Ok(string value = "") => new(0, value, "");
            SetupCommandResult Missing() => new(1, "", "not found");
            var args = arguments.ToArray();
            SetupCommandResult result;
            if (args[0] == "info") result = DockerRunning ? Ok("linux") : Missing();
            else if (args[0] == "compose" && args[1] == "version") result = Ok("v2.39.0");
            else if (args[0] == "volume")
            {
                var id = ReadStateAt(workingDirectory).GetProperty("installationId").GetString();
                result = VolumeExists ? Ok(JsonSerializer.Serialize(new Dictionary<string, string?> { ["io.kanbanboard.installation"] = ForeignVolume ? Guid.NewGuid().ToString() : id })) : Missing();
            }
            else if (args[0] == "ps") result = Ok(StackExists ? "fixture-api\nfixture-sql\n" : "");
            else if (args[0] == "inspect")
            {
                var state = ReadStateAt(workingDirectory); var service = args[1] == "fixture-api" ? "api" : "sqlserver";
                var id = state.GetProperty("installationId").GetString();
                var port = state.GetProperty("apiPort").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture);
                var ports = service == "api"
                    ? new Dictionary<string, object?> { ["8080/tcp"] = new[] { new { HostIp = ApiBindingIp, HostPort = port } } }
                    : new Dictionary<string, object?> { ["1433/tcp"] = PublishSql ? new[] { new { HostIp = "0.0.0.0", HostPort = "1433" } } : null };
                result = Ok(JsonSerializer.Serialize(new
                {
                    labels = new Dictionary<string, string?> { ["io.kanbanboard.installation"] = ForeignContainer ? Guid.NewGuid().ToString() : id, ["com.docker.compose.service"] = service },
                    image = service == "api" ? ValidManifest().ApiImage : ValidManifest().SqlImage,
                    imageId = "sha256:fixture-id", ports, running = true
                }));
            }
            else if (args[0] == "image")
            {
                var present = args[2] == ValidManifest().ApiImage ? ApiImagePresent : SqlImagePresent;
                result = present ? Ok(args[^1] == "{{.Architecture}}" ? LoadedImageArchitecture ?? Architecture : "sha256:fixture-id") : Missing();
            }
            else if (args[0] == "load")
            {
                var password = ReadStateAt(workingDirectory).GetProperty("sqlPassword").GetString();
                if (FailLoad) result = new(1, "", FailureSentinel + password);
                else { ApiImagePresent = true; result = Ok(); }
            }
            else if (args[0] == "pull") { SqlImagePresent = true; result = Ok(); }
            else if (args[0] == "compose" && args.Contains("up")) { StackExists = true; VolumeExists = true; result = Ok(); }
            else throw new InvalidOperationException("Unexpected fake command: " + string.Join(' ', args));
            return Task.FromResult(result);
        }
        private static JsonElement ReadStateAt(string root)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "installation.json")));
            return document.RootElement.Clone();
        }
    }

    private sealed class Download : ISetupDownload
    {
        public int Calls { get; private set; }
        public List<Uri> Sources { get; } = [];
        public byte[] Bytes { get; set; } = Archive;
        public TaskCompletionSource? Started { get; set; }
        public TaskCompletionSource? Continue { get; set; }
        public async Task DownloadAsync(Uri source, string destination, IProgress<double>? progress, CancellationToken token)
        {
            Calls++; Sources.Add(source);
            Started?.TrySetResult();
            if (Continue is not null) await Continue.Task.WaitAsync(token);
            await File.WriteAllBytesAsync(destination, Bytes, token);
            progress?.Report(100);
        }
    }

    private sealed class Probe : ISetupHttpProbe
    {
        public List<Uri> Addresses { get; } = [];
        public Task<bool> IsReadyAsync(Uri apiUri, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Addresses.Add(apiUri);
            return Task.FromResult(true);
        }
    }
}
