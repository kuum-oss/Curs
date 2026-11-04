using System;
using System.Linq;
using System.Threading.Tasks;

namespace Curs.ViewModels;

public sealed partial class BoardContentViewModel
{
    private Guid? pendingCompletion;
    public Func<Task>? CompletionChanged { get; set; }
    public bool CompletionPromptVisible => pendingCompletion is not null;
    public bool CanCreateCompletionColumn => board?.Role == KanbanContracts.BoardRole.Admin;
    public string CompletionButtonLabel => EditorCompleted ? "Повернути в роботу" : "✓ Виконано";
    public bool EditorCompleted => selectedCard?.Card.IsCompleted == true;
    public ActionCommand MarkCompletionColumnCommand { get; private set; } = null!;
    public ActionCommand CompleteCardCommand { get; private set; } = null!;
    public ActionCommand CreateCompletionColumnCommand { get; private set; } = null!;
    public ActionCommand CompleteWithoutColumnCommand { get; private set; } = null!;
    public ActionCommand CancelCompletionCommand { get; private set; } = null!;
    private void InitializeCompletion()
    {
        MarkCompletionColumnCommand = Command(SetCompletionColumnAsync, () => HasColumn && CanCreateCompletionColumn);
        CompleteCardCommand = Command(() => selectedCard is {} c ? RequestCompletionAsync(c.Card.Id, !c.Card.IsCompleted) : Task.CompletedTask, () => ExistingCard);
        CreateCompletionColumnCommand = Command(() => CompletePendingAsync(true), () => CompletionPromptVisible && CanCreateCompletionColumn);
        CompleteWithoutColumnCommand = Command(() => CompletePendingAsync(false), () => CompletionPromptVisible);
        CancelCompletionCommand = new(CancelCompletion, () => !IsBusy);
    }
    private void RefreshCompletion()
    {
        Notify(nameof(EditorCompleted)); Notify(nameof(CompletionButtonLabel)); Notify(nameof(CanCreateCompletionColumn));
        MarkCompletionColumnCommand.Refresh(); CompleteCardCommand.Refresh(); CreateCompletionColumnCommand.Refresh(); CompleteWithoutColumnCommand.Refresh(); CancelCompletionCommand.Refresh();
    }
    private void CancelCompletion()
    {
        pendingCompletion = null; Notify(nameof(CompletionPromptVisible)); Notify(nameof(AnyDialogVisible));
    }
    public async Task RequestCompletionAsync(Guid cardId, bool completed = true)
    {
        var completeNow = false;
        await Run(async token =>
        {
            if (board is null) return;
            if (HasUnsavedEditor()) { Error = "Спочатку збережіть зміни картки або скасуйте редагування."; return; }
            var card = selectedCard?.Card.Id == cardId ? selectedCard.Card : Columns.SelectMany(c => c.Cards).FirstOrDefault(c => c.Card.Id == cardId)?.Card;
            if (card is null || card.IsCompleted == completed) return;
            var stats = await services.Insights.Statistics(board.Id, token);
            token.ThrowIfCancellationRequested();
            pendingCompletion = cardId;
            completeNow = !completed || stats.CompletionColumnId is not null;
            if (!completeNow) { Notify(nameof(CompletionPromptVisible)); Notify(nameof(AnyDialogVisible)); }
        });
        if (completeNow && pendingCompletion == cardId) await CompletePendingAsync(false, completed);
    }

    public Task SetCompletionColumnAsync() => Run(async token =>
    {
        if (board is null || selectedColumn is null || !CanCreateCompletionColumn) return;
        await services.Insights.SetCompletion(board.Id, selectedColumn.Id, board.Version, token);
        await Load(board.Id, token);
        Status = "Колонку завершення збережено. Її картки враховуються як виконані.";
        if (CompletionChanged is not null) await CompletionChanged();
    });

    public Task CompletePendingAsync(bool createColumn, bool completed = true) => Run(async token =>
    {
        if (board is null || pendingCompletion is not {} id) return;
        var card = selectedCard?.Card.Id == id ? selectedCard.Card : Columns.SelectMany(c => c.Cards).FirstOrDefault(c => c.Card.Id == id)?.Card;
        if (card is null) { CancelCompletion(); return; }
        var editing = selectedCard?.Card.Id == id;
        var saved = await services.Content.CompleteCard(id, new() { ExpectedBoardVersion = board.Version, ExpectedVersion = card.Version, CreateCompletionColumn = createColumn, Completed = completed }, token);
        token.ThrowIfCancellationRequested();
        CancelCompletion();
        if (editing) selectedCard = saved;
        await Load(board.Id, token);
        if (editing && saved is not null)
        {
            selectedColumn = Columns.FirstOrDefault(c => c.Column.Id == saved.Card.ColumnId)?.Column;
            ColumnName = selectedColumn?.Name ?? "";
        }
        NotifyState(); RefreshCompletion(); Status = completed ? "Картку позначено як виконану." : "Картку повернуто в роботу.";
        if (CompletionChanged is not null) await CompletionChanged();
    });
}
