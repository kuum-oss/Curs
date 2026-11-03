using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Curs.Services.LocalSetup;

public enum SetupAction { None, InstallDocker, StartDocker, Retry }

public sealed class SetupException(string message, SetupAction action = SetupAction.Retry) : Exception(message)
{
    public SetupAction Action { get; } = action;
}

public sealed record SetupProgress(string Title, string Detail, double? Percent = null);
public sealed record LocalSetupResult(Uri ApiUri);
public sealed record SetupCommandResult(int ExitCode, string StandardOutput, string StandardError);

public sealed record RuntimeManifest
{
    public string Version { get; init; } = "";
    public string ApiImage { get; init; } = "";
    public string ServerArchiveUrl { get; init; } = "";
    public string ServerArchiveSha256 { get; init; } = "";
    public string SqlImage { get; init; } = "";

    public static RuntimeManifest? Load(string? baseDirectory = null)
    {
        var path = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "setup", "runtime.json");
        if (!File.Exists(path)) return null;
        try
        {
            var manifest = JsonSerializer.Deserialize<RuntimeManifest>(File.ReadAllText(path), Json);
            if (manifest is null) throw new JsonException();
            manifest.Validate();
            return manifest;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new SetupException("Не вдалося прочитати опис компонентів. Завантажте повний пакет KanbanBoard повторно.", SetupAction.None);
        }
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Version) || string.IsNullOrWhiteSpace(ApiImage) || string.IsNullOrWhiteSpace(ServerArchiveUrl) || string.IsNullOrWhiteSpace(ServerArchiveSha256) || string.IsNullOrWhiteSpace(SqlImage) || !Regex.IsMatch(Version, @"^v?\d+\.\d+\.\d+(?:-[a-zA-Z0-9.-]+)?$") ||
            !Regex.IsMatch(ApiImage, @"^kanbanboard-api:[a-z0-9][a-z0-9_.-]{0,127}$") ||
            !Regex.IsMatch(ServerArchiveSha256, "^[a-fA-F0-9]{64}$") ||
            !Uri.TryCreate(ServerArchiveUrl, UriKind.Absolute, out var source) ||
            source.Scheme != Uri.UriSchemeHttps || source.Host != "github.com" || !source.IsDefaultPort ||
            source.UserInfo.Length != 0 || source.Query.Length != 0 || source.Fragment.Length != 0 ||
            !source.AbsolutePath.StartsWith("/kuum-oss/Curs/releases/download/", StringComparison.Ordinal) ||
            !Regex.IsMatch(SqlImage, @"^mcr\.microsoft\.com/mssql/server:2022-latest@sha256:[a-fA-F0-9]{64}$"))
            throw new SetupException("Опис компонентів KanbanBoard некоректний. Завантажте офіційний пакет повторно.", SetupAction.None);
    }

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}

public interface ISetupPlatform
{
    string OS { get; }
    string Architecture { get; }
    string? FindDocker();
    Task<SetupCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken token);
    Task OpenDockerAsync(CancellationToken token);
    Task OpenFileAsync(string path, CancellationToken token);
    int GetAvailablePort();
}

public interface ISetupDownload
{
    Task DownloadAsync(Uri source, string destination, IProgress<double>? progress, CancellationToken token);
}

public interface ISetupHttpProbe
{
    Task<bool> IsReadyAsync(Uri apiUri, CancellationToken token);
}
