using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Curs.Services;
using KanbanContracts;

namespace Curs.ViewModels;

public sealed class CardItemViewModel
{
    public CardSummaryDto Card { get; }
    public string Title => Card.Title;
    public string Summary => $"{Card.Priority} · {Card.DueDate?.ToString("yyyy-MM-dd") ?? "Без дедлайну"}\n{Card.Assignee?.Name ?? "Без відповідального"}";
    public string Tags => string.Join(" · ", Card.Tags.Select(t => t.Name));
    public ActionCommand OpenCommand { get; }
    public CardItemViewModel(CardSummaryDto card, BoardContentViewModel workspace)
    { Card = card; OpenCommand = new ActionCommand(() => _ = workspace.OpenCardAsync(card.Id), () => !workspace.IsBusy); }
}
public sealed class ColumnItemViewModel
{
    public ColumnDto Column { get; }
    public string Name => Column.Name;
    public ObservableCollection<CardItemViewModel> Cards { get; } = [];
    public ActionCommand SelectCommand { get; }
    public ActionCommand AddCommand { get; }
    private readonly BoardContentViewModel workspace;
    public bool CanCreate => workspace.CanCreate;
    public ColumnItemViewModel(ColumnDto col, BoardContentViewModel workspace)
    {
        Column = col; this.workspace = workspace;
        SelectCommand = new ActionCommand(() => workspace.SelectColumn(this), () => !workspace.IsBusy);
        AddCommand = new ActionCommand(() => workspace.NewCard(this), () => workspace.CanCreate && !workspace.IsBusy);
    }
    public void Refresh() { SelectCommand.Refresh(); AddCommand.Refresh(); foreach (var c in Cards) c.OpenCommand.Refresh(); }
}
public sealed class TagChoiceViewModel(TagDto tag) : ViewModelBase
{
    private bool selected;
    public TagDto Tag { get; } = tag;
    public string Name => Tag.Name;
    public bool Selected { get => selected; set { selected = value; Notify(); } }
}
public sealed record AssigneeOption(Guid? Id, string Name);
public sealed record CommentItemViewModel(CommentDto Comment)
{
    public string Heading => $"{Comment.Author.Name} · {Comment.CreatedAtUtc.ToLocalTime():dd.MM.yyyy HH:mm}";
    public string Text => Comment.Text;
}

public sealed class BoardContentViewModel : ViewModelBase, IDisposable
{
    private readonly ClientServices services;
    private CancellationTokenSource? operation;
    private BoardDetailsDto? board;
    private ColumnDto? selectedColumn;
    private CardDetailsDto? selectedCard;
    private bool busy, visible, editor;
    private string error = "", status = "", newColumnName = "", columnName = "", title = "", description = "", dueDate = "", commentText = "", tagName = "", tagColor = "#64748B";
    private string? pendingDelete;
    private CardPriority priority = CardPriority.Medium;
    private AssigneeOption? assignee;
    public ObservableCollection<ColumnItemViewModel> Columns { get; } = [];
    public ObservableCollection<TagChoiceViewModel> Tags { get; } = [];
    public ObservableCollection<AssigneeOption> Assignees { get; } = [];
    public ObservableCollection<CommentItemViewModel> Comments { get; } = [];
    public bool IsVisible { get => visible; private set { visible = value; Notify(); } }
    public bool IsBusy { get => busy; private set { busy = value; Notify(); Notify(nameof(CanEdit)); RefreshCommands(); } }
    public bool CanEdit => !IsBusy;
    public bool CanCreate => board is not null && (board.Role == BoardRole.Admin || board.CanCreateCards);
    public bool CanDeleteCard => selectedCard is not null && board is not null && (board.Role == BoardRole.Admin || board.CanDeleteCards);
    public bool CanDeleteColumn => selectedColumn is not null && board is not null && (board.Role == BoardRole.Admin || board.CanDeleteCards || !Columns.SelectMany(c => c.Cards).Any(c => c.Card.ColumnId == selectedColumn.Id));
    public bool HasColumn => selectedColumn is not null;
    public bool EditorVisible { get => editor; private set { editor = value; Notify(); } }
    public bool ExistingCard => selectedCard is not null;
    public string EditorHeading => selectedCard is null ? "Нова картка" : "Редагування картки";
    public bool ShowConfirmation => pendingDelete is not null;
    public string ConfirmationText => pendingDelete == "column" ? "Видалити колонку разом з її картками, коментарями та фото?" : "Видалити картку з її коментарями та фото?";
    public string Error { get => error; private set { error = value; Notify(); Notify(nameof(HasError)); } }
    public bool HasError => Error.Length != 0;
    public string Status { get => status; private set { status = value; Notify(); } }
    public string NewColumnName { get => newColumnName; set { newColumnName = value; Notify(); } }
    public string ColumnName { get => columnName; set { columnName = value; Notify(); } }
    public string Title { get => title; set { title = value; Notify(); } }
    public string Description { get => description; set { description = value; Notify(); } }
    public string DueDate { get => dueDate; set { dueDate = value; Notify(); } }
    public CardPriority Priority { get => priority; set { priority = value; Notify(); } }
    public CardPriority[] Priorities { get; } = Enum.GetValues<CardPriority>();
    public AssigneeOption? Assignee { get => assignee; set { assignee = value; Notify(); } }
    public string CommentText { get => commentText; set { commentText = value; Notify(); } }
    public string TagName { get => tagName; set { tagName = value; Notify(); } }
    public string TagColor { get => tagColor; set { tagColor = value; Notify(); } }
    public ActionCommand RefreshCommand { get; }
    public ActionCommand CreateColumnCommand { get; }
    public ActionCommand RenameColumnCommand { get; }
    public ActionCommand DeleteColumnCommand { get; }
    public ActionCommand SaveCardCommand { get; }
    public ActionCommand DeleteCardCommand { get; }
    public ActionCommand CloseEditorCommand { get; }
    public ActionCommand CreateTagCommand { get; }
    public ActionCommand AddCommentCommand { get; }
    public ActionCommand ConfirmCommand { get; }
    public ActionCommand CancelCommand { get; }

    public BoardContentViewModel(ClientServices services)
    {
        this.services = services;
        RefreshCommand = Command(RefreshAsync);
        CreateColumnCommand = Command(CreateColumnAsync);
        RenameColumnCommand = Command(RenameColumnAsync, () => HasColumn);
        DeleteColumnCommand = new ActionCommand(() => RequestDelete("column"), () => !IsBusy && CanDeleteColumn);
        SaveCardCommand = Command(SaveCardAsync, () => EditorVisible && (ExistingCard || CanCreate));
        DeleteCardCommand = new ActionCommand(() => RequestDelete("card"), () => !IsBusy && CanDeleteCard);
        CloseEditorCommand = new ActionCommand(CloseEditor, () => !IsBusy);
        CreateTagCommand = Command(CreateTagAsync);
        AddCommentCommand = Command(AddCommentAsync, () => ExistingCard);
        ConfirmCommand = Command(ConfirmAsync, () => ShowConfirmation);
        CancelCommand = new ActionCommand(CancelConfirmation, () => !IsBusy);
        services.Session.Changed += SessionChanged;
    }
    private ActionCommand Command(Func<Task> action, Func<bool>? allowed = null) => new(() => _ = action(), () => !IsBusy && IsVisible && (allowed?.Invoke() ?? true));
    private void SessionChanged() { if (services.Session.AccessToken is null) Clear(); }
    public Task OpenAsync(Guid id) => Run(async token => { ClearData(); await Load(id, token); IsVisible = true; });
    public Task RefreshAsync() => Run(async token => { if (board is not null) await Load(board.Id, token); });
    private async Task Load(Guid id, CancellationToken token)
    {
        var metadata = await services.Boards.GetAsync(id, token);
        var columns = await services.Content.Columns(id, token);
        var tags = await services.Content.Tags(id, token);
        var members = await services.Boards.MembersAsync(id, token);
        var cards = new System.Collections.Generic.List<CardSummaryDto>();
        PagedResult<CardSummaryDto> page;
        do
        {
            page = await services.Content.Cards(id, cards.Count, token);
            if (page.BoardVersion != metadata.Version || (page.Items.Count == 0 && cards.Count < page.TotalCount)) throw Conflict();
            cards.AddRange(page.Items);
        } while (cards.Count < page.TotalCount);
        var latest = await services.Boards.GetAsync(id, token); token.ThrowIfCancellationRequested();
        if (latest.Version != metadata.Version) throw Conflict();
        var tagIds = Tags.Where(t => t.Selected).Select(t => t.Tag.Id).ToHashSet(); var assigneeId = Assignee?.Id;
        board = metadata; Columns.Clear();
        foreach (var col in columns)
        {
            var row = new ColumnItemViewModel(col, this); foreach (var card in cards.Where(c => c.ColumnId == col.Id)) row.Cards.Add(new CardItemViewModel(card, this)); Columns.Add(row);
        }
        if (selectedColumn is not null && columns.All(c => c.Id != selectedColumn.Id)) selectedColumn = null;
        Tags.Clear(); foreach (var t in tags) Tags.Add(new TagChoiceViewModel(t) { Selected = tagIds.Contains(t.Id) });
        Assignees.Clear(); Assignees.Add(new(null, "Без відповідального")); foreach (var member in members) Assignees.Add(new(member.User.Id, member.User.Name));
        Assignee = Assignees.FirstOrDefault(a => a.Id == assigneeId) ?? Assignees[0]; NotifyState();
    }
    private static ApiClientException Conflict() => new(409, new ApiProblem { Code = "concurrency_conflict", Title = "Дошку змінено під час завантаження. Оновіть дані." });
    public void SelectColumn(ColumnItemViewModel row) { if (IsBusy) return; CloseEditor(); selectedColumn = row.Column; ColumnName = row.Name; NotifyState(); }
    public Task CreateColumnAsync() => Run(async token =>
    {
        if (board is null) return;
        await services.Content.CreateColumn(board.Id, new() { Name = NewColumnName, ExpectedBoardVersion = board.Version }, token);
        token.ThrowIfCancellationRequested(); NewColumnName = ""; await Load(board.Id, token); Status = "Колонку створено.";
    });
    public Task RenameColumnAsync() => Run(async token =>
    {
        if (board is null || selectedColumn is null) return;
        var updated = await services.Content.UpdateColumn(board.Id, selectedColumn.Id, new() { Name = ColumnName, ExpectedBoardVersion = board.Version, ExpectedVersion = selectedColumn.Version }, token);
        token.ThrowIfCancellationRequested(); selectedColumn = updated;
        await Load(board.Id, token); Status = "Колонку перейменовано.";
    });
    public void NewCard(ColumnItemViewModel row)
    {
        if (IsBusy || !CanCreate) return; CloseEditor(); selectedColumn = row.Column; ColumnName = row.Name;
        Title = ""; Description = ""; DueDate = ""; Priority = CardPriority.Medium; Assignee = Assignees.FirstOrDefault();
        foreach (var tag in Tags) tag.Selected = false; EditorVisible = true; NotifyState();
    }
    public Task OpenCardAsync(Guid id) => Run(async token =>
    {
        var card = await services.Content.Card(id, token); var comments = await GetComments(id, token); token.ThrowIfCancellationRequested();
        selectedCard = card; selectedColumn = Columns.Single(c => c.Column.Id == card.Card.ColumnId).Column; ColumnName = selectedColumn.Name;
        Title = card.Card.Title; Description = card.Description ?? ""; DueDate = card.Card.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? ""; Priority = card.Card.Priority;
        Assignee = Assignees.FirstOrDefault(a => a.Id == card.Card.Assignee?.Id) ?? Assignees.FirstOrDefault();
        foreach (var tag in Tags) tag.Selected = card.Card.Tags.Any(t => t.Id == tag.Tag.Id);
        Comments.Clear(); foreach (var c in comments) Comments.Add(new(c)); CommentText = ""; EditorVisible = true; CancelConfirmation(); NotifyState();
    });
    private async Task<System.Collections.Generic.List<CommentDto>> GetComments(Guid card, CancellationToken token)
    {
        var result = new System.Collections.Generic.List<CommentDto>(); PagedResult<CommentDto> page;
        do { page = await services.Content.Comments(card, result.Count, token); if (page.Items.Count == 0) break; result.AddRange(page.Items); } while (result.Count < page.TotalCount);
        return result;
    }
    public Task SaveCardAsync() => Run(async token =>
    {
        if (board is null || selectedColumn is null || !EditorVisible) return;
        DateOnly? date = null;
        if (!string.IsNullOrWhiteSpace(DueDate))
        {
            if (!DateOnly.TryParseExact(DueDate.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) { Error = "Дедлайн: введіть дату у форматі РРРР-ММ-ДД або залиште порожнім."; return; }
            date = parsed;
        }
        var tags = Tags.Where(t => t.Selected).Select(t => t.Tag.Id).ToList();
        CardDetailsDto? saved;
        if (selectedCard is null)
        {
            if (!CanCreate) return;
            saved = await services.Content.CreateCard(board.Id, new() { ColumnId = selectedColumn.Id, Title = Title, Description = Description, DueDate = date, Priority = Priority, AssigneeId = Assignee?.Id, TagIds = tags, ExpectedBoardVersion = board.Version }, token);
        }
        else saved = await services.Content.UpdateCard(selectedCard.Card.Id, new() { Title = Title, Description = Description, DueDate = date, Priority = Priority, AssigneeId = Assignee?.Id, TagIds = tags, ExpectedBoardVersion = board.Version, ExpectedVersion = selectedCard.Card.Version }, token);
        token.ThrowIfCancellationRequested(); selectedCard = saved; await Load(board.Id, token); NotifyState(); Status = "Картку збережено.";
    });
    public Task CreateTagAsync() => Run(async token =>
    {
        if (board is null) return;
        var tag = await services.Content.CreateTag(board.Id, new() { Name = TagName, Color = TagColor, ExpectedBoardVersion = board.Version }, token);
        token.ThrowIfCancellationRequested(); TagName = ""; await Load(board.Id, token);
        if (tag is not null) Tags.Single(x => x.Tag.Id == tag.Id).Selected = true;
        Status = "Тег створено. Збережіть картку, щоб застосувати вибрані теги.";
    });
    public Task AddCommentAsync() => Run(async token =>
    {
        if (board is null || selectedCard is null) return;
        await services.Content.AddComment(selectedCard.Card.Id, new() { Text = CommentText, ExpectedBoardVersion = board.Version, ExpectedVersion = selectedCard.Card.Version }, token);
        var card = await services.Content.Card(selectedCard.Card.Id, token); var comments = await GetComments(card.Card.Id, token); token.ThrowIfCancellationRequested();
        selectedCard = card; CommentText = ""; Comments.Clear(); foreach (var c in comments) Comments.Add(new(c));
        await Load(board.Id, token); NotifyState(); Status = "Коментар додано.";
    });
    private void RequestDelete(string kind) { if (IsBusy || (kind == "card" ? !CanDeleteCard : !CanDeleteColumn)) return; pendingDelete = kind; NotifyState(); }
    public void CancelConfirmation() { pendingDelete = null; NotifyState(); }
    public Task ConfirmAsync() => Run(async token =>
    {
        if (board is null || pendingDelete is null) return;
        if (pendingDelete == "column" && selectedColumn is not null && CanDeleteColumn)
            await services.Content.DeleteColumn(board.Id, selectedColumn.Id, new() { ExpectedBoardVersion = board.Version, ExpectedVersion = selectedColumn.Version }, token);
        else if (pendingDelete == "card" && selectedCard is not null && CanDeleteCard)
            await services.Content.DeleteCard(selectedCard.Card.Id, new() { ExpectedBoardVersion = board.Version, ExpectedVersion = selectedCard.Card.Version }, token);
        else return;
        token.ThrowIfCancellationRequested(); CloseEditor(); selectedColumn = null; await Load(board.Id, token); NotifyState(); Status = "Видалення завершено.";
    });
    public void CloseEditor() { selectedCard = null; Comments.Clear(); CommentText = ""; EditorVisible = false; pendingDelete = null; NotifyState(); }
    private async Task Run(Func<CancellationToken, Task> action)
    {
        if (IsBusy || services.Session.AccessToken is null) return;
        using var cancellation = new CancellationTokenSource(); operation = cancellation; IsBusy = true; Error = ""; Status = "";
        try { await action(cancellation.Token); }
        catch (ApiClientException ex)
        {
            if (cancellation.IsCancellationRequested) return;
            Error = ex.Problem.Errors is { Count: > 0 } fields ? string.Join("\n", fields.Values.SelectMany(v => v).Distinct()) : ex.Message;
            if (ex.Status == 409 && ex.Problem.Code == "concurrency_conflict" && board is not null)
            {
                try
                {
                    await Load(board.Id, cancellation.Token);
                    if (selectedColumn is not null) selectedColumn = Columns.FirstOrDefault(c => c.Column.Id == selectedColumn.Id)?.Column;
                    if (selectedCard is not null)
                    {
                        var latest = await services.Content.Card(selectedCard.Card.Id, cancellation.Token);
                        cancellation.Token.ThrowIfCancellationRequested(); selectedCard = latest;
                    }
                    cancellation.Token.ThrowIfCancellationRequested(); NotifyState();
                }
                catch (OperationCanceledException) { }
                catch (ApiClientException recovery) when (recovery.Status == 404) { ClearData(); Error = "Картку або дошку видалено чи доступ відкликано."; }
                catch (Exception) { if (!cancellation.IsCancellationRequested) Error += "\nНе вдалося оновити дані. Перевірте підключення."; }
            }
            else if (ex.Status == 404 && ex.Problem.Code == "not_found") { ClearData(); Error = ex.Message; }
        }
        catch (OperationCanceledException) { if (!cancellation.IsCancellationRequested) Error = "Сервер не відповів вчасно."; }
        catch (HttpRequestException) { if (!cancellation.IsCancellationRequested) Error = "Не вдалося підключитися до сервера."; }
        catch (Exception) { if (!cancellation.IsCancellationRequested) Error = "Не вдалося виконати дію. Оновіть дошку."; }
        finally { if (ReferenceEquals(operation, cancellation)) { operation = null; IsBusy = false; } }
    }
    private void NotifyState()
    {
        foreach (var name in new[] { nameof(CanCreate), nameof(CanDeleteCard), nameof(CanDeleteColumn), nameof(HasColumn), nameof(ExistingCard), nameof(EditorHeading), nameof(ShowConfirmation), nameof(ConfirmationText) }) Notify(name);
        RefreshCommands();
    }
    private void RefreshCommands()
    {
        foreach (var command in new[] { RefreshCommand, CreateColumnCommand, RenameColumnCommand, DeleteColumnCommand, SaveCardCommand, DeleteCardCommand, CloseEditorCommand, CreateTagCommand, AddCommentCommand, ConfirmCommand, CancelCommand }) command.Refresh();
        foreach (var col in Columns) col.Refresh();
    }
    private void ClearData()
    {
        board = null; selectedColumn = null; selectedCard = null; Columns.Clear(); Tags.Clear(); Assignees.Clear(); Comments.Clear();
        NewColumnName = ""; ColumnName = ""; Title = ""; Description = ""; DueDate = ""; CommentText = ""; TagName = ""; TagColor = "#64748B"; Priority = CardPriority.Medium; Assignee = null;
        EditorVisible = false; IsVisible = false; pendingDelete = null; NotifyState();
    }
    public void Clear() { operation?.Cancel(); operation = null; IsBusy = false; ClearData(); Error = ""; Status = ""; }
    public void Dispose() { Clear(); services.Session.Changed -= SessionChanged; }
}
