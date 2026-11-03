using Curs.Services;
using Curs.Services.LocalSetup;
using Curs.ViewModels;
using Xunit;

namespace KanbanApi.Tests;

public sealed class DesktopSetupTests
{
    private sealed class Runtime : ISetupRuntime
    {
        public bool HasAcceptedTerms { get; set; }
        public int Accepted, Prepared, Installed, Started;
        public Func<IProgress<SetupProgress>, CancellationToken, Task<LocalSetupResult>>? Prepare;
        public void AcceptTerms() { HasAcceptedTerms = true; Accepted++; }
        public Task<LocalSetupResult> PrepareAsync(IProgress<SetupProgress> progress, CancellationToken token)
        { Prepared++; return Prepare?.Invoke(progress, token) ?? Task.FromResult(new LocalSetupResult(new Uri("http://127.0.0.1:55180/"))); }
        public Task InstallDockerAsync(IProgress<SetupProgress> progress, CancellationToken token) { Installed++; return Task.CompletedTask; }
        public Task StartDockerAsync(CancellationToken token) { Started++; return Task.CompletedTask; }
    }

    [Fact]
    public async Task FirstLaunchRequiresExplicitConsentAndOpensOnlyAfterReadiness()
    {
        var runtime = new Runtime(); Uri? opened = null;
        using var vm = new SetupViewModel(runtime, uri => opened = uri);
        await vm.InitializeAsync();
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.Equal(0, runtime.Prepared);
        vm.AcceptedTerms = true;
        Assert.True(vm.StartCommand.CanExecute(null));
        await vm.StartAsync();
        Assert.Equal(1, runtime.Accepted); Assert.Equal(1, runtime.Prepared);
        Assert.Equal(new Uri("http://127.0.0.1:55180/"), opened);
        Assert.False(vm.NeedsConsent); Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task ReturningInstallationStartsAutomaticallyWithoutRecordingNewConsent()
    {
        var runtime = new Runtime { HasAcceptedTerms = true }; var opened = false;
        using var vm = new SetupViewModel(runtime, _ => opened = true);
        await vm.InitializeAsync();
        Assert.True(opened); Assert.Equal(1, runtime.Prepared); Assert.Equal(0, runtime.Accepted);
    }

    [Fact]
    public async Task MissingDockerExposesInstallerAndCannotFallThroughToLogin()
    {
        var runtime = new Runtime { Prepare = (_, _) => throw new SetupException("Потрібен Docker Desktop.", SetupAction.InstallDocker) };
        using var vm = new SetupViewModel(runtime, _ => throw new InvalidOperationException("Must wait for server"));
        vm.AcceptedTerms = true;
        await vm.StartAsync();
        Assert.True(vm.NeedsDockerInstall); Assert.True(vm.HasError); Assert.False(vm.IsBusy);
        await vm.InstallDockerAsync();
        Assert.Equal(1, runtime.Installed); Assert.Equal(1, runtime.Prepared);
        Assert.Contains("Завершіть", vm.Title);
    }

    [Fact]
    public async Task CancellationBlocksDuplicateStartAndIgnoresLateProgress()
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IProgress<SetupProgress>? late = null;
        var runtime = new Runtime { HasAcceptedTerms = true, Prepare = async (progress, token) =>
        {
            late = progress; progress.Report(new("Завантаження", "Компоненти", 40)); arrived.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new(new Uri("http://127.0.0.1:55180/"));
        }};
        using var vm = new SetupViewModel(runtime, _ => throw new InvalidOperationException("Cancelled"));
        var preparing = vm.StartAsync(); await arrived.Task;
        Assert.Equal(40, vm.ProgressValue); Assert.False(vm.StartCommand.CanExecute(null));
        await vm.StartAsync(); Assert.Equal(1, runtime.Prepared);
        vm.CancelCommand.Execute(null); await preparing;
        Assert.False(vm.IsBusy); Assert.Contains("зупинено", vm.Title);
        var title = vm.Title; late!.Report(new("Запізніла відповідь", "", 100));
        Assert.Equal(title, vm.Title);
    }

    [Fact]
    public void CorruptPackageHasAnActionableScreenAndCannotRunPreparation()
    {
        using var vm = new SetupViewModel(null, _ => throw new InvalidOperationException(), blockingError: "Завантажте пакет повторно.");
        Assert.True(vm.HasBlockingError); Assert.True(vm.HasError);
        vm.AcceptedTerms = true; Assert.False(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task ADeadlineIsAnErrorRatherThanAnExplicitUserCancellation()
    {
        var runtime = new Runtime { HasAcceptedTerms = true, Prepare = (_, _) => throw new OperationCanceledException() };
        using var vm = new SetupViewModel(runtime, _ => throw new InvalidOperationException());
        await vm.StartAsync();
        Assert.True(vm.HasError); Assert.Contains("Час очікування", vm.Error);
        Assert.True(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public void TermsConsentIsBoundToTheExactSqlImageAndCannotBeAssumedForAnotherPackage()
    {
        var folder = Path.Combine(Path.GetTempPath(), "kanban-consent-" + Guid.NewGuid().ToString("N"));
        try
        {
            var one = new RuntimeManifest { SqlImage = "sql-image-one" };
            var adapter = new SetupRuntimeAdapter(new LocalSetupCoordinator(one, folder), one);
            Assert.False(adapter.HasAcceptedTerms); adapter.AcceptTerms(); Assert.True(adapter.HasAcceptedTerms);
            var two = new RuntimeManifest { SqlImage = "sql-image-two" };
            Assert.False(new SetupRuntimeAdapter(new LocalSetupCoordinator(two, folder), two).HasAcceptedTerms);
            Assert.False(File.Exists(Path.Combine(folder, "installation.json")));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void MissingExplicitAddressKeepsPackagedOrDevelopmentStartup(string? value) =>
        Assert.Null(StartupConfiguration.ExplicitApiAddress(value));

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("file:///tmp")]
    [InlineData("http://name:secret@example.test/")]
    public void InvalidExplicitAddressReturnsAnActionableErrorWithoutEchoingInput(string value)
    {
        var error = Assert.Throws<SetupException>(() => StartupConfiguration.ExplicitApiAddress(value));
        Assert.Equal(SetupAction.None, error.Action);
        Assert.DoesNotContain(value, error.Message);
    }
}
