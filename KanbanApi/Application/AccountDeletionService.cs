using System.Data;
using System.Security.Cryptography;
using System.Text;
using KanbanApi.Domain;
using KanbanApi.Infrastructure;
using KanbanContracts;
using Microsoft.EntityFrameworkCore;

namespace KanbanApi.Application;

public sealed class AccountDeletionService(KanbanDbContext db, PasswordService passwords)
{
    private sealed record BoardImpact(Board Board, BoardMember? Successor);
    private sealed record Plan(AccountDeletionPreviewDto Preview, List<BoardImpact> Owned);
    private IQueryable<Guid> AffectedBoards(Guid id) =>
        db.Boards.Where(b => b.OwnerId == id || db.BoardMembers.Any(m => m.BoardId == b.Id && m.UserId == id) ||
            db.Columns.Any(col => col.BoardId == b.Id && db.Cards.Any(c => c.ColumnId == col.Id &&
                (c.AssigneeId == id || c.CreatedById == id || db.Comments.Any(comment => comment.CardId == c.Id && comment.AuthorId == id)))))
            .Select(b => b.Id);
    private async Task<Plan> MakePlan(User user, List<Board> boards, CancellationToken token)
    {
        var ids = boards.Select(b => b.Id).ToArray();
        var members = await db.BoardMembers.Where(m => ids.Contains(m.BoardId)).ToListAsync(token);
        var owned = new List<BoardImpact>(); var display = new List<AccountDeletionBoardDto>();
        foreach (var board in boards)
        {
            var group = members.Where(m => m.BoardId == board.Id).ToList();
            // Also handle legacy boards without OwnerId whose sole administrator is leaving.
            if (board.OwnerId != user.Id && !(board.OwnerId is null && group.Any(m => m.UserId == user.Id && m.Role == BoardRole.Admin) && !group.Any(m => m.UserId != user.Id && m.Role == BoardRole.Admin))) continue;
            var successor = group.Where(m => m.UserId != user.Id).OrderBy(m => m.Role == BoardRole.Admin ? 0 : 1).ThenBy(m => m.JoinedAtUtc).ThenBy(m => m.UserId).FirstOrDefault();
            owned.Add(new(board, successor));
            var name = successor is null ? null : await db.Users.Where(u => u.Id == successor.UserId).Select(u => u.Name).SingleAsync(token);
            display.Add(new(board.Id, board.Name, name));
        }
        var snapshot = new StringBuilder(Convert.ToBase64String(user.RowVersion));
        foreach (var board in boards.OrderBy(b => b.Id)) snapshot.Append('|').Append(board.Id).Append(':').Append(Convert.ToBase64String(board.RowVersion));
        foreach (var member in members.OrderBy(m => m.BoardId).ThenBy(m => m.UserId)) snapshot.Append('|').Append(member.BoardId).Append(':').Append(member.UserId).Append(':').Append((int)member.Role).Append(':').Append(member.JoinedAtUtc.Ticks);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot.ToString())));
        return new(new(user.Email, Convert.ToBase64String(user.RowVersion), hash, display,
            members.Count(m => m.UserId == user.Id && owned.All(o => o.Board.Id != m.BoardId))), owned);
    }
    public async Task<AccountDeletionPreviewDto> Preview(Guid id, CancellationToken token)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, token) ?? throw Unauthorized();
        var boards = await db.Boards.AsNoTracking().Where(b => AffectedBoards(id).Contains(b.Id)).OrderBy(b => b.Id).ToListAsync(token);
        return (await MakePlan(user, boards, token)).Preview;
    }
    public async Task Delete(Guid id, DeleteAccountRequest request, CancellationToken token)
    {
        var observed = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, token) ?? throw Unauthorized();
        if (!string.Equals(observed.Email, request.ConfirmationEmail.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new ApiException(400, "confirmation_email_invalid", "Для підтвердження введіть свій поточний email.");
        if (string.IsNullOrEmpty(request.CurrentPassword) || Encoding.UTF8.GetByteCount(request.CurrentPassword) > 72 || !passwords.Verify(request.CurrentPassword, observed.PasswordHash))
            throw new ApiException(400, "current_password_invalid", "Поточний пароль неправильний.");
        // BCrypt runs before taking locks. Recheck the profile version under the lock.
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
            var ids = await AffectedBoards(id).OrderBy(b => b).ToArrayAsync(token);
            var boards = new List<Board>();
            foreach (var boardId in ids)
                boards.AddRange(await db.Boards.FromSqlInterpolated($"SELECT * FROM Boards WITH (UPDLOCK, HOLDLOCK) WHERE Id = {boardId}").ToListAsync(token));
            var user = await db.Users.FromSqlInterpolated($"SELECT * FROM Users WITH (UPDLOCK, HOLDLOCK) WHERE Id = {id}").SingleOrDefaultAsync(token) ?? throw Unauthorized();
            if (Convert.ToBase64String(user.RowVersion) != request.ExpectedVersion || !user.RowVersion.SequenceEqual(observed.RowVersion)) throw Changed();
            var plan = await MakePlan(user, boards, token);
            if (!string.Equals(plan.Preview.ConfirmationToken, request.ConfirmationToken, StringComparison.Ordinal)) throw Changed();
            foreach (var board in boards) board.UpdatedAtUtc = DateTime.UtcNow;
            foreach (var item in plan.Owned.Where(o => o.Successor is not null))
            {
                item.Board.OwnerId = item.Successor!.UserId;
                item.Successor.Role = BoardRole.Admin; item.Successor.CanCreateCards = true; item.Successor.CanDeleteCards = true;
            }
            await db.SaveChangesAsync(token);
            var removedBoards = plan.Owned.Where(o => o.Successor is null).Select(o => o.Board.Id).ToArray();
            await DeleteBoards(removedBoards, token);
            await db.Cards.Where(c => c.AssigneeId == id || c.CreatedById == id).ExecuteUpdateAsync(s =>
                s.SetProperty(c => c.AssigneeId, c => c.AssigneeId == id ? null : c.AssigneeId)
                 .SetProperty(c => c.CreatedById, c => c.CreatedById == id ? null : c.CreatedById)
                 .SetProperty(c => c.UpdatedAtUtc, DateTime.UtcNow), token);
            await db.Comments.Where(c => c.AuthorId == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.AuthorId, (Guid?)null), token);
            await db.BoardMembers.Where(m => m.UserId == id).ExecuteDeleteAsync(token);
            // The user row, email, password hash and avatar are physically removed.
            await db.Users.Where(u => u.Id == id).ExecuteDeleteAsync(token);
            await transaction.CommitAsync(token);
        });
    }
    private async Task DeleteBoards(Guid[] ids, CancellationToken token)
    {
        if (ids.Length == 0) return;
        var columns = db.Columns.Where(c => ids.Contains(c.BoardId)).Select(c => c.Id);
        var cards = db.Cards.Where(c => columns.Contains(c.ColumnId)).Select(c => c.Id);
        var tags = db.Tags.Where(t => ids.Contains(t.BoardId)).Select(t => t.Id);
        await db.CardTags.Where(t => cards.Contains(t.CardId) || tags.Contains(t.TagId)).ExecuteDeleteAsync(token);
        await db.CardImages.Where(i => cards.Contains(i.CardId)).ExecuteDeleteAsync(token);
        await db.Comments.Where(c => cards.Contains(c.CardId)).ExecuteDeleteAsync(token);
        await db.Cards.Where(c => columns.Contains(c.ColumnId)).ExecuteDeleteAsync(token);
        await db.Columns.Where(c => ids.Contains(c.BoardId)).ExecuteDeleteAsync(token);
        await db.Tags.Where(t => ids.Contains(t.BoardId)).ExecuteDeleteAsync(token);
        await db.BoardMembers.Where(m => ids.Contains(m.BoardId)).ExecuteDeleteAsync(token);
        await db.Boards.Where(b => ids.Contains(b.Id)).ExecuteDeleteAsync(token);
    }
    private static ApiException Unauthorized() => new(401, "unauthorized", "Сесію завершено. Увійдіть знову.");
    private static ApiException Changed() => new(409, "deletion_preview_changed", "Профіль або дошки змінилися. Перегляньте наслідки видалення ще раз.");
}
