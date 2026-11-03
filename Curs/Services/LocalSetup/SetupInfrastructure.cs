using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Curs.Services.LocalSetup;

public sealed class SetupPlatform : ISetupPlatform
{
    public string OS => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "unsupported";
    public string Architecture => RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.X64 ? "amd64" : "unsupported";

    public string? FindDocker()
    {
        string[] fixedPaths = OS == "macos"
            ? ["/Applications/Docker.app/Contents/Resources/bin/docker", "/usr/local/bin/docker", "/opt/homebrew/bin/docker"]
            : [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "DockerDesktop", "resources", "bin", "docker.exe"),
               Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "resources", "bin", "docker.exe")];
        foreach (var path in fixedPaths) if (File.Exists(path)) return path;
        var file = OS == "windows" ? "docker.exe" : "docker";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            try { var path = Path.Combine(directory.Trim('"'), file); if (File.Exists(path)) return path; }
            catch (ArgumentException) { }
        }
        return null;
    }

    public async Task<SetupCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = workingDirectory };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        try
        {
            process.Start();
            // Read both pipes concurrently so a full stderr pipe cannot block Docker.
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var error = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            return new(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } catch (Win32Exception) { }
            if (token.IsCancellationRequested) throw;
            throw new SetupException("Операція триває надто довго. Перевірте Docker Desktop і повторіть спробу.");
        }
        catch (Win32Exception) { throw new SetupException("Не вдалося запустити системний компонент. Перевірте встановлення Docker Desktop.", SetupAction.InstallDocker); }
    }

    public Task OpenDockerAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var path = OS == "macos" ? "/Applications/Docker.app" : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "DockerDesktop", "Docker Desktop.exe");
        if (OS == "windows" && !File.Exists(path)) path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "Docker Desktop.exe");
        if (!Directory.Exists(path) && !File.Exists(path)) throw new SetupException("Docker Desktop не знайдено. Встановіть його та повторіть перевірку.", SetupAction.InstallDocker);
        return OpenFileAsync(path, token);
    }

    public Task OpenFileAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var start = OperatingSystem.IsMacOS() ? new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false } : new ProcessStartInfo(path) { UseShellExecute = true };
            if (OperatingSystem.IsMacOS()) start.ArgumentList.Add(path);
            using var process = Process.Start(start);
            if (process is null) throw new SetupException("Не вдалося відкрити встановлювач. Відкрийте завантажений файл вручну.");
            return Task.CompletedTask;
        }
        catch (Win32Exception) { throw new SetupException("Система не відкрила встановлювач. Відкрийте завантажений файл у папці компонентів і завершіть встановлення."); }
    }

    public int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        try { listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}

public sealed class SetupDownload : ISetupDownload
{
    public async Task DownloadAsync(Uri source, string destination, IProgress<double>? progress, CancellationToken token)
    {
        if (source.Scheme != Uri.UriSchemeHttps || !Allowed(source.Host) || source.UserInfo.Length != 0 || !source.IsDefaultPort) throw new SetupException("Недопустиме джерело компонентів.", SetupAction.None);
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(20) };
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("KanbanBoard-Setup/1.0");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(40));
        var uri = source;
        for (var redirect = 0; redirect < 8; redirect++)
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location;
                if (location is null) throw new SetupException("Сервер не надав адресу компонента. Повторіть завантаження.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (uri.Scheme != Uri.UriSchemeHttps || !Allowed(uri.Host) || uri.UserInfo.Length != 0 || !uri.IsDefaultPort) throw new SetupException("Сервер перенаправив завантаження на недопустиме джерело.", SetupAction.None);
                continue;
            }
            if (!response.IsSuccessStatusCode) throw new SetupException("Компонент поки недоступний для завантаження. Перевірте інтернет і повторіть спробу.");
            var total = response.Content.Headers.ContentLength;
            const long maximum = 8L * 1024 * 1024 * 1024;
            if (total > maximum) throw new SetupException("Розмір компонента перевищує допустиму межу.", SetupAction.None);
            await using var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true);
            var buffer = new byte[131072]; long readTotal = 0; var lastUpdate = Stopwatch.StartNew();
            while (true)
            {
                var read = await input.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
                if (read == 0) break;
                readTotal += read;
                if (readTotal > maximum) throw new SetupException("Розмір компонента перевищує допустиму межу.", SetupAction.None);
                await output.WriteAsync(buffer.AsMemory(0, read), deadline.Token).ConfigureAwait(false);
                if (total > 0 && lastUpdate.ElapsedMilliseconds >= 200) { progress?.Report(Math.Min(100, 100d * readTotal / total.Value)); lastUpdate.Restart(); }
            }
            if (total is {} expected && expected != readTotal) throw new SetupException("Завантаження обірвалося. Повторіть спробу.");
            await output.FlushAsync(deadline.Token).ConfigureAwait(false);
            progress?.Report(100);
            return;
        }
        throw new SetupException("Забагато перенаправлень під час завантаження. Повторіть спробу.");
    }

    private static bool Allowed(string host) => host == "github.com" || host == "release-assets.githubusercontent.com" || host == "objects.githubusercontent.com" || host == "desktop.docker.com";
}

public sealed class SetupHttpProbe : ISetupHttpProbe
{
    public async Task<bool> IsReadyAsync(Uri apiUri, CancellationToken token)
    {
        if (!apiUri.IsLoopback || apiUri.Scheme != Uri.UriSchemeHttp) return false;
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(2) }) { Timeout = TimeSpan.FromSeconds(4) };
        try
        {
            using var response = await http.GetAsync(new Uri(apiUri, "health/ready"), token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
            return body.RootElement.TryGetProperty("status", out var status) && status.GetString() == "ready";
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or OperationCanceledException)
        {
            token.ThrowIfCancellationRequested();
            return false;
        }
    }
}
