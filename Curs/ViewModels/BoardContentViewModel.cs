using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Curs.Services;
using KanbanContracts;
using Avalonia.Media.Imaging;
using System.IO;

namespace Curs.ViewModels;

public sealed partial class CardItemViewModel : ViewModelBase, IDisposable
{
    private Bitmap? thumbnail;
    public Bitmap? Thumbnail { get => thumbnail; internal set { thumbnail = value; Notify(); } }
    internal bool ImageRequested;
    public bool HasImage => Card.HasImage;
    public void Dispose() { var old = Thumbnail; Thumbnail = null; old?.Dispose(); Avatar?.Dispose(); }
    public CardSummaryDto Card { get; }
    public string Title => Card.Title;
    public string Summary => $"{Card.Priority} · {Card.DueDate?.ToString("yyyy-MM-dd") ?? "Без дедлайну"}\n{Card.Assignee?.Name ?? (Card.SourceAssigneeName is {} name?name+" · з копії (не призначено)":"Без відповідального")}";
    public string Tags => string.Join(" · ", Card.Tags.Select(t => t.Name));
    public ActionCommand OpenCommand { get; }
    public CardItemViewModel(CardSummaryDto card, BoardContentViewModel workspace)
    { Card = card; Avatar=card.Assignee is {} user?new(user):null; OpenCommand = new ActionCommand(() => _ = workspace.OpenCardAsync(card.Id), () => !workspace.IsBusy); }
}
public sealed partial class ColumnItemViewModel : ViewModelBase
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
        Column = col; this.workspace = workspace; Cards.CollectionChanged+=(_,_)=>Notify(nameof(Count));
        SelectCommand = new ActionCommand(() => workspace.SelectColumn(this), () => !workspace.IsBusy);
        AddCommand = new ActionCommand(() => workspace.NewCard(this), () => workspace.CanCreate && !workspace.IsBusy);
    }
    public void Refresh() { Notify(nameof(CanCreate)); SelectCommand.Refresh(); AddCommand.Refresh(); foreach (var c in Cards) c.OpenCommand.Refresh(); }
}
public sealed class TagChoiceViewModel : ViewModelBase
{
    private bool selected;
    public TagDto Tag { get; }
    public string Name => Tag.Name;
    public event Action? SelectionChanged;
    public bool Selected { get => selected; set { selected=value;Notify();SelectionChanged?.Invoke(); } }
    public ActionCommand RemoveCommand { get; }
    public Avalonia.Media.IBrush Background => TagPillViewModel.Pastel(Tag.Color);
    public Avalonia.Media.IBrush Foreground => Avalonia.Media.Brush.Parse(Tag.Color);
    public TagChoiceViewModel(TagDto tag){Tag=tag;RemoveCommand=new(()=>Selected=false);}
}
public sealed record AssigneeOption(Guid? Id, string Name, UserDto? User = null)
{
    public override string ToString()=>Name;
    public AvatarViewModel? Avatar { get; } = User is {} user?new(user):null;
    public bool HasAvatar=>Avatar is not null;
}
public sealed record PriorityFilterOption(CardPriority? Value, string Name){public override string ToString()=>Name;}
public sealed record DeadlineFilterOption(DeadlineFilter? Value, string Name){public override string ToString()=>Name;}
public sealed record TagFilterOption(Guid? Id, string Name){public override string ToString()=>Name;}

public sealed partial class BoardContentViewModel : ViewModelBase, IDisposable
{
    private CancellationTokenSource? filterDelay;
    private CancellationTokenSource imagesLifetime = new();
    private int filterRevision;
    private readonly Func<DateOnly> localToday;
    private DateOnly loadedFilterDay;
    private bool filtering, suppressFilters, transientError;
    private string search = "";
    private PriorityFilterOption? filterPriority;
    private DeadlineFilterOption? filterDeadline;
    private TagFilterOption? filterTag;
    private AssigneeOption? filterAssignee;
    private Bitmap? fullImage;
    public Bitmap? FullImage { get => fullImage; private set { var old=fullImage; fullImage=value; Notify(); old?.Dispose(); } }
    public Guid? EditingCardId => selectedCard?.Card.Id;
    public bool HasCover => selectedCard?.Card.HasImage == true;
    public bool CanChangeCover => ExistingCard && CanEdit && !polling;
    public bool CanFilter => !IsBusy || filtering;
    public string Search { get => search; set { if(search==value)return; search=value; Notify(); ScheduleFilters(); } }
    public PriorityFilterOption? FilterPriority { get => filterPriority; set { if(filterPriority==value)return; filterPriority=value; Notify(); ScheduleFilters(); } }
    public DeadlineFilterOption? FilterDeadline { get => filterDeadline; set { if(filterDeadline==value)return; filterDeadline=value; Notify(); ScheduleFilters(); } }
    public TagFilterOption? FilterTag { get => filterTag; set { if(filterTag==value)return; filterTag=value; Notify(); ScheduleFilters(); } }
    public AssigneeOption? FilterAssignee { get => filterAssignee; set { if(filterAssignee==value)return; filterAssignee=value; Notify(); ScheduleFilters(); } }
    public PriorityFilterOption[] PriorityFilters { get; } = [new(null,"Усі пріоритети"),..Enum.GetValues<CardPriority>().Select(p=>new PriorityFilterOption(p,p.ToString()))];
    public DeadlineFilterOption[] DeadlineFilters { get; } = [new(null,"Усі дедлайни"),new(DeadlineFilter.Overdue,"Прострочені"),new(DeadlineFilter.Today,"Сьогодні"),new(DeadlineFilter.Upcoming,"Майбутні")];
    public ObservableCollection<TagFilterOption> TagFilters { get; } = [];
    public ObservableCollection<AssigneeOption> AssigneeFilters { get; } = [];
    public ActionCommand ResetFiltersCommand { get; }
    public ActionCommand RemoveCoverCommand { get; }
    public Task Filtering { get; private set; } = Task.CompletedTask;
    private CardQuery Query() => new() { Search=Search,Priority=FilterPriority?.Value,TagId=FilterTag?.Id,AssigneeId=FilterAssignee?.Id,Deadline=FilterDeadline?.Value,Today=localToday() };
    private void ScheduleFilters()
    {
        if(suppressFilters)return;
        filterRevision++; CardsFiltered=!string.IsNullOrWhiteSpace(Search)||FilterPriority?.Value is not null||FilterTag?.Id is not null||FilterAssignee?.Id is not null||FilterDeadline?.Value is not null;
        filterDelay?.Cancel();filterDelay?.Dispose();filterDelay=new(); if(filtering)operation?.Cancel();
        if(board is null)return;
        complete=false;NotifyDrag(); var id=board.Id; var revision=filterRevision; var token=filterDelay.Token;
        Filtering=Apply();
        async Task Apply()
        {
            try
            {
                await Task.Delay(300,token);
                while(IsBusy||polling||IsDragging){await Task.Delay(30,token);}
                token.ThrowIfCancellationRequested(); if(revision!=filterRevision||BoardId!=id)return;
                filtering=true;Notify(nameof(CanFilter));
                try{await Run(t=>Load(id,t));}finally{filtering=false;Notify(nameof(CanFilter));}
            }
            catch(OperationCanceledException) { }
        }
    }
    public void ResetFilters()
    {
        suppressFilters=true; Search=""; FilterPriority=PriorityFilters[0];FilterDeadline=DeadlineFilters[0];FilterTag=TagFilters.FirstOrDefault();FilterAssignee=AssigneeFilters.FirstOrDefault();suppressFilters=false;ScheduleFilters();
    }
    private void DisposeRows() { foreach(var row in Columns.SelectMany(c=>c.Cards))row.Dispose(); }
    private void CancelImages(){imagesLifetime.Cancel();imagesLifetime.Dispose();imagesLifetime=new();}
    public async Task LoadThumbnailAsync(CardItemViewModel row)
    {
        if(row.ImageRequested||!row.Card.HasImage||board is null)return; row.ImageRequested=true;
        var token=imagesLifetime.Token; var id=board.Id;
        try
        {
            var bytes=await services.Images.Read(row.Card.Id,row.Card.Version,true,token);token.ThrowIfCancellationRequested();
            if(BoardId!=id||!Columns.Any(c=>c.Cards.Contains(row)))return;
            row.Thumbnail=new Bitmap(new MemoryStream(bytes));
        }
        catch(OperationCanceledException) { }
        catch(ApiClientException ex) when(ex.Status==404) { services.Images.Clear(); row.ImageRequested=false; }
        catch(Exception) { row.ImageRequested=false; }
    }
    private async Task LoadFullImage(CancellationToken token)
    {
        if(selectedCard is not {Card.HasImage:true} card){FullImage=null;return;}
        var bytes=await services.Images.Read(card.Card.Id,card.Card.Version,false,token);token.ThrowIfCancellationRequested();
        FullImage=new Bitmap(new MemoryStream(bytes));
    }
    public bool CanDropCover(Guid cardId)=>IsVisible&&complete&&!IsBusy&&!polling&&!IsDragging&&
        (EditorVisible?selectedCard?.Card.Id==cardId:!AnyDialogVisible&&Columns.Any(c=>c.Cards.Any(r=>r.Card.Id==cardId)));
    public Task UploadCoverAsync(byte[] bytes,string filename)=>selectedCard is {} current
        ? ImportCoverAsync(current.Card.Id,_=>Task.FromResult((bytes,filename))) : Task.CompletedTask;
    public Task ImportCoverAsync(Guid cardId,Func<CancellationToken,Task<(byte[] Bytes,string Name)>> read)
    {
        if(!CanDropCover(cardId)||board is null)return Task.CompletedTask;
        var id=board.Id;var boardVersion=board.Version;var editing=EditorVisible;
        var target=editing?selectedCard?.Card:Columns.SelectMany(c=>c.Cards).FirstOrDefault(c=>c.Card.Id==cardId)?.Card;
        if(target is null)return Task.CompletedTask;
        return Run(async token=>
        {
            (byte[] Bytes,string Name) file;
            try{file=await read(token);}
            catch(ArgumentException ex){ImageError(ex.Message);return;}
            catch(System.IO.IOException){ImageError("Не вдалося відкрити фото.");return;}
            token.ThrowIfCancellationRequested();
            if(BoardId!=id||(editing&&EditingCardId!=cardId))return;
            if(file.Bytes.Length>ImageLimits.MaxBytes){ImageError("Фото: не більше 5 МіБ.");return;}
            await services.Images.Upload(cardId,file.Bytes,file.Name,new(){ExpectedBoardVersion=boardVersion,ExpectedVersion=target.Version},token);
            token.ThrowIfCancellationRequested();services.Images.Clear();
            if(editing)selectedCard=await services.Content.Card(cardId,token);
            await Load(id,token);
            if(editing)await LoadFullImage(token);
            NotifyState();Status="Обкладинку збережено.";
        });
    }
    public Task RemoveCoverAsync()=>Run(async token=>
    {
        if(board is null||selectedCard is null)return;
        await services.Images.Delete(selectedCard.Card.Id,new(){ExpectedBoardVersion=board.Version,ExpectedVersion=selectedCard.Card.Version},token);
        token.ThrowIfCancellationRequested();services.Images.Clear();FullImage=null;selectedCard=await services.Content.Card(selectedCard.Card.Id,token);await Load(board.Id,token);NotifyState();Status="Обкладинку прибрано.";
    });
    public void ImageError(string message){transientError=false;Error=message;Status="";}
    private readonly ClientServices services;
    private CancellationTokenSource? operation;
    private BoardDetailsDto? board;
    private ColumnDto? selectedColumn;
    private CardDetailsDto? selectedCard;
    private bool busy, visible, editor;
    private bool dragging, complete, filtered, pendingRemote, polling;
    public bool IsPolling => polling;
    public Guid? BoardId => board?.Id;
    public bool IsDragging { get => dragging; private set { dragging = value; Notify(); NotifyDrag(); } }
    public bool IsComplete => complete;
    public bool CardsFiltered { get => filtered; set { filtered = value; Notify(); NotifyDrag(); } }
    public bool CanDragColumns => !NewColumnVisible && IsVisible && complete && !IsBusy && !polling && !IsDragging && !EditorVisible && !HasColumn;
    public bool CanDragCards => CanDragColumns && !CardsFiltered;
    public bool RemoteChangesPending { get => pendingRemote; private set { pendingRemote = value; Notify(); } }
    private string syncMessage = "Оновлення кожні 5 секунд.";
    public string SyncMessage { get => syncMessage; private set { syncMessage = value; Notify(); } }
    public double LastReorderFeedbackMilliseconds { get; private set; }
    private string error = "", status = "", newColumnName = "", columnName = "", title = "", description = "", dueDate = "", commentText = "", tagName = "", tagColor = "#64748B";
    private string? pendingDelete;
    private CardPriority priority = CardPriority.Medium;
    private AssigneeOption? assignee;
    public ObservableCollection<ColumnItemViewModel> Columns { get; } = [];
    public ObservableCollection<TagChoiceViewModel> Tags { get; } = [];
    public ObservableCollection<AssigneeOption> Assignees { get; } = [];
    public ObservableCollection<CommentItemViewModel> Comments { get; } = [];
    public bool IsVisible { get => visible; private set { visible = value; Notify(); } }
    public bool IsBusy { get => busy; private set { busy = value; Notify(); Notify(nameof(CanEdit)); NotifyDrag(); RefreshCommands(); } }
    public bool CanEdit => !IsBusy;
    public bool CanCreate => board is not null && (board.Role == BoardRole.Admin || board.CanCreateCards);
    public bool CanDeleteCard => selectedCard is not null && board is not null && (board.Role == BoardRole.Admin || board.CanDeleteCards);
    public bool CanDeleteColumn => selectedColumn is not null && board is not null && (board.Role == BoardRole.Admin || board.CanDeleteCards || !Columns.SelectMany(c => c.Cards).Any(c => c.Card.ColumnId == selectedColumn.Id));
    public bool HasColumn => selectedColumn is not null;
    public bool EditorVisible { get => editor; private set { editor = value; Notify();Notify(nameof(AnyDialogVisible)); } }
    public string SourceAssigneeNotice=>selectedCard?.Card.SourceAssigneeName is {} name?"Виконавець у копії: "+name+". Призначте учасника цієї дошки.":"";
    public bool ExistingCard => selectedCard is not null;
    public string EditorHeading => selectedCard is null ? "Нова картка" : "Редагування картки";
    public bool ShowConfirmation => pendingDelete is not null;
    public string ConfirmationText => pendingDelete == "column" ? "Видалити колонку разом з її картками, коментарями та фото?" : "Видалити картку з її коментарями та фото?";
    public string Error { get => error; private set { error = value; Notify(); Notify(nameof(HasError)); } }
    public bool HasError => Error.Length != 0;
    public string Status { get => status; private set { status = value; Notify();Notify(nameof(HasStatus)); } }
    public string NewColumnName { get => newColumnName; set { newColumnName = value; Notify(); } }
    public string ColumnName { get => columnName; set { columnName = value; Notify(); } }
    public string Title { get => title; set { title = value; Notify(); } }
    public string Description { get => description; set { description = value; Notify(); } }
    public string DueDate { get => dueDate; set { dueDate = value; Notify(); Notify(nameof(CalendarDate)); } }
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

    public BoardContentViewModel(ClientServices services, Func<DateOnly>? localToday = null)
    {
        this.services = services; this.localToday=localToday??(()=>DateOnly.FromDateTime(DateTime.Today));
        filterPriority=PriorityFilters[0];filterDeadline=DeadlineFilters[0];
        ResetFiltersCommand=new ActionCommand(ResetFilters,()=>CanFilter);
        RemoveCoverCommand=new ActionCommand(()=>_ = RemoveCoverAsync(),()=>CanChangeCover&&HasCover);
        RefreshCommand = Command(RefreshAsync);
        CreateColumnCommand = Command(CreateColumnAsync);
        RenameColumnCommand = Command(RenameColumnAsync, () => HasColumn);
        DeleteColumnCommand = new ActionCommand(() => RequestDelete("column"), () => !IsBusy && CanDeleteColumn);
        SaveCardCommand = Command(SaveCardAsync, () => EditorVisible && (ExistingCard || CanCreate));
        DeleteCardCommand = new ActionCommand(() => RequestDelete("card"), () => !IsBusy && CanDeleteCard);
        CloseEditorCommand = new ActionCommand(RequestCloseEditor, () => !IsBusy);
        CreateTagCommand = Command(CreateTagAsync);
        AddCommentCommand = Command(AddCommentAsync, () => ExistingCard);
        ConfirmCommand = Command(ConfirmAsync, () => ShowConfirmation);
        CancelCommand = new ActionCommand(CancelConfirmation, () => !IsBusy);
        services.Session.Changed += SessionChanged; InitializePresentation(); InitializeChecklist(); InsertMentionCommand=new(InsertMention);
    }
    private ActionCommand Command(Func<Task> action, Func<bool>? allowed = null) => new(() => _ = action(), () => !IsBusy && !polling && IsVisible && (allowed?.Invoke() ?? true));
    private void SessionChanged() { if (services.Session.AccessToken is null) Clear(); }
    public Task OpenAsync(Guid id) => Run(async token => { ClearData(); await Load(id, token); IsVisible = true; });
    public Task RefreshAsync() => Run(async token => { if (board is not null && !IsDragging) await Load(board.Id, token); });
    public Task PollAsync()
    {
        if (IsBusy || polling || IsDragging || !IsVisible || board is null) return Task.CompletedTask;
        return Run(async token =>
        {
            var current = await services.Boards.GetAsync(board.Id, token); token.ThrowIfCancellationRequested();
            if (current.Version == board.Version && (FilterDeadline?.Value is null || loadedFilterDay==localToday())) return;
            if (EditorVisible || HasColumn)
            {
                // Keep the original versions and draft fields; the next write must still detect a conflict.
                board = board with { Role = current.Role, CanCreateCards = current.CanCreateCards, CanDeleteCards = current.CanDeleteCards };
                RemoteChangesPending = true; SyncMessage = "Дошку змінено. Чернетку збережено; закрийте редактор, щоб оновити вміст."; NotifyState();
            }
            else { await Load(board.Id, token); SyncMessage = "Отримано зміни спільної дошки."; }
        }, background: true);
    }
    private async Task Load(Guid id, CancellationToken token)
    {
        var revision=filterRevision;var query=Query();
        var metadata = await services.Boards.GetAsync(id, token);
        var columns = await services.Content.Columns(id, token);
        var tags = await services.Content.Tags(id, token);
        var members = await services.Boards.MembersAsync(id, token);
        var cards = new System.Collections.Generic.List<CardSummaryDto>();
        PagedResult<CardSummaryDto> page;
        do
        {
            page = await services.Content.Cards(id, cards.Count, token, query);
            if (page.BoardVersion != metadata.Version || (page.Items.Count == 0 && cards.Count < page.TotalCount)) throw Conflict();
            cards.AddRange(page.Items);
        } while (cards.Count < page.TotalCount);
        var latest = await services.Boards.GetAsync(id, token); token.ThrowIfCancellationRequested();
        if (latest.Version != metadata.Version) throw Conflict();
        if(revision!=filterRevision)throw new OperationCanceledException();
        var tagIds = Tags.Where(t => t.Selected).Select(t => t.Tag.Id).ToHashSet(); var assigneeId = Assignee?.Id;
        CancelImages();DisposeRows();board = metadata; Columns.Clear();
        foreach (var col in columns)
        {
            var row = new ColumnItemViewModel(col, this); foreach (var card in cards.Where(c => c.ColumnId == col.Id)) row.Cards.Add(new CardItemViewModel(card, this)); Columns.Add(row);
        }
        if (selectedColumn is not null && columns.All(c => c.Id != selectedColumn.Id)) selectedColumn = null;
        Tags.Clear(); foreach (var t in tags) { var choice=new TagChoiceViewModel(t) { Selected = tagIds.Contains(t.Id) };choice.SelectionChanged+=NotifySelectedTags;Tags.Add(choice); }
        foreach(var option in Assignees)option.Avatar?.Dispose();Assignees.Clear(); Assignees.Add(new(null, "Без відповідального")); foreach (var member in members) Assignees.Add(new(member.User.Id, member.User.Name,member.User));
        Assignee = Assignees.FirstOrDefault(a => a.Id == assigneeId) ?? Assignees[0];
        suppressFilters=true;
        var oldTag=FilterTag;var oldAssignee=FilterAssignee;
        TagFilters.Clear();TagFilters.Add(new(null,"Усі теги"));foreach(var tag in tags)TagFilters.Add(new(tag.Id,tag.Name));
        if(oldTag?.Id is {} oldTagId&&TagFilters.All(t=>t.Id!=oldTagId))TagFilters.Add(oldTag);
        AssigneeFilters.Clear();AssigneeFilters.Add(new(null,"Усі відповідальні"));foreach(var member in members)AssigneeFilters.Add(new(member.User.Id,member.User.Name));
        if(oldAssignee?.Id is {} oldUser&&AssigneeFilters.All(a=>a.Id!=oldUser))AssigneeFilters.Add(oldAssignee);
        FilterTag=TagFilters.FirstOrDefault(t=>t.Id==oldTag?.Id)??TagFilters[0];FilterAssignee=AssigneeFilters.FirstOrDefault(a=>a.Id==oldAssignee?.Id)??AssigneeFilters[0];suppressFilters=false;
        loadedFilterDay=query.Today??localToday();complete = true; RemoteChangesPending = false; SyncMessage = "Оновлення кожні 5 секунд."; NotifyState();
    }
    private static ApiClientException Conflict() => new(409, new ApiProblem { Code = "concurrency_conflict", Title = "Дошку змінено під час завантаження. Оновіть дані." });
    public void SelectColumn(ColumnItemViewModel row) { if (IsBusy) return; CloseEditor(); selectedColumn = row.Column; ColumnName = row.Name; NotifyState(); }
    public Task CreateColumnAsync() => Run(async token =>
    {
        if (board is null) return;
        await services.Content.CreateColumn(board.Id, new() { Name = NewColumnName, ExpectedBoardVersion = board.Version }, token);
        token.ThrowIfCancellationRequested(); NewColumnName = ""; NewColumnVisible=false; await Load(board.Id, token); Status = "Колонку створено.";
    });
    public Task RenameColumnAsync() => Run(async token =>
    {
        if (board is null || selectedColumn is null) return;
        var updated = await services.Content.UpdateColumn(board.Id, selectedColumn.Id, new() { Name = ColumnName, ExpectedBoardVersion = board.Version, ExpectedVersion = selectedColumn.Version }, token);
        token.ThrowIfCancellationRequested(); selectedColumn = updated;
        selectedColumn=null; await Load(board.Id, token); Status = "Колонку перейменовано.";
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
        LoadChecklist(card.Checklist);
        Title = card.Card.Title; Description = card.Description ?? ""; DueDate = card.Card.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? ""; Priority = card.Card.Priority;
        Assignee = Assignees.FirstOrDefault(a => a.Id == card.Card.Assignee?.Id) ?? Assignees.FirstOrDefault();
        foreach (var tag in Tags) tag.Selected = card.Card.Tags.Any(t => t.Id == tag.Tag.Id);
        DisposeComments(); Comments.Clear(); foreach (var c in comments) Comments.Add(new(c,this)); CommentText = ""; EditorVisible = true; CancelConfirmation(); await LoadFullImage(token); NotifyState();
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
        if (!string.IsNullOrWhiteSpace(NewChecklistText)) { Error = "Натисніть «Додати», щоб включити новий пункт до чек-листа."; return; }
        if (ChecklistItems.Any(i => string.IsNullOrWhiteSpace(i.Title) || i.Title.Length > 300)) { Error = "Кожен пункт: від 1 до 300 символів."; return; }
        var tags = Tags.Where(t => t.Selected).Select(t => t.Tag.Id).ToList();
        CardDetailsDto? saved;
        if (selectedCard is null)
        {
            if (!CanCreate) return;
            saved = await services.Content.CreateCard(board.Id, new() { ColumnId = selectedColumn.Id, Title = Title, Description = Description, DueDate = date, Priority = Priority, AssigneeId = Assignee?.Id, TagIds = tags, Checklist = ChecklistPayload(), ExpectedBoardVersion = board.Version }, token);
        }
        else saved = await services.Content.UpdateCard(selectedCard.Card.Id, new() { Title = Title, Description = Description, DueDate = date, Priority = Priority, AssigneeId = Assignee?.Id, TagIds = tags, Checklist = ChecklistPayload(), ExpectedBoardVersion = board.Version, ExpectedVersion = selectedCard.Card.Version }, token);
        token.ThrowIfCancellationRequested(); selectedCard = saved; LoadChecklist(saved?.Checklist); await Load(board.Id, token); NotifyState(); Status = "Картку збережено.";
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
        selectedCard = card; CommentText = ""; DisposeComments(); Comments.Clear(); foreach (var c in comments) Comments.Add(new(c,this));
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
    public void CloseEditor() { MentionPerson=null; LoadChecklist(null); DiscardChangesVisible=false;Notify(nameof(DiscardChangesVisible)); NewColumnVisible=false;FullImageVisible=false;CancelCommentEdit(); FullImage=null; selectedCard = null; selectedColumn = null; ColumnName = ""; DisposeComments(); Comments.Clear(); CommentText = ""; EditorVisible = false; pendingDelete = null; NotifyState(); }

    public bool BeginDrag(Guid id, bool card)
    {
        if (card ? !CanDragCards || !Columns.Any(c => c.Cards.Any(x => x.Card.Id == id)) : !CanDragColumns || !Columns.Any(c => c.Column.Id == id)) return false;
        IsDragging = true; return true;
    }
    public void EndDrag() => IsDragging = false;
    public Task MoveCardAsync(Guid id, Guid targetColumn, Guid? beforeCard = null) => Run(async token =>
    {
        if (board is null || !complete || CardsFiltered || EditorVisible || HasColumn) return;
        var source = Columns.FirstOrDefault(c => c.Cards.Any(x => x.Card.Id == id));
        var target = Columns.FirstOrDefault(c => c.Column.Id == targetColumn);
        if (source is null || target is null || beforeCard == id) return;
        var card = source.Cards.Single(x => x.Card.Id == id);
        var index = beforeCard is Guid before ? target.Cards.ToList().FindIndex(c => c.Card.Id == before) : target.Cards.Count;
        if (index < 0) return;
        var original = Columns.ToDictionary(c => c, c => c.Cards.ToList());
        var body = new MoveCardRequest { TargetColumnId = targetColumn, BeforeCardId = beforeCard, ExpectedBoardVersion = board.Version, ExpectedVersion = card.Card.Version };
        var started = Stopwatch.GetTimestamp();
        source.Cards.Remove(card);
        index = beforeCard is Guid anchor ? target.Cards.ToList().FindIndex(c => c.Card.Id == anchor) : target.Cards.Count;
        target.Cards.Insert(index, card);
        LastReorderFeedbackMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        try
        {
            await services.Content.MoveCard(id, body, token); await Load(board.Id, token); Status = "Картку переміщено.";
        }
        catch
        {
            if (!token.IsCancellationRequested)
                foreach (var pair in original) { pair.Key.Cards.Clear(); foreach (var row in pair.Value) pair.Key.Cards.Add(row); }
            throw;
        }
    });
    public Task ReorderColumnAsync(Guid id, Guid? beforeColumn = null) => Run(async token =>
    {
        if (board is null || !complete || EditorVisible || HasColumn || beforeColumn == id) return;
        var original = Columns.ToList(); var row = Columns.FirstOrDefault(c => c.Column.Id == id);
        if (row is null || (beforeColumn is Guid target && Columns.All(c => c.Column.Id != target))) return;
        var started = Stopwatch.GetTimestamp(); Columns.Remove(row);
        var index = beforeColumn is Guid before ? Columns.ToList().FindIndex(c => c.Column.Id == before) : Columns.Count;
        Columns.Insert(index, row); LastReorderFeedbackMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        try
        {
            await services.Content.ReorderColumns(board.Id, new() { ColumnIds = Columns.Select(c => c.Column.Id).ToList(), ExpectedBoardVersion = board.Version }, token);
            await Load(board.Id, token); Status = "Порядок колонок збережено.";
        }
        catch
        {
            if (!token.IsCancellationRequested) { Columns.Clear(); foreach (var item in original) Columns.Add(item); }
            throw;
        }
    });
    private async Task Run(Func<CancellationToken, Task> action, bool background = false)
    {
        if (IsBusy || polling || services.Session.AccessToken is null) return;
        using var cancellation = new CancellationTokenSource(); operation = cancellation;
        if (background) { polling = true; Notify(nameof(IsPolling)); NotifyDrag(); RefreshCommands(); }
        else IsBusy = true;
        if (!background) { Error = ""; Status = ""; transientError = false; }
        try
        {
            await action(cancellation.Token);
            if (background && transientError && !cancellation.IsCancellationRequested)
            { Error = ""; transientError = false; }
        }
        catch (ApiClientException ex)
        {
            if (cancellation.IsCancellationRequested) return;
            transientError = ex.Status >= 500 || ex.Problem.Code == "http_error";
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
        catch (OperationCanceledException) { if (!cancellation.IsCancellationRequested) { Error = "Сервер не відповів вчасно."; transientError = true; } }
        catch (HttpRequestException) { if (!cancellation.IsCancellationRequested) { Error = "Не вдалося підключитися до сервера."; transientError = true; } }
        catch (Exception) { if (!cancellation.IsCancellationRequested) { transientError=false; Error = "Не вдалося виконати дію. Оновіть дошку."; } }
        finally
        {
            if (ReferenceEquals(operation, cancellation))
            {
                operation = null; polling = false; Notify(nameof(IsPolling)); IsBusy = false;
            }
        }
    }
    private void NotifyState()
    {
        Notify(nameof(SourceAssigneeNotice));
        foreach (var name in new[] { nameof(CanCreate), nameof(CanDeleteCard), nameof(CanDeleteColumn), nameof(HasColumn), nameof(ExistingCard), nameof(EditorHeading), nameof(ShowConfirmation), nameof(ConfirmationText) }) Notify(name);
        NotifyPresentation();RefreshCommands();
        NotifyDrag();
    }
    private void NotifyDrag()
    {
        Notify(nameof(CanFilter));Notify(nameof(HasCover));Notify(nameof(CanChangeCover));RemoveCoverCommand.Refresh();ResetFiltersCommand.Refresh();
        Notify(nameof(CanDragColumns)); Notify(nameof(CanDragCards)); Notify(nameof(IsComplete));
    }
    private void RefreshCommands()
    {
        ChecklistChanged();ArchiveCardCommand?.Refresh();SaveCommentCommand?.Refresh();DeleteCommentCommand?.Refresh();
        foreach (var command in new[] { RefreshCommand, CreateColumnCommand, RenameColumnCommand, DeleteColumnCommand, SaveCardCommand, DeleteCardCommand, CloseEditorCommand, CreateTagCommand, AddCommentCommand, ConfirmCommand, CancelCommand }) command.Refresh();
        foreach (var col in Columns) col.Refresh();
    }
    private void ClearData()
    {
        LoadChecklist(null);
        filterDelay?.Cancel();filterRevision++;CancelImages();DisposeRows();FullImage=null;services.Images.Clear();
        suppressFilters=true;Search="";FilterPriority=PriorityFilters[0];FilterDeadline=DeadlineFilters[0];FilterTag=null;FilterAssignee=null;TagFilters.Clear();AssigneeFilters.Clear();suppressFilters=false;
        board = null; complete = false; dragging = false; filtered = false; RemoteChangesPending = false;
        SyncMessage = "Оновлення кожні 5 секунд.";
        selectedColumn = null; selectedCard = null; Columns.Clear(); Tags.Clear(); foreach(var option in Assignees)option.Avatar?.Dispose();Assignees.Clear(); DisposeComments(); Comments.Clear();
        NewColumnName = ""; ColumnName = ""; Title = ""; Description = ""; DueDate = ""; CommentText = ""; TagName = ""; TagColor = "#64748B"; Priority = CardPriority.Medium; Assignee = null;
        DiscardChangesVisible=false;Notify(nameof(DiscardChangesVisible));NewColumnVisible=false;FullImageVisible=false;CancelCommentEdit(); EditorVisible = false; IsVisible = false; pendingDelete = null; NotifyState();
    }
    public void Clear() { operation?.Cancel(); operation = null; polling = false; transientError = false; Notify(nameof(IsPolling)); IsBusy = false; ClearData(); Error = ""; Status = ""; }
    public void Dispose() { Clear();imagesLifetime.Dispose();filterDelay?.Dispose();services.Session.Changed -= SessionChanged; }
}
