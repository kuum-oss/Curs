using System;
using System.Threading;
using System.Threading.Tasks;
using Curs.Services;
using Curs.Services.LocalSetup;

namespace Curs.ViewModels;

public sealed class SetupViewModel : ViewModelBase, IDisposable
{
    private readonly ISetupRuntime? runtime;
    private readonly Action<Action> dispatch;
    private readonly Action<Uri> ready;
    private CancellationTokenSource? operation;
    private bool busy, accepted, disposed;
    private string title = "Ваш робочий простір — за кілька кроків", detail = "Першого разу ми завантажимо компоненти й підготуємо локальну базу. Наступні запуски будуть швидшими.", error = "";
    private double? percent;
    private SetupAction action;

    public bool IsBusy { get => busy; private set { busy = value; Notify(); Notify(nameof(CanEdit)); RefreshCommands(); } }
    public bool CanEdit => !IsBusy;
    public bool AcceptedTerms { get => accepted; set { accepted = value; Notify(); RefreshCommands(); } }
    public bool NeedsConsent => runtime?.HasAcceptedTerms != true;
    public bool HasBlockingError => runtime is null;
    public string Title { get => title; private set { title = value; Notify(); } }
    public string Detail { get => detail; private set { detail = value; Notify(); } }
    public string Error { get => error; private set { error = value; Notify(); Notify(nameof(HasError)); } }
    public bool HasError => Error.Length > 0;
    public bool NeedsDockerInstall => action == SetupAction.InstallDocker;
    public bool NeedsDockerStart => action == SetupAction.StartDocker;
    public bool IsIndeterminate => percent is null;
    public double ProgressValue => percent ?? 0;
    public string ProgressLabel => percent is {} value ? $"{Math.Clamp(value, 0, 100):0}%" : "";
    public string StartLabel => HasError || action != SetupAction.None ? "Перевірити знову" : NeedsConsent ? "Підготувати й відкрити" : "Відкрити KanbanBoard";
    public ActionCommand StartCommand { get; }
    public ActionCommand InstallDockerCommand { get; }
    public ActionCommand StartDockerCommand { get; }
    public ActionCommand CancelCommand { get; }

    public SetupViewModel(ISetupRuntime? runtime, Action<Uri> ready, Action<Action>? dispatch = null, string? blockingError = null)
    {
        this.runtime = runtime; this.ready = ready; this.dispatch = dispatch ?? (a => a());
        accepted = runtime?.HasAcceptedTerms == true;
        StartCommand = new(() => _ = StartAsync(), () => !disposed && !IsBusy && runtime is not null && AcceptedTerms);
        InstallDockerCommand = new(() => _ = InstallDockerAsync(), () => !disposed && !IsBusy && runtime is not null && AcceptedTerms);
        StartDockerCommand = new(() => _ = StartDockerAsync(), () => !disposed && !IsBusy && runtime is not null);
        CancelCommand = new(() => operation?.Cancel(), () => IsBusy);
        if (blockingError is not null) { Error = blockingError; Title = "Не вдалося відкрити пакет"; Detail = "Завантажте повний інсталяційний пакет із розділу Releases та спробуйте ще раз."; }
    }

    public Task InitializeAsync() => runtime?.HasAcceptedTerms == true ? StartAsync() : Task.CompletedTask;
    public Task StartAsync() => Run(async (progress, token) =>
    {
        if (!AcceptedTerms || runtime is null) return;
        if (!runtime.HasAcceptedTerms) runtime.AcceptTerms();
        Notify(nameof(NeedsConsent));
        var result = await runtime.PrepareAsync(progress, token);
        token.ThrowIfCancellationRequested();
        if (!disposed) ready(result.ApiUri);
    });

    public Task InstallDockerAsync() => Run(async (progress, token) =>
    {
        if (runtime is null || !AcceptedTerms) return;
        await runtime.InstallDockerAsync(progress, token);
        Title = "Завершіть установлення Docker Desktop";
        Detail = "У вікні інсталятора завершіть установлення, відкрийте Docker Desktop та поверніться сюди. Потім натисніть «Перевірити знову». На Windows може знадобитися перезавантаження.";
        action = SetupAction.InstallDocker; NotifyActions();
    });

    public Task StartDockerAsync() => Run(async (_, token) =>
    {
        if (runtime is null) return;
        await runtime.StartDockerAsync(token);
        Title = "Docker Desktop запускається";
        Detail = "Дочекайтеся завершення запуску Docker Desktop та натисніть «Перевірити знову».";
        action = SetupAction.StartDocker; NotifyActions();
    });

    private async Task Run(Func<IProgress<SetupProgress>, CancellationToken, Task> work)
    {
        if (IsBusy || disposed || runtime is null) return;
        using var source = new CancellationTokenSource(); operation = source;
        Error = ""; IsBusy = true; SetPercent(null);
        var progress = new InlineProgress<SetupProgress>(p => dispatch(() =>
        {
            if (disposed || source.IsCancellationRequested || !ReferenceEquals(operation, source)) return;
            Title = p.Title; Detail = p.Detail; SetPercent(p.Percent);
        }));
        try { await work(progress, source.Token); }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { if (!disposed) { Title = "Підготовку зупинено"; Detail = "Ваші дані збережені. Підготовку можна продовжити кнопкою нижче."; action = SetupAction.Retry; } }
        catch (OperationCanceledException) { if (!disposed) { Error = "Час очікування вичерпано. Перевірте інтернет і Docker Desktop та повторіть спробу."; action = SetupAction.Retry; } }
        catch (SetupException e) { if (!disposed) { Error = e.Message; action = e.Action; } }
        catch (Exception) { if (!disposed) { Error = "Не вдалося завершити підготовку. Перевірте доступ до інтернету й вільне місце та спробуйте ще раз."; action = SetupAction.Retry; } }
        finally { operation = null; if (!disposed) { IsBusy = false; NotifyActions(); } }
    }
    private void SetPercent(double? value) { percent = value; Notify(nameof(ProgressValue)); Notify(nameof(IsIndeterminate)); Notify(nameof(ProgressLabel)); }
    private void NotifyActions() { Notify(nameof(NeedsDockerInstall)); Notify(nameof(NeedsDockerStart)); Notify(nameof(StartLabel)); }
    private void RefreshCommands() { StartCommand?.Refresh(); InstallDockerCommand?.Refresh(); StartDockerCommand?.Refresh(); CancelCommand?.Refresh(); Notify(nameof(StartLabel)); }
    public void Dispose() { disposed = true; operation?.Cancel(); }
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
}
