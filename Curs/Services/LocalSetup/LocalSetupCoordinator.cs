using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Curs.Services.LocalSetup;

public sealed class LocalSetupCoordinator
{
    private readonly RuntimeManifest manifest;
    private readonly ISetupPlatform platform;
    private readonly ISetupDownload download;
    private readonly ISetupHttpProbe probe;
    public string DataDirectory { get; }
    public string InstallationFile => Path.Combine(DataDirectory, "installation.json");
    private string EnvFile => Path.Combine(DataDirectory, ".env");
    private string ComposeFile => Path.Combine(DataDirectory, "compose.yaml");
    private const string OwnershipLabel = "io.kanbanboard.installation";
    private const string InspectFormat = "{\"labels\":{{json .Config.Labels}},\"image\":{{json .Config.Image}},\"imageId\":{{json .Image}},\"ports\":{{json .NetworkSettings.Ports}},\"running\":{{json .State.Running}}}";

    public LocalSetupCoordinator(RuntimeManifest manifest, string? dataDirectory = null, ISetupPlatform? platform = null, ISetupDownload? download = null, ISetupHttpProbe? probe = null)
    {
        this.manifest = manifest;
        this.platform = platform ?? new SetupPlatform();
        this.download = download ?? new SetupDownload();
        this.probe = probe ?? new SetupHttpProbe();
        var configuredDirectory = dataDirectory ?? Environment.GetEnvironmentVariable("KANBAN_RUNTIME_HOME");
        DataDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(configuredDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KanbanBoard", "runtime")
            : configuredDirectory);
    }

    // The caller obtains explicit SQL Developer license consent before the first call.
    public async Task<LocalSetupResult> EnsureReadyAsync(IProgress<SetupProgress>? progress = null, CancellationToken token = default)
    {
        manifest.Validate(); ValidatePlatform(); token.ThrowIfCancellationRequested();
        try
        {
            PrepareDirectory();
            using var installationLock = AcquireLock();
            Report("Перевірка компонентів", "Перевіряємо Docker Desktop.");
            var docker = platform.FindDocker() ?? throw new SetupException("Для локального сервера потрібен Docker Desktop. Натисніть «Встановити Docker» та завершіть його налаштування.", SetupAction.InstallDocker);
            var info = await Run(docker, ["info", "--format", "{{.OSType}}"], TimeSpan.FromSeconds(20), token).ConfigureAwait(false);
            if (info.ExitCode != 0)
            {
                Report("Запуск Docker Desktop", "Підтвердьте системні запити, якщо вони з’являться.");
                await platform.OpenDockerAsync(token).ConfigureAwait(false);
                var ready = false;
                for (var attempt = 0; attempt < 45; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    info = await Run(docker, ["info", "--format", "{{.OSType}}"], TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
                    if (info.ExitCode == 0) { ready = true; break; }
                    await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                }
                if (!ready) throw new SetupException("Docker Desktop ще не готовий. Завершіть його налаштування й повторіть перевірку.", SetupAction.StartDocker);
            }
            if (!info.StandardOutput.Trim().Equals("linux", StringComparison.OrdinalIgnoreCase)) throw new SetupException("Для KanbanBoard потрібні Linux-контейнери. У Docker Desktop увімкніть режим Linux і повторіть перевірку.", SetupAction.StartDocker);
            var compose = await Run(docker, ["compose", "version", "--short"], TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
            if (compose.ExitCode != 0) throw new SetupException("Компонент Docker Compose не знайдено. Оновіть Docker Desktop і повторіть перевірку.", SetupAction.InstallDocker);

            var settings = LoadOrCreateInstallation();
            var project = Project(settings);
            await ValidateVolumeOwnership(docker, project, settings, token).ConfigureAwait(false);
            var stack = await InspectStack(docker, project, settings, false, token).ConfigureAwait(false);
            var apiUri = new Uri($"http://127.0.0.1:{settings.ApiPort}/");
            if (stack && settings.ApiImageId is not null && string.Equals(settings.ApiArchiveSha256, manifest.ServerArchiveSha256, StringComparison.OrdinalIgnoreCase) && await probe.IsReadyAsync(apiUri, token).ConfigureAwait(false))
            {
                Report("Усе готово", "Локальний сервер уже працює.", 100);
                return new(apiUri);
            }

            settings = await EnsureApiImage(docker, settings, progress, token).ConfigureAwait(false);
            Report("Підготовка бази даних", "Перевіряємо образ Microsoft SQL Server. Перша підготовка може тривати кілька хвилин.");
            var sql = await Run(docker, ["image", "inspect", manifest.SqlImage, "--format", "{{.Id}}"], TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
            if (sql.ExitCode != 0)
            {
                var pull = await Run(docker, ["pull", "--platform", "linux/amd64", manifest.SqlImage], TimeSpan.FromMinutes(25), token).ConfigureAwait(false);
                if (pull.ExitCode != 0) throw new SetupException("Не вдалося завантажити SQL Server. Перевірте інтернет і вільне місце в Docker Desktop.");
            }
            WriteCompose(settings);
            Report("Запуск локального сервера", "Створюємо базу або відкриваємо наявні дані. Ваші дошки зберігаються.");
            var up = await Run(docker, ComposeArguments(project, "up", "--detach", "--no-build", "--pull", "never", "--wait", "--wait-timeout", "240"), TimeSpan.FromMinutes(6), token).ConfigureAwait(false);
            if (up.ExitCode != 0) throw new SetupException("Локальний сервер ще не готовий. Перевірте вільне місце, пам’ять Docker Desktop і доступність його Linux-контейнерів. Дані збережено; повторіть спробу.");
            // A successful HTTP response alone is insufficient: verify ownership, images and loopback binding.
            await InspectStack(docker, project, settings, true, token).ConfigureAwait(false);
            Report("Перевірка готовності", "Перевіряємо з’єднання з локальною базою даних.");
            for (var attempt = 0; attempt < 30; attempt++)
            {
                if (await probe.IsReadyAsync(apiUri, token).ConfigureAwait(false)) { Report("Усе готово", "Відкриваємо KanbanBoard.", 100); return new(apiUri); }
                await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
            }
            throw new SetupException("Сервер запущено, але база ще не відповідає. Зачекайте трохи й повторіть перевірку.");
        }
        catch (OperationCanceledException) { throw; }
        catch (SetupException) { throw; }
        catch (UnauthorizedAccessException) { throw new SetupException("Немає доступу до папки компонентів KanbanBoard. Перевірте дозволи вашого облікового запису.", SetupAction.None); }
        catch (IOException) { throw new SetupException("Не вдалося зберегти компоненти KanbanBoard. Перевірте вільне місце на диску й повторіть спробу."); }
        catch (System.Net.Http.HttpRequestException) { throw new SetupException("Не вдалося завантажити компоненти. Перевірте інтернет і повторіть спробу."); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException) { throw new SetupException("Наявні налаштування компонентів пошкоджені. Дані не змінено. Зверніться до інструкції відновлення налаштувань.", SetupAction.None); }

        void Report(string title, string detail, double? percent = null) => progress?.Report(new(title, detail, percent));
    }

    public async Task DownloadAndOpenDockerInstallerAsync(IProgress<SetupProgress>? progress = null, CancellationToken token = default)
    {
        ValidatePlatform(); PrepareDirectory();
        using var installationLock = AcquireLock();
        var source = platform.OS == "macos"
            ? new Uri($"https://desktop.docker.com/mac/main/{platform.Architecture}/Docker.dmg")
            : new Uri("https://desktop.docker.com/win/main/amd64/Docker%20Desktop%20Installer.exe");
        var installer = Path.Combine(DataDirectory, platform.OS == "macos" ? "Docker.dmg" : "DockerDesktopInstaller.exe");
        var partial = installer + ".partial";
        try
        {
            progress?.Report(new("Завантаження Docker Desktop", "Завантажуємо офіційний встановлювач. Умови й системні дозволи підтверджуються у його вікні."));
            await download.DownloadAsync(source, partial, new InlineProgress<double>(p => progress?.Report(new("Завантаження Docker Desktop", "Офіційний встановлювач Docker.", p))), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Move(partial, installer, true);
            progress?.Report(new("Завершіть встановлення Docker", "Відкрито встановлювач Docker Desktop. Завершіть встановлення, запустіть Docker та натисніть «Перевірити знову».", 100));
            await platform.OpenFileAsync(installer, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { TryDelete(partial); throw; }
        catch (SetupException) { TryDelete(partial); throw; }
        catch (System.Net.Http.HttpRequestException) { TryDelete(partial); throw new SetupException("Не вдалося завантажити Docker Desktop. Перевірте інтернет і повторіть спробу.", SetupAction.InstallDocker); }
        catch (IOException) { TryDelete(partial); throw new SetupException("Не вдалося зберегти встановлювач Docker. Перевірте вільне місце на диску.", SetupAction.InstallDocker); }
    }

    public Task OpenDockerAsync(CancellationToken token = default) => platform.OpenDockerAsync(token);

    private void ValidatePlatform()
    {
        if (platform.OS is not ("windows" or "macos") || platform.Architecture is not ("amd64" or "arm64") || platform.OS == "windows" && platform.Architecture != "amd64")
            throw new SetupException("Цей пакет підтримує Windows x64 або macOS з Intel / Apple Silicon. Завантажте пакет для вашого комп’ютера.", SetupAction.None);
        if (!manifest.ApiImage.EndsWith("-" + platform.Architecture, StringComparison.Ordinal)) throw new SetupException("Пакет компонентів має іншу архітектуру. Завантажте відповідний пакет KanbanBoard.", SetupAction.None);
    }

    private void PrepareDirectory()
    {
        Directory.CreateDirectory(DataDirectory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(DataDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private FileStream AcquireLock()
    {
        try { return new FileStream(Path.Combine(DataDirectory, ".setup.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new SetupException("Підготовка KanbanBoard вже відкрита в іншому вікні. Завершіть її та повторіть спробу."); }
    }

    private sealed record Installation
    {
        public int SchemaVersion { get; init; } = 1;
        public Guid InstallationId { get; init; }
        public int ApiPort { get; init; }
        public string SqlPassword { get; init; } = "";
        public string JwtKey { get; init; } = "";
        public string? ApiImageId { get; init; }
        public string? ApiArchiveSha256 { get; init; }
    }

    private Installation LoadOrCreateInstallation()
    {
        if (File.Exists(InstallationFile))
        {
            var current = JsonSerializer.Deserialize<Installation>(File.ReadAllText(InstallationFile), RuntimeManifest.Json);
            if (current is null || current.SchemaVersion != 1 || current.InstallationId == Guid.Empty || current.ApiPort is < 1024 or > 65535 || string.IsNullOrWhiteSpace(current.SqlPassword) || string.IsNullOrWhiteSpace(current.JwtKey) || current.SqlPassword.Length < 32 || !Regex.IsMatch(current.SqlPassword, "^[A-Za-z0-9!]{32,128}$") || !Regex.IsMatch(current.JwtKey, "^[A-Za-z0-9+/=]{32,128}$") || !File.Exists(EnvFile))
                throw new SetupException("Наявні налаштування сервера неповні. Вони не будуть перезаписані. Відновіть папку компонентів із резервної копії.", SetupAction.None);
            var expected = EnvironmentText(current);
            if (File.ReadAllText(EnvFile) != expected) throw new SetupException("Налаштування локального сервера відрізняються від збережених. Дані не змінено; відновіть узгоджені налаштування.", SetupAction.None);
            return current;
        }
        if (File.Exists(EnvFile) || File.Exists(ComposeFile)) throw new SetupException("У папці компонентів уже є налаштування без запису встановлення. Вони не будуть перезаписані.", SetupAction.None);
        var created = new Installation { InstallationId = Guid.NewGuid(), ApiPort = platform.GetAvailablePort(), SqlPassword = "Kanban1!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)), JwtKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)) };
        if (created.ApiPort is < 1024 or > 65535) throw new SetupException("Не вдалося вибрати вільний локальний порт. Повторіть спробу.");
        WritePrivateAtomic(InstallationFile, JsonSerializer.Serialize(created, RuntimeManifest.Json));
        WritePrivateAtomic(EnvFile, EnvironmentText(created));
        return created;
    }

    private static string EnvironmentText(Installation state) => $"MSSQL_SA_PASSWORD={state.SqlPassword}\nJWT_KEY={state.JwtKey}\nAPI_PORT={state.ApiPort}\n";
    private static string Project(Installation state) => "kanbanboard-desktop-" + state.InstallationId.ToString("N")[..12];
    private Task<SetupCommandResult> Run(string docker, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken token) => platform.RunAsync(docker, args, DataDirectory, timeout, token);
    private IReadOnlyList<string> ComposeArguments(string project, params string[] args) => new[] { "compose", "--project-name", project, "--env-file", EnvFile, "--file", ComposeFile }.Concat(args).ToArray();

    private async Task ValidateVolumeOwnership(string docker, string project, Installation state, CancellationToken token)
    {
        var result = await Run(docker, ["volume", "inspect", project + "_sqlserver-data", "--format", "{{json .Labels}}"], TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
        if (result.ExitCode != 0) return;
        using var document = JsonDocument.Parse(result.StandardOutput);
        RequireOwnership(document.RootElement, state);
    }

    private async Task<bool> InspectStack(string docker, string project, Installation state, bool requireRunning, CancellationToken token)
    {
        var list = await Run(docker, ["ps", "-a", "--filter", "label=com.docker.compose.project=" + project, "--format", "{{.ID}}"], TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
        if (list.ExitCode != 0) throw new SetupException("Не вдалося перевірити локальні компоненти в Docker Desktop.");
        var ids = list.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var api = false; var sql = false;
        foreach (var id in ids)
        {
            var inspected = await Run(docker, ["inspect", id, "--format", InspectFormat], TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
            if (inspected.ExitCode != 0) throw new SetupException("Не вдалося перевірити локальний контейнер.");
            using var document = JsonDocument.Parse(inspected.StandardOutput); var root = document.RootElement;
            var labels = root.GetProperty("labels"); RequireOwnership(labels, state);
            var service = labels.GetProperty("com.docker.compose.service").GetString();
            if (service is not ("api" or "sqlserver")) throw new SetupException("У просторі KanbanBoard знайдено невідомі компоненти. Автоматичні зміни зупинено.", SetupAction.None);
            var running = root.GetProperty("running").GetBoolean();
            var image = root.GetProperty("image").GetString();
            if (service == "api")
            {
                if (root.TryGetProperty("ports", out var ports) && ports.ValueKind == JsonValueKind.Object && ports.TryGetProperty("8080/tcp", out var bindings) && bindings.ValueKind == JsonValueKind.Array)
                {
                    if (bindings.GetArrayLength() != 1 || bindings[0].GetProperty("HostIp").GetString() != "127.0.0.1" || bindings[0].GetProperty("HostPort").GetString() != state.ApiPort.ToString(System.Globalization.CultureInfo.InvariantCulture))
                        throw new SetupException("Локальний сервер має неочікувані мережеві налаштування. Автоматичні зміни зупинено.", SetupAction.None);
                    api = running && image == manifest.ApiImage && root.TryGetProperty("imageId", out var imageId) && imageId.GetString() == state.ApiImageId;
                }
            }
            else
            {
                if (root.TryGetProperty("ports", out var ports) && ports.ValueKind == JsonValueKind.Object && ports.EnumerateObject().Any(p => p.Value.ValueKind == JsonValueKind.Array && p.Value.GetArrayLength() > 0))
                    throw new SetupException("SQL Server має неочікуваний зовнішній порт. Автоматичні зміни зупинено.", SetupAction.None);
                sql = running && image == manifest.SqlImage;
            }
        }
        if (requireRunning && (!api || !sql || ids.Length != 2)) throw new SetupException("Не всі локальні компоненти KanbanBoard готові. Повторіть перевірку.");
        return api && sql && ids.Length == 2;
    }

    private static void RequireOwnership(JsonElement labels, Installation state)
    {
        if (labels.ValueKind != JsonValueKind.Object || !labels.TryGetProperty(OwnershipLabel, out var owner) || owner.GetString() != state.InstallationId.ToString("D"))
            throw new SetupException("Знайдено компоненти з іншими налаштуваннями власності. Вони не будуть змінені.", SetupAction.None);
    }

    private async Task<Installation> EnsureApiImage(string docker, Installation settings, IProgress<SetupProgress>? progress, CancellationToken token)
    {
        var current = await Run(docker, ["image", "inspect", manifest.ApiImage, "--format", "{{.Id}}"], TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
        if (current.ExitCode == 0 && settings.ApiImageId == current.StandardOutput.Trim() && string.Equals(settings.ApiArchiveSha256, manifest.ServerArchiveSha256, StringComparison.OrdinalIgnoreCase)) return settings;
        var archive = Path.Combine(DataDirectory, "server-" + platform.Architecture + ".tar.gz");
        if (!File.Exists(archive) || !await HashMatches(archive, token).ConfigureAwait(false))
        {
            var partial = archive + ".partial";
            try
            {
                progress?.Report(new("Завантаження сервера", "Завантажуємо готові компоненти KanbanBoard."));
                await download.DownloadAsync(new Uri(manifest.ServerArchiveUrl), partial, new InlineProgress<double>(p => progress?.Report(new("Завантаження сервера", "Завантажуємо готові компоненти KanbanBoard.", p))), token).ConfigureAwait(false);
                if (!await HashMatches(partial, token).ConfigureAwait(false)) throw new SetupException("Контрольна сума компонента не збігається. Його не буде запущено. Повторіть завантаження.");
                File.Move(partial, archive, true);
            }
            finally { TryDelete(partial); }
        }
        token.ThrowIfCancellationRequested();
        progress?.Report(new("Підготовка сервера", "Перевірений компонент додається до Docker Desktop."));
        var loaded = await Run(docker, ["load", "--input", archive], TimeSpan.FromMinutes(12), token).ConfigureAwait(false);
        if (loaded.ExitCode != 0) throw new SetupException("Не вдалося підготувати серверний компонент. Перевірте вільне місце в Docker Desktop.");
        var image = await Run(docker, ["image", "inspect", manifest.ApiImage, "--format", "{{.Id}}"], TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
        var architecture = await Run(docker, ["image", "inspect", manifest.ApiImage, "--format", "{{.Architecture}}"], TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
        if (image.ExitCode != 0 || image.StandardOutput.Trim().Length == 0 || architecture.ExitCode != 0 || architecture.StandardOutput.Trim() != platform.Architecture) throw new SetupException("Завантажений компонент не відповідає цьому пакету. Його запуск зупинено.", SetupAction.None);
        settings = settings with { ApiImageId = image.StandardOutput.Trim(), ApiArchiveSha256 = manifest.ServerArchiveSha256 };
        WritePrivateAtomic(InstallationFile, JsonSerializer.Serialize(settings, RuntimeManifest.Json));
        return settings;
    }

    private async Task<bool> HashMatches(string path, CancellationToken token)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        var hash = await SHA256.HashDataAsync(input, token).ConfigureAwait(false);
        return Convert.ToHexString(hash).Equals(manifest.ServerArchiveSha256, StringComparison.OrdinalIgnoreCase);
    }

    private void WriteCompose(Installation settings)
    {
        var owner = settings.InstallationId.ToString("D");
        var yaml = $$"""
services:
  sqlserver:
    image: {{manifest.SqlImage}}
    platform: linux/amd64
    environment:
      ACCEPT_EULA: "Y"
      MSSQL_PID: "Developer"
      MSSQL_SA_PASSWORD: ${MSSQL_SA_PASSWORD}
      MSSQL_MEMORY_LIMIT_MB: "2048"
    labels:
      io.kanbanboard.installation: "{{owner}}"
    volumes:
      - sqlserver-data:/var/opt/mssql
    healthcheck:
      test: ["CMD-SHELL", "SQLCMDPASSWORD=\"$$MSSQL_SA_PASSWORD\" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -Q 'SELECT 1' -b -o /dev/null"]
      interval: 10s
      timeout: 10s
      start_period: 60s
      retries: 12
    restart: unless-stopped
  api:
    image: {{manifest.ApiImage}}
    platform: linux/{{platform.Architecture}}
    environment:
      ASPNETCORE_HTTP_PORTS: "8080"
      ASPNETCORE_ENVIRONMENT: "Production"
      Database__ApplyMigrationsOnStartup: "true"
      ConnectionStrings__Default: "Server=sqlserver,1433;Database=KanbanDB;User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=True;"
      Jwt__Key: ${JWT_KEY}
      Jwt__Issuer: "KanbanBoard.Api"
      Jwt__Audience: "KanbanBoard.Desktop"
    labels:
      io.kanbanboard.installation: "{{owner}}"
    ports:
      - "127.0.0.1:${API_PORT}:8080"
    depends_on:
      sqlserver:
        condition: service_healthy
    restart: unless-stopped
    ulimits:
      core: 0
volumes:
  sqlserver-data:
    labels:
      io.kanbanboard.installation: "{{owner}}"
""";
        WritePrivateAtomic(ComposeFile, yaml + "\n");
    }

    private static void WritePrivateAtomic(string path, string text)
    {
        var temporary = path + ".new";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)); writer.Write(text); writer.Flush(); stream.Flush(true);
        }
        File.Move(temporary, path, true);
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
}
