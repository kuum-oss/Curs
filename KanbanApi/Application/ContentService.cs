using KanbanApi.Domain;
using KanbanApi.Infrastructure;
using KanbanContracts;
using Microsoft.EntityFrameworkCore;

namespace KanbanApi.Application;

public sealed class ContentService(KanbanDbContext db, BoardAccess access, NotificationService notifications)
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
        await db.ChecklistItems.Where(x => ids.Contains(x.CardId)).ExecuteDeleteAsync(token);
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
        var query = BoardCards(boardId).AsNoTracking().Where(c=>c.IsArchived==page.Archived);
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
        var users = await db.Users.AsNoTracking().Where(u => assignees.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => UserPresentation.Dto(u), token);
        var tags = await (from ct in db.CardTags join t in db.Tags on ct.TagId equals t.Id where ids.Contains(ct.CardId)
                          orderby t.Name, t.Id select new { ct.CardId, Tag = new TagDto(t.Id, t.Name, t.Color) }).ToListAsync(token);
        var images = await db.CardImages.Where(x => ids.Contains(x.CardId)).Select(x => x.CardId).ToListAsync(token);
        var comments = await db.Comments.Where(c=>ids.Contains(c.CardId)).GroupBy(c=>c.CardId).Select(g=>new{Id=g.Key,Count=g.Count()}).ToDictionaryAsync(g=>g.Id,g=>g.Count,token);
        var checks = await db.ChecklistItems.Where(i => ids.Contains(i.CardId)).GroupBy(i => i.CardId)
            .Select(g => new { Id = g.Key, Total = g.Count(), Done = g.Count(i => i.IsCompleted) }).ToDictionaryAsync(g => g.Id, token);
        return cards.Select(c => new CardSummaryDto(c.Id, c.ColumnId, c.Title, c.DueDate, c.Priority,
            c.AssigneeId is Guid id ? users.GetValueOrDefault(id) : null, c.Position, tags.Where(t => t.CardId == c.Id).Select(t => t.Tag).ToList(),
            images.Contains(c.Id), Convert.ToBase64String(c.RowVersion), c.Description, comments.GetValueOrDefault(c.Id), c.IsArchived, checks.GetValueOrDefault(c.Id)?.Done ?? 0, checks.GetValueOrDefault(c.Id)?.Total ?? 0, c.SourceAssigneeName)).ToList();
    }
    private async Task<CardDetailsDto> Details(Card card, CancellationToken token)
    {
        var summary = (await Summaries([card], token))[0];
        var type = await db.CardImages.Where(x => x.CardId == card.Id).Select(x => x.ContentType).SingleOrDefaultAsync(token);
        var items = await db.ChecklistItems.AsNoTracking().Where(i => i.CardId == card.Id).OrderBy(i => i.Position).ThenBy(i => i.Id)
            .Select(i => new ChecklistItemDto(i.Id, i.Title, i.IsCompleted, i.Position)).ToListAsync(token);
        return new(summary, card.Description, type, card.CreatedAtUtc, card.UpdatedAtUtc, items);
    }
    public async Task<CardDetailsDto> GetCard(Guid cardId, Guid userId, CancellationToken token)
    {
        var boardId = await CardBoard(cardId, userId, token);
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, token);
            var card = await db.Cards.AsNoTracking().SingleOrDefaultAsync(x => x.Id == cardId, token) ?? throw Missing();
            var result = await Details(card, token); await access.RequireAsync(boardId, userId, false, token);
            await transaction.CommitAsync(token); return result;
        });
    }
    private async Task Fields(Card card, Guid boardId, CardFieldsRequest request, CancellationToken token)
    {
        if (request.AssigneeId is Guid id && !await db.BoardMembers.AnyAsync(x => x.BoardId == boardId && x.UserId == id, token))
            throw new ApiException(400, "invalid_assignee", "Відповідальний має бути учасником цієї дошки.");
        if (request.TagIds.Count != request.TagIds.Distinct().Count() ||
            await db.Tags.CountAsync(x => x.BoardId == boardId && request.TagIds.Contains(x.Id), token) != request.TagIds.Count)
            throw new ApiException(400, "invalid_tags", "Теги мають бути унікальними й належати цій дошці.");
        card.Title = request.Title.Trim(); card.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        if(card.AssigneeId!=request.AssigneeId||request.AssigneeId is not null)card.SourceAssigneeName=null;
        card.DueDate = request.DueDate; card.Priority = request.Priority; card.AssigneeId = request.AssigneeId; card.UpdatedAtUtc = DateTime.UtcNow;
        await db.CardTags.Where(x => x.CardId == card.Id).ExecuteDeleteAsync(token);
        foreach (var tag in request.TagIds) db.CardTags.Add(new CardTag { CardId = card.Id, TagId = tag });
        if (request.Checklist is {} requested)
        {
            var ids = requested.Select(i => i.Id).ToArray();
            if (requested.Count > 100 || requested.Any(i => i.Id == Guid.Empty || string.IsNullOrWhiteSpace(i.Title) || i.Title.Length > 300) || ids.Distinct().Count() != ids.Length ||
                await db.ChecklistItems.AnyAsync(i => ids.Contains(i.Id) && i.CardId != card.Id, token))
                throw new ApiException(400, "invalid_checklist", "Перевірте текст та унікальність пунктів чек-листа.");
            var existing = await db.ChecklistItems.Where(i => i.CardId == card.Id).ToDictionaryAsync(i => i.Id, token);
            db.ChecklistItems.RemoveRange(existing.Values.Where(i => !ids.Contains(i.Id)));
            for (var position = 0; position < requested.Count; position++)
            {
                var input = requested[position];
                if (!existing.TryGetValue(input.Id, out var item)) { item = new ChecklistItem { Id = input.Id, CardId = card.Id }; db.ChecklistItems.Add(item); }
                item.Title = input.Title.Trim(); item.IsCompleted = input.IsCompleted; item.Position = position;
            }
        }
    }
    public Task<CardDetailsDto> CreateCard(Guid boardId, Guid userId, CreateCardRequest request, CancellationToken token) =>
        access.MutateAsync(boardId, userId, request.ExpectedBoardVersion, false, async member =>
        {
            if (member.Role != BoardRole.Admin && !member.CanCreateCards)
                throw new ApiException(403, "forbidden", "Створення карток на цій дошці обмежено.");
            if (!await db.Columns.AnyAsync(x => x.Id == request.ColumnId && x.BoardId == boardId, token)) throw Missing();
            var card = new Card { ColumnId = request.ColumnId, CreatedById = userId, Position = await db.Cards.CountAsync(x => x.ColumnId == request.ColumnId, token) };
            db.Cards.Add(card); await Fields(card, boardId, request, token); notifications.Assigned(card,userId); await db.SaveChangesAsync(token); return await Details(card, token);
        }, token);
    public async Task<CardDetailsDto> UpdateCard(Guid cardId, Guid userId, UpdateCardRequest request, CancellationToken token)
    {
        var boardId = await CardBoard(cardId, userId, token);
        return await access.MutateAsync(boardId, userId, request.ExpectedBoardVersion, false, async _ =>
        {
            var card = await db.Cards.SingleOrDefaultAsync(x => x.Id == cardId, token) ?? throw Missing();
            Version(card.RowVersion, request.ExpectedVersion); var oldAssignee=card.AssigneeId; await Fields(card, boardId, request, token);
            if(oldAssignee!=card.AssigneeId)notifications.Assigned(card,userId);
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
        var rows = await (from c in query join author in db.Users on c.AuthorId equals author.Id into authors
                          from u in authors.DefaultIfEmpty() orderby c.CreatedAtUtc, c.Id
                          select new CommentDto(c.Id, c.CardId, u == null ? new UserDto(Guid.Empty, "", c.SourceAuthorName==null?"Видалений користувач":c.SourceAuthorName+" · з копії") : new UserDto(u.Id, u.Email, u.Name, Convert.ToBase64String(u.RowVersion), u.AvatarData != null), c.Text, c.CreatedAtUtc, Convert.ToBase64String(c.RowVersion), c.EditedAtUtc))
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
            await notifications.Mentioned(comment,boardId,userId,"",token);
            var author = await db.Users.AsNoTracking().SingleAsync(x => x.Id == userId, token);
            await db.SaveChangesAsync(token);
            return new CommentDto(comment.Id, cardId, UserPresentation.Dto(author), comment.Text, comment.CreatedAtUtc, Convert.ToBase64String(comment.RowVersion));
        }, token);
    }
    public async Task<CardDetailsDto> Archive(Guid cardId,Guid userId,ArchiveCardRequest request,CancellationToken token)
    {
        var boardId=await CardBoard(cardId,userId,token);
        return await access.MutateAsync(boardId,userId,request.ExpectedBoardVersion,false,async member=>
        {
            DeleteRight(member);var card=await db.Cards.SingleAsync(c=>c.Id==cardId,token);Version(card.RowVersion,request.ExpectedVersion);
            card.IsArchived=request.Archived;card.UpdatedAtUtc=DateTime.UtcNow;
            if(!request.Archived)card.Position=await db.Cards.Where(c=>c.ColumnId==card.ColumnId).MaxAsync(c=>(int?)c.Position,token)+1??0;
            await db.SaveChangesAsync(token);return await Details(card,token);
        },token);
    }
    public async Task<CommentDto?> EditComment(Guid cardId,Guid commentId,Guid userId,string? text,CommentVersionQuery request,CancellationToken token)
    {
        var boardId=await CardBoard(cardId,userId,token);
        return await access.MutateAsync<CommentDto?>(boardId,userId,request.ExpectedBoardVersion,false,async member=>
        {
            var card=await db.Cards.SingleAsync(c=>c.Id==cardId,token);Version(card.RowVersion,request.ExpectedVersion);
            var comment=await db.Comments.SingleOrDefaultAsync(c=>c.Id==commentId && c.CardId==cardId,token)??throw Missing();Version(comment.RowVersion,request.ExpectedCommentVersion);
            if(comment.AuthorId!=userId && (text is not null || member.Role!=BoardRole.Admin))throw new ApiException(403,"forbidden","Можна редагувати лише власний коментар.");
            card.UpdatedAtUtc=DateTime.UtcNow;
            if(text is null){db.Comments.Remove(comment);return null;}
            if(string.IsNullOrWhiteSpace(text))throw new ApiException(400,"validation_failed","Введіть коментар.");
            var previous=comment.Text;comment.Text=text.Trim();comment.EditedAtUtc=DateTime.UtcNow;
            await notifications.Mentioned(comment,boardId,userId,previous,token);await db.SaveChangesAsync(token);
            var author=await db.Users.SingleAsync(u=>u.Id==comment.AuthorId,token);
            return new CommentDto(comment.Id,cardId,UserPresentation.Dto(author),comment.Text,comment.CreatedAtUtc,Convert.ToBase64String(comment.RowVersion),comment.EditedAtUtc);
        },token);
    }
    public async Task<PagedResult<PersonalCardDto>> Overview(Guid userId,PageQuery page,bool archived,bool assignedOnly,CancellationToken token)
    {
        var query=from card in db.Cards.AsNoTracking() join col in db.Columns on card.ColumnId equals col.Id
                  join board in db.Boards on col.BoardId equals board.Id join member in db.BoardMembers on board.Id equals member.BoardId
                  where member.UserId==userId && card.IsArchived==archived && (archived || !board.IsArchived) && (!assignedOnly || card.AssigneeId==userId)
                  orderby card.DueDate,board.Name,col.Position,card.Position select new{Card=card,BoardId=board.Id,BoardName=board.Name};
        var total=await query.CountAsync(token);var rows=await query.Skip(page.Offset).Take(page.Limit).ToListAsync(token);
        var summaries=await Summaries(rows.Select(r=>r.Card).ToList(),token);
        var allowed=await db.BoardMembers.Where(m=>m.UserId==userId).Select(m=>m.BoardId).ToListAsync(token);
        var result=rows.Where(r=>allowed.Contains(r.BoardId)).Select(r=>new PersonalCardDto(r.BoardId,r.BoardName,summaries.Single(c=>c.Id==r.Card.Id))).ToList();
        return new(result,total,page.Offset,page.Limit);
    }
    public async Task<IReadOnlyList<UserDto>> Team(Guid userId,CancellationToken token)=>
        await db.Users.AsNoTracking().Where(u=>db.BoardMembers.Any(peer=>peer.UserId==u.Id && db.BoardMembers.Any(caller=>caller.UserId==userId && caller.BoardId==peer.BoardId)))
            .OrderBy(u=>u.Name).Select(u=>new UserDto(u.Id,u.Email,u.Name,Convert.ToBase64String(u.RowVersion),u.AvatarData!=null)).ToListAsync(token);
}
