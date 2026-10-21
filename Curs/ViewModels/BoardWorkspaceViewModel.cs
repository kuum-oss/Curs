using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Curs.Services;
using KanbanContracts;

namespace Curs.ViewModels;

public sealed partial class BoardListItemViewModel : ViewModelBase, IDisposable
{
    public BoardSummaryDto Board { get; }
    public string Name => Board.Name;
    public string Description => Board.Description ?? "Без опису";
    public string Role => Board.Role == BoardRole.Admin ? "Адміністратор" : "Учасник";
    public ActionCommand OpenCommand { get; }
    public BoardListItemViewModel(BoardSummaryDto board, BoardWorkspaceViewModel workspace)
    {
        Board = board; InitializeTile(workspace); OpenCommand = new ActionCommand(() => _ = workspace.OpenVisualAsync(board.Id), () => !workspace.IsBusy);
    }
}

public sealed partial class MemberViewModel : ViewModelBase
{
    private readonly BoardWorkspaceViewModel workspace;
    private bool create, delete;
    public MemberDto Member { get; }
    public string Name => Member.User.Name;
    public string Email => Member.User.Email;
    public string Role => Member.Role == BoardRole.Admin ? "Адміністратор" : "Учасник";
    public bool CanManage => workspace.IsAdmin && Member.User.Id != workspace.SelectedBoard?.OwnerId && (Member.Role != BoardRole.Admin || workspace.SelectedBoard?.OwnerId==workspace.CurrentUser?.Id);
    public bool CanEdit => CanManage && !workspace.IsBusy;
    public bool CanCreateCards { get => create; set { create = value; Notify(); } }
    public bool CanDeleteCards { get => delete; set { delete = value; Notify(); } }
    public ActionCommand SaveCommand { get; }
    public ActionCommand RemoveCommand { get; }
    public MemberViewModel(MemberDto member, BoardWorkspaceViewModel workspace)
    {
        this.workspace = workspace; Member = member; InitializeMember(); create = member.CanCreateCards; delete = member.CanDeleteCards;
        SaveCommand = new ActionCommand(() => _ = workspace.SavePermissionsAsync(this), () => CanEdit);
        RemoveCommand = new ActionCommand(() => workspace.RequestRemove(this), () => CanEdit);
    }
    public void Refresh() { Notify(nameof(CanEdit)); Notify(nameof(CanChangeRole)); Notify(nameof(CanChangeRights)); SaveCommand.Refresh(); RemoveCommand.Refresh(); }
}

public sealed partial class BoardWorkspaceViewModel : ViewModelBase, IDisposable
{
    private readonly ClientServices services;
    private CancellationTokenSource? operation;
    private BoardDetailsDto? selected;
    private MemberViewModel? pendingMember;
    private bool busy, confirmDelete;
    private string error = "", status = "", newName = "", newDescription = "", editName = "", editDescription = "", memberEmail = "";
    public ObservableCollection<BoardListItemViewModel> Boards { get; } = [];
    public ObservableCollection<MemberViewModel> Members { get; } = [];
    public BoardContentViewModel Content { get; }
    public BoardInsightsViewModel Insights { get; private set; } = null!;
    public ActionCommand OpenContentCommand { get; }
    public BoardDetailsDto? SelectedBoard => selected;
    public bool ShowList => selected is null;
    public bool ShowBoard => selected is not null;
    public bool IsAdmin => selected?.Role == BoardRole.Admin;
    public bool IsBusy { get => busy; private set { busy = value; Notify(); Notify(nameof(CanEdit)); RefreshCommands(); } }
    public bool CanEdit => !IsBusy;
    public bool HasBoards => Boards.Count != 0;
    public bool IsEmpty => Boards.Count == 0 && !IsBusy;
    public string BoardTitle => selected?.Name ?? "";
    public string BoardDescription => selected?.Description ?? "Без опису";
    public string RoleLabel => IsAdmin ? "Адміністратор" : "Учасник";
    public string CardPermissions => selected is null ? "" : $"Картки: створення {(selected.CanCreateCards ? "дозволено" : "обмежено")}, видалення {(selected.CanDeleteCards ? "дозволено" : "обмежено")}.";
    public string NewName { get => newName; set { newName = value; Notify(); } }
    public string NewDescription { get => newDescription; set { newDescription = value; Notify(); } }
    public string EditName { get => editName; set { editName = value; Notify(); } }
    public string EditDescription { get => editDescription; set { editDescription = value; Notify(); } }
    public string MemberEmail { get => memberEmail; set { memberEmail = value; Notify(); } }
    public string Error { get => error; private set { error = value; Notify(); Notify(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrEmpty(Error);
    public string Status { get => status; private set { status = value; Notify(); } }
    public bool ShowConfirmation => confirmDelete || pendingMember is not null;
    public string ConfirmationText => confirmDelete ? "Видалити дошку разом з усіма її картками, коментарями та фото?" : $"Відкликати доступ для {pendingMember?.Name}? Призначення відповідального на цій дошці буде прибрано.";
    public Task Loading { get; private set; } = Task.CompletedTask;
    public ActionCommand RefreshCommand { get; }
    public ActionCommand CreateCommand { get; }
    public ActionCommand SaveBoardCommand { get; }
    public ActionCommand AddMemberCommand { get; }
    public ActionCommand BackCommand { get; }
    public ActionCommand DeleteCommand { get; }
    public ActionCommand ConfirmCommand { get; }
    public ActionCommand CancelCommand { get; }

    public BoardWorkspaceViewModel(ClientServices services)
    {
        this.services = services;
        Content = new BoardContentViewModel(services);
        OpenContentCommand = new ActionCommand(() => { if (selected is not null) _ = Content.OpenAsync(selected.Id); }, () => !IsBusy && selected is not null);
        RefreshCommand = new ActionCommand(() => _ = RefreshAsync(), () => !IsBusy);
        CreateCommand = new ActionCommand(() => _ = CreateAsync(), () => !IsBusy && ShowList);
        SaveBoardCommand = new ActionCommand(() => _ = SaveBoardAsync(), () => !IsBusy && IsAdmin);
        AddMemberCommand = new ActionCommand(() => _ = AddMemberAsync(), () => !IsBusy && IsAdmin);
        BackCommand = new ActionCommand(Back, () => !IsBusy);
        DeleteCommand = new ActionCommand(RequestDelete, () => !IsBusy && IsAdmin);
        ConfirmCommand = new ActionCommand(() => _ = ConfirmAsync(), () => !IsBusy && ShowConfirmation);
        CancelCommand = new ActionCommand(CancelConfirmation, () => !IsBusy);
        services.Session.Changed += SessionChanged; services.Navigation.Changed += NavigationChanged; InitializePresentation(); Notifications=new(services,NavigateNotification);
    }

    private void NavigationChanged()
    {
        if (services.Navigation.CurrentScreen == AppScreen.Boards && services.Session.AccessToken is not null)
            Loading = LoadAsync();
    }
    private void SessionChanged() { if (services.Session.AccessToken is null) Clear(); RefreshIdentity(); }
    public Task LoadAsync() => RunAsync(LoadCoreAsync);
    public Task RefreshAsync() => selected is null ? LoadAsync() : RunAsync(token => OpenCoreAsync(selected.Id, token));
    private async Task LoadCoreAsync(CancellationToken token)
    {
        var boards = await services.Boards.ListAsync(token); token.ThrowIfCancellationRequested();
        foreach(var old in Boards)old.Dispose(); Boards.Clear(); foreach (var b in boards) Boards.Add(new BoardListItemViewModel(b, this));
        Notify(nameof(HasBoards)); Notify(nameof(IsEmpty)); RefreshTiles();
    }
    public Task OpenAsync(Guid id) => RunAsync(token => OpenCoreAsync(id, token));
    private async Task OpenCoreAsync(Guid id, CancellationToken token, bool preserveDraft = false)
    {
        var board = await services.Boards.GetAsync(id, token);
        var members = await services.Boards.MembersAsync(id, token); token.ThrowIfCancellationRequested();
        if (selected?.Id != id) Content.Clear();
        selected = board; foreach(var old in Members)old.Avatar.Dispose(); Members.Clear(); foreach (var m in members) Members.Add(new MemberViewModel(m, this));
        if (!preserveDraft) { EditName = board.Name; EditDescription = board.Description ?? ""; }
        NotifyBoard(); services.Navigation.OpenBoard(id);
    }
    public Task CreateAsync() => RunAsync(async token =>
    {
        var board = await services.Boards.CreateAsync(new CreateBoardRequest { Name = NewName, Description = NewDescription }, token);
        token.ThrowIfCancellationRequested(); NewName = ""; NewDescription = "";
        await OpenCoreAsync(board.Id, token); NewBoardVisible=false; Page="board"; await Content.OpenAsync(board.Id); await LoadCoreAsync(token); Status = "Дошку створено.";
    });
    public Task SaveBoardAsync() => RunAsync(async token =>
    {
        if (selected is null || !IsAdmin) return;
        await services.Boards.UpdateAsync(selected.Id, new UpdateBoardRequest { Name = EditName, Description = EditDescription, ExpectedBoardVersion = selected.Version }, token);
        await OpenCoreAsync(selected.Id, token); SettingsVisible=false; await Content.RefreshAsync(); await LoadCoreAsync(token); Status = "Зміни збережено.";
    });
    public Task AddMemberAsync() => RunAsync(async token =>
    {
        if (selected is null || !IsAdmin) return;
        var draft=MemberDraft();
        await services.Boards.AddMemberAsync(selected.Id, new AddMemberRequest { Email = MemberEmail.Trim(), ExpectedBoardVersion = selected.Version }, token);
        token.ThrowIfCancellationRequested(); MemberEmail = ""; await OpenCoreAsync(selected.Id, token); RestoreMemberDraft(draft); foreach(var row in Members)_=row.Avatar.Load(services); Status = "Учаснику надано доступ.";
    });
    public Task SavePermissionsAsync(MemberViewModel member) => RunAsync(async token =>
    {
        if (selected is null || !member.CanManage) return;
        await services.Boards.PermissionsAsync(selected.Id, member.Member.User.Id, new UpdateMemberPermissionsRequest
            { CanCreateCards = member.CanCreateCards, CanDeleteCards = member.CanDeleteCards, ExpectedBoardVersion = selected.Version }, token);
        await OpenCoreAsync(selected.Id, token); Status = "Дозволи учасника оновлено.";
    });
    public void RequestDelete() { if (!IsAdmin || IsBusy) return; confirmDelete = true; pendingMember = null; NotifyConfirmation(); }
    public void RequestRemove(MemberViewModel member) { if (!member.CanEdit) return; pendingMember = member; confirmDelete = false; NotifyConfirmation(); }
    public void CancelConfirmation() { confirmDelete = false; pendingMember = null; NotifyConfirmation(); }
    public Task ConfirmAsync() => RunAsync(async token =>
    {
        if (selected is null || !IsAdmin || !ShowConfirmation) return;
        if (confirmDelete)
        {
            await services.Boards.DeleteAsync(selected.Id, selected.Version, token); token.ThrowIfCancellationRequested();
            Content.Clear(); selected = null; Members.Clear(); CloseDialogs(); Page="boards"; CancelConfirmation(); NotifyBoard();
            await LoadCoreAsync(token); services.Navigation.OpenBoards(); Status = "Дошку видалено.";
        }
        else if (pendingMember is not null)
        {
            var draft=MemberDraft();
            await services.Boards.RemoveMemberAsync(selected.Id, pendingMember.Member.User.Id, selected.Version, token);
            token.ThrowIfCancellationRequested(); CancelConfirmation(); await OpenCoreAsync(selected.Id, token); RestoreMemberDraft(draft); foreach(var row in Members)_=row.Avatar.Load(services); Status = "Доступ відкликано.";
        }
    });
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy || services.Session.AccessToken is null) return;
        Error = ""; Status = ""; IsBusy = true;
        using var cancellation = new CancellationTokenSource(); operation = cancellation;
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { if (!cancellation.IsCancellationRequested) Error = "Сервер не відповів вчасно. Спробуйте ще раз."; }
        catch (ApiClientException ex)
        {
            if (cancellation.IsCancellationRequested) return;
            Error = ex.Problem.Errors is { Count: > 0 } fields ? string.Join("\n", fields.Values.SelectMany(x => x).Distinct()) : ex.Message;
            if (ex.Status == 409 && ex.Problem.Code == "concurrency_conflict" && selected is not null)
                await RecoverAsync(true, cancellation.Token);
            else if (ex.Status == 404 && ex.Problem.Code == "not_found")
                await RecoverAsync(false, cancellation.Token);
        }
        catch (HttpRequestException) { if (!cancellation.IsCancellationRequested) Error = "Не вдалося підключитися до сервера."; }
        catch (Exception) { if (!cancellation.IsCancellationRequested) Error = "Не вдалося виконати дію. Оновіть дані та спробуйте ще раз."; }
        finally
        {
            if (ReferenceEquals(operation, cancellation)) { operation = null; IsBusy = false; Notify(nameof(IsEmpty)); }
        }
    }
    private async Task RecoverAsync(bool conflict, CancellationToken token)
    {
        CancelConfirmation();
        try
        {
            if (conflict && selected is not null) await OpenCoreAsync(selected.Id, token, preserveDraft: true);
            else await LostAccessAsync(token);
        }
        catch (ApiClientException ex) when (ex.Status == 404)
        {
            selected = null; Members.Clear(); EditName = ""; EditDescription = ""; MemberEmail = ""; NotifyBoard();
            Boards.Clear(); Notify(nameof(HasBoards)); Notify(nameof(IsEmpty));
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!token.IsCancellationRequested) Error += "\nНе вдалося оновити дані. Перевірте підключення."; }
    }
    private async Task LostAccessAsync(CancellationToken token)
    {
        Content.Clear();
        selected = null; Members.Clear(); CloseDialogs(); Page="boards"; EditName = ""; EditDescription = ""; MemberEmail = ""; CancelConfirmation(); NotifyBoard();
        await LoadCoreAsync(token); services.Navigation.OpenBoards();
    }
    private void Back()
    {
        Content.Clear();
        selected = null; Members.Clear(); EditName = ""; EditDescription = ""; MemberEmail = ""; Error = ""; Status = "";
        CancelConfirmation(); NotifyBoard(); services.Navigation.OpenBoards();
    }
    private void NotifyBoard()
    {
        foreach (var property in new[] { nameof(SelectedBoard), nameof(ShowList), nameof(ShowBoard), nameof(IsAdmin), nameof(BoardTitle), nameof(BoardDescription), nameof(RoleLabel), nameof(CardPermissions) }) Notify(property);
        RefreshCommands(); NotifyPresentation();
    }
    private void NotifyConfirmation() { Notify(nameof(ShowConfirmation)); Notify(nameof(ConfirmationText)); ConfirmCommand.Refresh(); }
    private void RefreshCommands()
    {
        SaveMembersCommand?.Refresh();
        foreach (var command in new[] { OpenContentCommand, RefreshCommand, CreateCommand, SaveBoardCommand, AddMemberCommand, BackCommand, DeleteCommand, ConfirmCommand, CancelCommand }) command.Refresh();
        foreach (var row in Members) row.Refresh(); foreach (var board in Boards) board.OpenCommand.Refresh();
    }
    public void Clear()
    {
        Insights?.Clear(); Notifications?.Clear(); Content.Clear();
        operation?.Cancel(); operation = null; IsBusy = false;
        foreach(var old in Boards)old.Dispose(); foreach(var old in Members)old.Avatar.Dispose(); Boards.Clear(); Members.Clear(); ResetPresentation(); selected = null; NewName = ""; NewDescription = ""; EditName = ""; EditDescription = ""; MemberEmail = "";
        Error = ""; Status = ""; CancelConfirmation(); NotifyBoard(); Notify(nameof(HasBoards)); Notify(nameof(IsEmpty));
    }
    public void Dispose() { Clear(); Insights.Dispose(); Notifications.Dispose(); DisposePresentation(); presentationLifetime.Cancel();presentationLifetime.Dispose(); Content.Dispose(); services.Session.Changed -= SessionChanged; services.Navigation.Changed -= NavigationChanged; }
}
