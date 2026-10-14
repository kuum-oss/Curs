using KanbanApi.Domain;
using KanbanApi.Infrastructure;
using KanbanContracts;
using Microsoft.EntityFrameworkCore;

namespace KanbanApi.Application;

public sealed class ContentService(KanbanDbContext db, BoardAccess access)
{
    private static ApiException Missing() => BoardAccess.NotFound();
    private static void Version(byte[] actual, string expected)
    {
        if (Convert.ToBase64String(actual) != expected)
            throw new ApiException(409, "concurrency_conflict", "Дані вже змінено. Оновіть дошку й повторіть дію.");
    }
    private static void DeleteRight(BoardMember member)
    {
        if (member.Role != BoardRole.Admin && !member.CanDeleteCards)
            throw new ApiException(403, "forbidden", "Видалення карток на цій дошці обмежено.");
    }
    private IQueryable<Card> BoardCards(Guid boardId) => db.Cards.Where(c => db.Columns.Any(col => col.Id == c.ColumnId && col.BoardId == boardId));
    private async Task<Guid> CardBoard(Guid cardId, Guid userId, CancellationToken token) =>
        await (from c in db.Cards join col in db.Columns on c.ColumnId equals col.Id
               join m in db.BoardMembers on col.BoardId equals m.BoardId
               where c.Id == cardId && m.UserId == userId select (Guid?)col.BoardId).SingleOrDefaultAsync(token) ?? throw Missing();
    private static ColumnDto Column(BoardColumn col) => new(col.Id, col.BoardId, col.Name, col.Position, Convert.ToBase64String(col.RowVersion));

    public async Task<IReadOnlyList<ColumnDto>> Columns(Guid boardId, Guid userId, CancellationToken token)
    {
        await access.RequireAsync(boardId, userId, false, token);
        var rows = await db.Columns.AsNoTracking().Where(c => c.BoardId == boardId).OrderBy(c => c.Position).ThenBy(c => c.Id).ToListAsync(token);
        await access.RequireAsync(boardId, userId, false, token);
        return rows.Select(Column).ToList();
    }
    public Task<ColumnDto> CreateColumn(Guid boardId, Guid userId, CreateColumnRequest request, CancellationToken token) =>
        access.MutateAsync(boardId, userId, request.ExpectedBoardVersion, false, async _ =>
        {
            var col = new BoardColumn { BoardId = boardId, Name = request.Name.Trim(), Position = await db.Columns.CountAsync(x => x.BoardId == boardId, token) };
            db.Columns.Add(col); await db.SaveChangesAsync(token); return Column(col);
        }, token);
    public Task<ColumnDto> UpdateColumn(Guid boardId, Guid columnId, Guid userId, UpdateColumnRequest request, CancellationToken token) =>
        access.MutateAsync(boardId, userId, request.ExpectedBoardVersion, false, async _ =>
        {
            var col = await db.Columns.SingleOrDefaultAsync(x => x.Id == columnId && x.BoardId == boardId, token) ?? throw Missing();
            Version(col.RowVersion, request.ExpectedVersion); col.Name = request.Name.Trim();
            await db.SaveChangesAsync(token); return Column(col);
        }, token);
    public Task<bool> DeleteColumn(Guid boardId, Guid columnId, Guid userId, ResourceVersionQuery request, CancellationToken token) =>
        access.MutateAsync(boardId, userId, request.ExpectedBoardVersion, false, async member =>
        {
            var col = await db.Columns.SingleOrDefaultAsync(x => x.Id == columnId && x.BoardId == boardId, token) ?? throw Missing();
            Version(col.RowVersion, request.ExpectedVersion);
            if (await db.Cards.AnyAsync(x => x.ColumnId == columnId, token)) DeleteRight(member);
            await DeleteCards(db.Cards.Where(x => x.ColumnId == columnId), token);
            db.Columns.Remove(col);
            var remaining = await db.Columns.Where(x => x.BoardId == boardId && x.Id != columnId).OrderBy(x => x.Position).ThenBy(x => x.Id).ToListAsync(token);
            for (var i = 0; i < remaining.Count; i++) remaining[i].Position = i;
            return true;
        }, token);
    private async Task DeleteCards(IQueryable<Card> cards, CancellationToken token)
    {
        var ids = cards.Select(c => c.Id);
        await db.CardTags.Where(x => ids.Contains(x.CardId)).ExecuteDeleteAsync(token);
        await db.CardImages.Where(x => ids.Contains(x.CardId)).ExecuteDeleteAsync(token);
        await db.Comments.Where(x => ids.Contains(x.CardId)).ExecuteDeleteAsync(token);
        await cards.ExecuteDeleteAsync(token);
    }
    public async Task<PagedResult<CardSummaryDto>> Cards(Guid boardId, Guid userId, CardQuery page, CancellationToken token)
    {
        await access.RequireAsync(boardId, userId, false, token);
        // Hold a consistent read view for this page and its version, including tag/assignee metadata.
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, token);
        var board = await db.Boards.AsNoTracking().SingleOrDefaultAsync(x => x.Id == boardId, token) ?? throw Missing();
        var query = BoardCards(boardId).AsNoTracking();
        if (!string.IsNullOrWhiteSpace(page.Search))
        {
            var term = page.Search.Trim();
            query = query.Where(c => EF.Functions.Collate(c.Title, "Latin1_General_100_CI_AS_SC").Contains(term));
        }
        if (page.Priority is { } priority) query = query.Where(c => c.Priority == priority);
        if (page.TagId is { } tag) query = query.Where(c => db.CardTags.Any(ct => ct.CardId == c.Id && ct.TagId == tag));
        if (page.AssigneeId is { } assignee) query = query.Where(c => c.AssigneeId == assignee);
        if (page.Deadline is { } deadline && page.Today is { } today)
            query = deadline switch
            {
                DeadlineFilter.Overdue => query.Where(c => c.DueDate < today),
                DeadlineFilter.Today => query.Where(c => c.DueDate == today),
                _ => query.Where(c => c.DueDate > today),
            };

        var total = await query.CountAsync(token);
        var rows = await (from c in query join col in db.Columns on c.ColumnId equals col.Id
                          orderby col.Position, c.Position, c.Id select c).Skip(page.Offset).Take(page.Limit).ToListAsync(token);
        var summaries = await Summaries(rows, token);
        await access.RequireAsync(boardId, userId, false, token); await transaction.CommitAsync(token);
        return new PagedResult<CardSummaryDto>(summaries, total, page.Offset, page.Limit, Convert.ToBase64String(board.RowVersion));
        });
    }
    private async Task<IReadOnlyList<CardSummaryDto>> Summaries(List<Card> cards, CancellationToken token)
    {
        var ids = cards.Select(c => c.Id).ToArray(); var assignees = cards.Where(c => c.AssigneeId != null).Select(c => c.AssigneeId!.Value).Distinct().ToArray();
        var users = await db.Users.AsNoTracking().Where(u => assignees.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => new UserDto(u.Id, u.Email, u.Name), token);
        var tags = await (from ct in db.CardTags join t in db.Tags on ct.TagId equals t.Id where ids.Contains(ct.CardId)
                          orderby t.Name, t.Id select new { ct.CardId, Tag = new TagDto(t.Id, t.Name, t.Color) }).ToListAsync(token);
        var images = await db.CardImages.Where(x => ids.Contains(x.CardId)).Select(x => x.CardId).ToListAsync(token);
        return cards.Select(c => new CardSummaryDto(c.Id, c.ColumnId, c.Title, c.DueDate, c.Priority,
            c.AssigneeId is Guid id ? users.GetValueOrDefault(id) : null, c.Position, tags.Where(t => t.CardId == c.Id).Select(t => t.Tag).ToList(),
            images.Contains(c.Id), Convert.ToBase64String(c.RowVersion))).ToList();
    }
    private async Task<CardDetailsDto> Details(Card card, CancellationToken token)
    {
        var summary = (await Summaries([card], token))[0];
        var type = await db.CardImages.Where(x => x.CardId == card.Id).Select(x => x.ContentType).SingleOrDefaultAsync(token);
        return new(summary, card.Description, type, card.CreatedAtUtc, card.UpdatedAtUtc);
    }
    public async Task<CardDetailsDto> GetCard(Guid cardId, Guid userId, CancellationToken token)
    {
        var boardId = await CardBoard(cardId, userId, token);
        var card = await db.Cards.AsNoTracking().SingleOrDefaultAsync(x => x.Id == cardId, token) ?? throw Missing();
        var result = await Details(card, token); await access.RequireAsync(boardId, userId, false, token); return result;
    }
    private async Task Fields(Card card, Guid boardId, CardFieldsRequest request, CancellationToken token)
    {
        if (request.AssigneeId is Guid id && !await db.BoardMembers.AnyAsync(x => x.BoardId == boardId && x.UserId == id, token))
            throw new ApiException(400, "invalid_assignee", "Відповідальний має бути учасником цієї дошки.");
        if (request.TagIds.Count != request.TagIds.Distinct().Count() ||
            await db.Tags.CountAsync(x => x.BoardId == boardId && request.TagIds.Contains(x.Id), token) != request.TagIds.Count)
            throw new ApiException(400, "invalid_tags", "Теги мають бути унікальними й належати цій дошці.");
        card.Title = request.Title.Trim(); card.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        card.DueDate = request.DueDate; card.Priority = request.Priority; card.AssigneeId = request.AssigneeId; card.UpdatedAtUtc = DateTime.UtcNow;
        await db.CardTags.Where(x => x.CardId == card.Id).ExecuteDeleteAsync(token);
        foreach (var tag in request.TagIds) db.CardTags.Add(new CardTag { CardId = card.Id, TagId = tag });
    }
    public Task<CardDetailsDto> CreateCard(Guid boardId, Guid userId, CreateCardRequest request, CancellationToken token) =>
        access.MutateAsync(boardId, userId, request.ExpectedBoardVersion, false, async member =>
        {
            if (member.Role != BoardRole.Admin && !member.CanCreateCards)
                throw new ApiException(403, "forbidden", "Створення карток на цій дошці обмежено.");
            if (!await db.Columns.AnyAsync(x => x.Id == request.ColumnId && x.BoardId == boardId, token)) throw Missing();
            var card = new Card { ColumnId = request.ColumnId, CreatedById = userId, Position = await db.Cards.CountAsync(x => x.ColumnId == request.ColumnId, token) };
            db.Cards.Add(card); await Fields(card, boardId, request, token); await db.SaveChangesAsync(token); return await Details(card, token);
        }, token);
    public async Task<CardDetailsDto> UpdateCard(Guid cardId, Guid userId, UpdateCardRequest request, CancellationToken token)
    {
        var boardId = await CardBoard(cardId, userId, token);
        return await access.MutateAsync(boardId, userId, request.ExpectedBoardVersion, false, async _ =>
        {
            var card = await db.Cards.SingleOrDefaultAsync(x => x.Id == cardId, token) ?? throw Missing();
            Version(card.RowVersion, request.ExpectedVersion); await Fields(card, boardId, request, token);
            await db.SaveChangesAsync(token); return await Details(card, token);
        }, token);
    }
    public async Task<bool> DeleteCard(Guid cardId, Guid userId, ResourceVersionQuery request, CancellationToken token)
    {
        var boardId = await CardBoard(cardId, userId, token);
        return await access.MutateAsync(boardId, userId, request.ExpectedBoardVersion, false, async member =>
        {
            DeleteRight(member);
            var card = await db.Cards.AsNoTracking().SingleOrDefaultAsync(x => x.Id == cardId, token) ?? throw Missing();
            Version(card.RowVersion, request.ExpectedVersion);
            await DeleteCards(db.Cards.Where(x => x.Id == cardId), token);
            var rows = await db.Cards.Where(x => x.ColumnId == card.ColumnId).OrderBy(x => x.Position).ThenBy(x => x.Id).ToListAsync(token);
            for (var i = 0; i < rows.Count; i++) rows[i].Position = i;
            return true;
        }, token);
    }
    public async Task<IReadOnlyList<TagDto>> Tags(Guid boardId, Guid userId, CancellationToken token)
    {
        await access.RequireAsync(boardId, userId, false, token);
        var tags = await db.Tags.AsNoTracking().Where(x => x.BoardId == boardId).OrderBy(x => x.Name).ThenBy(x => x.Id).Select(x => new TagDto(x.Id, x.Name, x.Color)).ToListAsync(token);
        await access.RequireAsync(boardId, userId, false, token); return tags;
    }
    public Task<TagDto> CreateTag(Guid boardId, Guid userId, CreateTagRequest request, CancellationToken token) =>
        access.MutateAsync(boardId, userId, request.ExpectedBoardVersion, false, async _ =>
        {
            var name = request.Name.Trim(); var normalized = name.ToUpperInvariant();
            if (await db.Tags.AnyAsync(x => x.BoardId == boardId && x.NormalizedName == normalized, token))
                throw new ApiException(409, "tag_exists", "Тег із такою назвою вже існує на дошці.");
            var tag = new Tag { BoardId = boardId, Name = name, NormalizedName = normalized, Color = request.Color.ToUpperInvariant() };
            db.Tags.Add(tag); return new TagDto(tag.Id, tag.Name, tag.Color);
        }, token);
    public async Task<PagedResult<CommentDto>> Comments(Guid cardId, Guid userId, PageQuery page, CancellationToken token)
    {
        var boardId = await CardBoard(cardId, userId, token);
        var query = db.Comments.Where(x => x.CardId == cardId); var total = await query.CountAsync(token);
        var rows = await (from c in query join u in db.Users on c.AuthorId equals u.Id orderby c.CreatedAtUtc, c.Id
                          select new CommentDto(c.Id, c.CardId, new UserDto(u.Id, u.Email, u.Name), c.Text, c.CreatedAtUtc))
            .Skip(page.Offset).Take(page.Limit).ToListAsync(token);
        await access.RequireAsync(boardId, userId, false, token); return new(rows, total, page.Offset, page.Limit);
    }
    public async Task<CommentDto> AddComment(Guid cardId, Guid userId, CreateCommentRequest request, CancellationToken token)
    {
        var boardId = await CardBoard(cardId, userId, token);
        return await access.MutateAsync(boardId, userId, request.ExpectedBoardVersion, false, async _ =>
        {
            var card = await db.Cards.SingleOrDefaultAsync(x => x.Id == cardId, token) ?? throw Missing();
            Version(card.RowVersion, request.ExpectedVersion); card.UpdatedAtUtc = DateTime.UtcNow;
            var comment = new Comment { CardId = cardId, AuthorId = userId, Text = request.Text.Trim() }; db.Comments.Add(comment);
            var author = await db.Users.AsNoTracking().SingleAsync(x => x.Id == userId, token);
            return new CommentDto(comment.Id, cardId, new UserDto(author.Id, author.Email, author.Name), comment.Text, comment.CreatedAtUtc);
        }, token);
    }
}
