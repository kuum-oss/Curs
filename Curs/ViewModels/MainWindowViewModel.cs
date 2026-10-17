using System;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Curs.Services;

namespace Curs.ViewModels;

public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class ActionCommand(Action action, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) { if (CanExecute(parameter)) action(); }
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class MainWindowViewModel : ViewModelBase, IDisposable
{
    private string email = "", password = "", name = "", error = "", status = "";
    private bool isRegister, isBusy, rememberMe, revealPassword, hasSavedSession;
    private CancellationTokenSource? operation;
    public ClientServices Services { get; }
    public BoardWorkspaceViewModel Workspace { get; }
    public string Title => "KanbanBoard";
    public string Email { get => email; set { email = value; Notify(); } }
    public string Password { get => password; set { password = value; Notify(); } }
    public string Name { get => name; set { name = value; Notify(); } }
    public bool RememberMe
    {
        get => rememberMe;
        set
        {
            rememberMe = value; Services.Remembered.SetEnabled(value); Notify();
            if (!value) { hasSavedSession = false; Notify(nameof(CanRetrySavedSession)); }
            if (Services.Remembered.Error is {} message) Error = message;
        }
    }
    public bool RevealPassword { get => revealPassword; set { revealPassword = value; Notify(); Notify(nameof(PasswordVisibilityLabel)); } }
    public string PasswordVisibilityLabel => RevealPassword ? "Приховати пароль" : "Показати пароль";
    public bool CanRetrySavedSession => hasSavedSession && !IsBusy;
    public string Error { get => error; private set { error = value; Notify(); Notify(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public string Status { get => status; private set { status = value; Notify(); Notify(nameof(HasStatus)); } }
    public bool HasStatus => !string.IsNullOrWhiteSpace(Status);
    public bool IsRegister { get => isRegister; private set { isRegister = value; Notify(); Notify(nameof(FormTitle)); Notify(nameof(FormSubtitle)); Notify(nameof(SubmitLabel)); Notify(nameof(SwitchLabel)); } }
    public bool IsBusy { get => isBusy; private set { isBusy = value; Notify(); Notify(nameof(CanEdit)); Notify(nameof(CanRetrySavedSession)); SubmitCommand.Refresh(); SwitchCommand.Refresh(); CheckSessionCommand.Refresh(); RetrySavedSessionCommand.Refresh(); } }
    public bool CanEdit => !IsBusy;
    public bool IsSignedIn => Services.Session.User is not null;
    public bool ShowAuth => !IsSignedIn;
    public string UserName => Services.Session.User?.Name ?? "";
    public string UserEmail => Services.Session.User?.Email ?? "";
    public string FormTitle => IsRegister ? "Створити обліковий запис" : "Вхід до KanbanBoard";
    public string FormSubtitle => IsRegister ? "Почніть планувати та працювати разом із командою." : "Увійдіть, щоб працювати зі своїми дошками.";
    public string SubmitLabel => IsRegister ? "Зареєструватися" : "Увійти";
    public string SwitchLabel => IsRegister ? "Уже маєте обліковий запис? Увійти" : "Новий користувач? Зареєструватися";
    public ActionCommand SubmitCommand { get; }
    public ActionCommand SwitchCommand { get; }
    public ActionCommand LogoutCommand { get; }
    public ActionCommand CheckSessionCommand { get; }
    public ActionCommand RetrySavedSessionCommand { get; }
    public ActionCommand TogglePasswordCommand { get; }

    public MainWindowViewModel(ClientServices services)
    {
        Services = services;
        Workspace = new BoardWorkspaceViewModel(services);
        SubmitCommand = new ActionCommand(() => _ = SubmitAsync(), () => !IsBusy);
        SwitchCommand = new ActionCommand(() => { IsRegister = !IsRegister; Password = ""; RevealPassword = false; Name = ""; Error = ""; }, () => !IsBusy);
        LogoutCommand = new ActionCommand(Logout);
        CheckSessionCommand = new ActionCommand(() => _ = CheckSessionAsync(), () => !IsBusy && IsSignedIn);
        RetrySavedSessionCommand = new ActionCommand(() => _ = InitializeAsync(), () => CanRetrySavedSession);
        TogglePasswordCommand = new ActionCommand(() => RevealPassword = !RevealPassword);
        Services.Session.Changed += SessionChanged;
    }

    public Task SubmitAsync() => RunAsync(async token =>
    {
        Services.Remembered.Forget(); hasSavedSession = false;
        if (IsRegister) await Services.Auth.RegisterAsync(Email, Name, Password, token);
        else await Services.Auth.LoginAsync(Email, Password, token);
        await Workspace.Loading;
        if(Services.Session.PublicNotice is {} notice)Workspace.ShowSessionNotice(notice);
        if(Services.Remembered.Error is {} message)Workspace.ShowSessionNotice(message);
    });

    public Task InitializeAsync() => RunAsync(async token =>
    {
        if (IsSignedIn) return;
        var saved = Services.Remembered.Read();
        hasSavedSession = saved is not null;
        if (saved is null) { if (Services.Remembered.Error is {} message) Error = message; return; }
        rememberMe = true; Notify(nameof(RememberMe)); Email = saved.Email;
        try
        {
            await Services.Auth.RestoreAsync(saved.AccessToken, token);
            hasSavedSession = false;
            await Workspace.Loading;
        }
        catch (ApiClientException ex) when (ex.Status == 401)
        {
            Services.Remembered.Forget(); hasSavedSession = false;
            Status = "Збережений сеанс завершився. Увійдіть знову.";
        }
    });

    public Task CheckSessionAsync() => RunAsync(async token =>
    {
        await Services.Auth.MeAsync(token);
        Status = "Ви авторизовані. З’єднання із сервером працює.";
    });

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        Error = ""; Status = ""; IsBusy = true;
        using var cancellation = new CancellationTokenSource(); operation = cancellation;
        try { await action(cancellation.Token); }
        catch (ApiClientException exception)
        {
            Error = exception.Problem.Errors is { Count: > 0 } fields
                ? string.Join("\n", fields.Values.SelectMany(x => x).Distinct())
                : exception.Message;
        }
        catch (OperationCanceledException) { if (!cancellation.IsCancellationRequested) Error = "Сервер не відповів вчасно. Спробуйте ще раз."; }
        catch (HttpRequestException) { Error = "Не вдалося підключитися до сервера. Перевірте його запуск."; }
        catch (Exception) { Error = "Не вдалося виконати дію. Спробуйте ще раз."; }
        finally { Password = ""; RevealPassword = false; operation = null; IsBusy = false; }
    }

    public void Logout()
    {
        operation?.Cancel(); Services.Remembered.Forget(); hasSavedSession = false; Services.Auth.Logout(); ClearFields();
    }
    private void ClearFields() { Email = ""; Name = ""; Password = ""; RevealPassword = false; Error = ""; Status = ""; IsRegister = false; }
    private void SessionChanged()
    {
        ClearFields(); Status=Services.Session.PublicNotice??""; Notify(nameof(IsSignedIn)); Notify(nameof(ShowAuth)); Notify(nameof(UserName)); Notify(nameof(UserEmail));
        CheckSessionCommand.Refresh();
    }
    public void Dispose() { Workspace.Dispose(); operation?.Cancel(); Services.Session.Changed -= SessionChanged; }
}
