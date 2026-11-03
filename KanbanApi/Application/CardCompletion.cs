using KanbanContracts;
using KanbanApi.Domain;
using Microsoft.EntityFrameworkCore;

namespace KanbanApi.Application;

public sealed partial class ContentService
{
    public async Task<CardDetailsDto> CompleteCard(Guid cardId, Guid user, CompleteCardRequest request, CancellationToken token)
    {
        var boardId = await CardBoard(cardId, user, token);
        return await access.MutateAsync(boardId, user, request.ExpectedBoardVersion, false, async member =>
        {
            var card = await db.Cards.SingleAsync(c => c.Id == cardId, token);
            Version(card.RowVersion, request.ExpectedVersion);
            if (card.IsArchived) throw new ApiException(400, "card_archived", "Спочатку відновіть картку з архіву.");
            var target = await db.Boards.Where(b => b.Id == boardId).Select(b => b.CompletionColumnId).SingleAsync(token);
            if (!request.Completed)
            {
                if (target == card.ColumnId)
                {
                    var openColumn = await db.Columns.Where(c => c.BoardId == boardId && c.Id != target).OrderBy(c => c.Position).ThenBy(c => c.Id).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(token);
                    if (openColumn is null) throw new ApiException(400, "no_open_column", "Додайте колонку для задач у роботі, щоб повернути картку.");
                    target = openColumn;
                }
                else target = null;
            }
            if (request.Completed && target is null && request.CreateCompletionColumn)
            {
                if (member.Role != BoardRole.Admin)
                    throw new ApiException(403, "forbidden", "Колонку завершення налаштовує адміністратор. Можна позначити картку без перенесення.");
                var column = await db.Columns.Where(c => c.BoardId == boardId && c.Name == "Готово").OrderBy(c => c.Position).FirstOrDefaultAsync(token);
                if (column is null)
                {
                    var position = await db.Columns.Where(c => c.BoardId == boardId).MaxAsync(c => (int?)c.Position, token) ?? -1;
                    column = new BoardColumn { BoardId = boardId, Name = "Готово", Position = position + 1 };
                    db.Columns.Add(column); await db.SaveChangesAsync(token);
                }
                target = column.Id;
                await db.Boards.Where(b => b.Id == boardId).ExecuteUpdateAsync(s => s.SetProperty(b => b.CompletionColumnId, target), token);
            }
            if (target is {} targetId && card.ColumnId != targetId)
            {
                var source = await db.Cards.Where(c => c.ColumnId == card.ColumnId && c.Id != cardId && !c.IsArchived).OrderBy(c => c.Position).ThenBy(c => c.Id).ToListAsync(token);
                for (var i = 0; i < source.Count; i++) source[i].Position = i;
                card.Position = await db.Cards.CountAsync(c => c.ColumnId == targetId && !c.IsArchived, token);
                card.ColumnId = targetId;
            }
            card.CompletedAtUtc = request.Completed ? card.CompletedAtUtc ?? DateTime.UtcNow : null;
            card.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(token);
            return await Details(card, token);
        }, token);
    }
}
