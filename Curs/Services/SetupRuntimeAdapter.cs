using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Curs.Services.LocalSetup;

namespace Curs.Services;

public interface ISetupRuntime
{
    bool HasAcceptedTerms { get; }
    void AcceptTerms();
    Task<LocalSetupResult> PrepareAsync(IProgress<SetupProgress> progress, CancellationToken token);
    Task InstallDockerAsync(IProgress<SetupProgress> progress, CancellationToken token);
    Task StartDockerAsync(CancellationToken token);
}

// Consent belongs to this installation and the exact SQL image, not to a user session.
public sealed class SetupRuntimeAdapter(LocalSetupCoordinator coordinator, RuntimeManifest manifest) : ISetupRuntime
{
    private string ConsentPath => Path.Combine(coordinator.DataDirectory, "sql-consent.json");
    public bool HasAcceptedTerms
    {
        get
        {
            try
            {
                var consent = JsonSerializer.Deserialize<Consent>(File.ReadAllText(ConsentPath));
                return consent?.SqlImage == manifest.SqlImage && consent.AcceptedAtUtc != default;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return false; }
        }
    }

    public void AcceptTerms()
    {
        Directory.CreateDirectory(coordinator.DataDirectory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(coordinator.DataDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporary = ConsentPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Consent(manifest.SqlImage, DateTimeOffset.UtcNow)));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, ConsentPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public Task<LocalSetupResult> PrepareAsync(IProgress<SetupProgress> progress, CancellationToken token)
    {
        if (!HasAcceptedTerms) throw new SetupException("Перед підготовкою прийміть умови використання локальної бази.", SetupAction.None);
        return coordinator.EnsureReadyAsync(progress, token);
    }
    public Task InstallDockerAsync(IProgress<SetupProgress> progress, CancellationToken token) => coordinator.DownloadAndOpenDockerInstallerAsync(progress, token);
    public Task StartDockerAsync(CancellationToken token) => coordinator.OpenDockerAsync(token);
    private sealed record Consent(string SqlImage, DateTimeOffset AcceptedAtUtc);
}
