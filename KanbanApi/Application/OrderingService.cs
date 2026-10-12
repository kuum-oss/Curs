using KanbanApi.Infrastructure;
using KanbanContracts;
using Microsoft.EntityFrameworkCore;

namespace KanbanApi.Application;

public sealed class OrderingService(KanbanDbContext db, BoardAccess access)
{
    private async Task<string> BoardVersion(Guid id, CancellationToken token) =>
        Convert.ToBase64String(await db.Boards.Where(x => x.Id == id).Select(x => x.RowVersion).SingleAsync(token));

    public Task<OrderResultDto> ReorderColumns(Guid boardId, Guid userId, ReorderColumnsRequest request, CancellationToken token) =>
        access.MutateAsync(boardId, userId, request.ExpectedBoardVersion, false, async _ =>
        {
            var columns = await db.Columns.Where(x => x.BoardId == boardId).ToListAsync(token);
            if (request.ColumnIds.Count != columns.Count || request.ColumnIds.Distinct().Count() != columns.Count ||
                columns.Any(c => !request.ColumnIds.Contains(c.Id)))
                throw new ApiException(400, "invalid_order", "Порядок має містити всі колонки цієї дошки рівно один раз.");
            var byId = columns.ToDictionary(c => c.Id);
            for (var i = 0; i < request.ColumnIds.Count; i++) byId[request.ColumnIds[i]].Position = i;
            await db.SaveChangesAsync(token);
            return new OrderResultDto(await BoardVersion(boardId, token));
        }, token);

    public async Task<MoveResultDto> MoveCard(Guid cardId, Guid userId, MoveCardRequest request, CancellationToken token)
    {
        var boardId = await (from card in db.Cards join column in db.Columns on card.ColumnId equals column.Id
                             join member in db.BoardMembers on column.BoardId equals member.BoardId
                             where card.Id == cardId && member.UserId == userId select (Guid?)column.BoardId).SingleOrDefaultAsync(token)
            ?? throw BoardAccess.NotFound();
        return await access.MutateAsync(boardId, userId, request.ExpectedBoardVersion, false, async _ =>
        {
            var card = await db.Cards.SingleOrDefaultAsync(x => x.Id == cardId, token) ?? throw BoardAccess.NotFound();
            if (Convert.ToBase64String(card.RowVersion) != request.ExpectedVersion)
                throw new ApiException(409, "concurrency_conflict", "Картку вже змінено. Оновіть дошку й повторіть дію.");
            if (!await db.Columns.AnyAsync(x => x.Id == request.TargetColumnId && x.BoardId == boardId, token))
                throw BoardAccess.NotFound();
            var sourceId = card.ColumnId;
            var rows = await db.Cards.Where(x => x.ColumnId == sourceId || x.ColumnId == request.TargetColumnId)
                .OrderBy(x => x.Position).ThenBy(x => x.Id).ToListAsync(token);
            var source = rows.Where(x => x.ColumnId == sourceId && x.Id != cardId).ToList();
            var target = sourceId == request.TargetColumnId ? source : rows.Where(x => x.ColumnId == request.TargetColumnId).ToList();
            var index = request.BeforeCardId is Guid before ? target.FindIndex(x => x.Id == before) : target.Count;
            if (index < 0)
                throw new ApiException(400, "invalid_anchor", "Опорна картка має належати цільовій колонці й відрізнятися від переміщуваної.");
            card.ColumnId = request.TargetColumnId; card.UpdatedAtUtc = DateTime.UtcNow; target.Insert(index, card);
            for (var i = 0; i < source.Count; i++) source[i].Position = i;
            for (var i = 0; i < target.Count; i++) target[i].Position = i;
            await db.SaveChangesAsync(token);
            return new MoveResultDto(card.Id, card.ColumnId, Convert.ToBase64String(card.RowVersion), await BoardVersion(boardId, token));
        }, token);
    }
}
