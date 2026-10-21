using KanbanApi.Domain;
using KanbanApi.Infrastructure;
using KanbanContracts;
using Microsoft.EntityFrameworkCore;

namespace KanbanApi.Application;

// All future board mutations (including cards and assignments) must use this same gate.
public sealed class BoardAccess(KanbanDbContext db, ActivityRecorder recorder)
{
    public async Task<BoardMember> RequireAsync(Guid boardId, Guid userId, bool admin, CancellationToken token)
    {
        var member = await db.BoardMembers.AsNoTracking().SingleOrDefaultAsync(x => x.BoardId == boardId && x.UserId == userId, token)
            ?? throw NotFound();
        if (admin && member.Role != BoardRole.Admin)
            throw new ApiException(403, "forbidden", "Ця дія доступна лише адміністратору дошки.");
        return member;
    }

    public async Task<T> MutateAsync<T>(Guid boardId, Guid userId, string expectedVersion, bool admin,
        Func<BoardMember, Task<T>> action, CancellationToken token)
    {
        byte[] version;
        try { version = Convert.FromBase64String(expectedVersion); }
        catch (FormatException) { throw new ApiException(400, "validation_failed", "Некоректна версія дошки."); }
        if (version.Length != 8) throw new ApiException(400, "validation_failed", "Некоректна версія дошки.");
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            // Check access before examining a version, so private IDs never reveal a conflict.
            await RequireAsync(boardId, userId, admin, token);
            var touched = await db.Boards.Where(x => x.Id == boardId && x.RowVersion == version)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.UpdatedAtUtc, DateTime.UtcNow), token);
            if (touched == 0)
            {
                await RequireAsync(boardId, userId, admin, token);
                throw new ApiException(409, "concurrency_conflict", "Дошку вже змінено. Оновіть її й повторіть дію.");
            }
            // The update holds the board lock until commit. Membership is checked again under that lock.
            var member = await RequireAsync(boardId, userId, admin, token);
            var before = await recorder.Capture(boardId, token);
            var result = await action(member);
            await db.SaveChangesAsync(token);
            await recorder.Record(boardId, userId, before, token);
            await transaction.CommitAsync(token);
            return result;
        });
    }

    public static ApiException NotFound() => new(404, "not_found", "Дошку або учасника не знайдено чи доступ відкликано.");
}
