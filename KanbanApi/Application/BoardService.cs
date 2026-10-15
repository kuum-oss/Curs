using KanbanApi.Domain;
using KanbanApi.Infrastructure;
using KanbanContracts;
using Microsoft.EntityFrameworkCore;

namespace KanbanApi.Application;

public sealed class BoardService(KanbanDbContext db, BoardAccess access)
{
    public async Task<IReadOnlyList<BoardSummaryDto>> ListAsync(Guid userId, CancellationToken token)
    {
        var rows = await (from b in db.Boards.AsNoTracking() join m in db.BoardMembers on b.Id equals m.BoardId
            where m.UserId == userId orderby b.Name, b.Id
            select new BoardSummaryDto(b.Id, b.Name, b.Description, m.Role,
                db.Cards.Count(c => !c.IsArchived && db.Columns.Any(col => col.Id == c.ColumnId && col.BoardId == b.Id)),
                db.BoardMembers.Count(x => x.BoardId == b.Id), null, b.CoverThumbnailData != null,
                Convert.ToBase64String(b.RowVersion), m.IsFavorite, b.IsArchived, m.LastOpenedAtUtc)).ToListAsync(token);
        var ids = rows.Select(b => b.Id).ToArray();
        var members = await (from m in db.BoardMembers.AsNoTracking() join u in db.Users on m.UserId equals u.Id
            where ids.Contains(m.BoardId) && db.BoardMembers.Any(caller => caller.BoardId == m.BoardId && caller.UserId == userId)
            orderby m.Role, u.Name, u.Id
            select new { m.BoardId, User = new UserDto(u.Id,u.Email,u.Name,Convert.ToBase64String(u.RowVersion),u.AvatarData != null) }).ToListAsync(token);
        return rows.Select(b => b with { Participants = members.Where(m => m.BoardId == b.Id).Select(m => m.User).Take(4).ToList() }).ToList();
    }

    public async Task<BoardDetailsDto> GetAsync(Guid boardId, Guid userId, CancellationToken token) =>
        await (from b in db.Boards.AsNoTracking() join m in db.BoardMembers on b.Id equals m.BoardId
               where b.Id == boardId && m.UserId == userId
               select new BoardDetailsDto(b.Id, b.Name, b.Description, m.Role, m.CanCreateCards, m.CanDeleteCards,
                   Convert.ToBase64String(b.RowVersion), b.CoverThumbnailData != null, b.IsArchived, m.IsFavorite, b.OwnerId)).SingleOrDefaultAsync(token) ?? throw BoardAccess.NotFound();

    public async Task<BoardDetailsDto> CreateAsync(Guid userId, CreateBoardRequest request, CancellationToken token)
    {
        if (!await db.Users.AnyAsync(x => x.Id == userId, token))
            throw new ApiException(401, "unauthorized", "Увійдіть знову.");
        var board = new Board { OwnerId = userId, Name = request.Name.Trim(), Description = Description(request.Description) };
        db.Boards.Add(board);
        db.BoardMembers.Add(new BoardMember { BoardId = board.Id, UserId = userId, Role = BoardRole.Admin });
        // One SaveChanges transaction creates board and mandatory administrator together.
        await db.SaveChangesAsync(token);
        return await GetAsync(board.Id, userId, token);
    }

    public Task<BoardDetailsDto> UpdateAsync(Guid boardId, Guid userId, UpdateBoardRequest request, CancellationToken token) =>
        access.MutateAsync(boardId, userId, request.ExpectedBoardVersion, true, async _ =>
        {
            await db.Boards.Where(x => x.Id == boardId).ExecuteUpdateAsync(set => set
                .SetProperty(x => x.Name, request.Name.Trim()).SetProperty(x => x.Description, Description(request.Description)), token);
            return await GetAsync(boardId, userId, token);
        }, token);

    public Task<bool> DeleteAsync(Guid boardId, Guid userId, string version, CancellationToken token) =>
        access.MutateAsync(boardId, userId, version, true, async _ =>
        {
            var columns = db.Columns.Where(x => x.BoardId == boardId).Select(x => x.Id);
            var cards = db.Cards.Where(x => columns.Contains(x.ColumnId)).Select(x => x.Id);
            var tags = db.Tags.Where(x => x.BoardId == boardId).Select(x => x.Id);
            await db.CardTags.Where(x => cards.Contains(x.CardId) || tags.Contains(x.TagId)).ExecuteDeleteAsync(token);
            await db.CardImages.Where(x => cards.Contains(x.CardId)).ExecuteDeleteAsync(token);
            await db.Comments.Where(x => cards.Contains(x.CardId)).ExecuteDeleteAsync(token);
            await db.Cards.Where(x => columns.Contains(x.ColumnId)).ExecuteDeleteAsync(token);
            await db.Columns.Where(x => x.BoardId == boardId).ExecuteDeleteAsync(token);
            await db.Tags.Where(x => x.BoardId == boardId).ExecuteDeleteAsync(token);
            await db.BoardMembers.Where(x => x.BoardId == boardId).ExecuteDeleteAsync(token);
            await db.Boards.Where(x => x.Id == boardId).ExecuteDeleteAsync(token);
            return true;
        }, token);

    public async Task<IReadOnlyList<MemberDto>> MembersAsync(Guid boardId, Guid userId, CancellationToken token)
    {
        await access.RequireAsync(boardId, userId, false, token);
        var result = await (from m in db.BoardMembers.AsNoTracking() join u in db.Users on m.UserId equals u.Id
            where m.BoardId == boardId && db.BoardMembers.Any(caller => caller.BoardId == boardId && caller.UserId == userId)
            orderby m.Role, u.Name, u.Id
            select new MemberDto(new UserDto(u.Id, u.Email, u.Name, Convert.ToBase64String(u.RowVersion), u.AvatarData != null), m.Role, m.CanCreateCards, m.CanDeleteCards)).ToListAsync(token);
        if (result.Count == 0) throw BoardAccess.NotFound();
        return result;
    }

    public Task<MemberDto> AddMemberAsync(Guid boardId, Guid userId, AddMemberRequest request, CancellationToken token) =>
        access.MutateAsync(boardId, userId, request.ExpectedBoardVersion, true, async _ =>
        {
            var email = request.Email.Trim().ToUpperInvariant();
            var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.NormalizedEmail == email, token)
                ?? throw new ApiException(404, "user_not_found", "Зареєстрованого користувача з цим email не знайдено.");
            if (await db.BoardMembers.AnyAsync(x => x.BoardId == boardId && x.UserId == user.Id, token))
                throw new ApiException(409, "member_exists", "Користувач уже має доступ до дошки.");
            var member = new BoardMember { BoardId = boardId, UserId = user.Id };
            db.BoardMembers.Add(member);
            return new MemberDto(UserPresentation.Dto(user), member.Role, true, true);
        }, token);

    public Task<MemberDto> UpdatePermissionsAsync(Guid boardId, Guid userId, Guid targetId, UpdateMemberPermissionsRequest request, CancellationToken token) =>
        access.MutateAsync(boardId, userId, request.ExpectedBoardVersion, true, async _ =>
        {
            var member = await TargetAsync(boardId, targetId, token);
            ProtectAdmin(member);
            member.CanCreateCards = request.CanCreateCards; member.CanDeleteCards = request.CanDeleteCards;
            var user = await db.Users.AsNoTracking().SingleAsync(x => x.Id == targetId, token);
            return new MemberDto(UserPresentation.Dto(user), member.Role, member.CanCreateCards, member.CanDeleteCards);
        }, token);

    public Task<bool> RemoveMemberAsync(Guid boardId, Guid userId, Guid targetId, string version, CancellationToken token) =>
        access.MutateAsync(boardId, userId, version, true, async _ =>
        {
            var member = await TargetAsync(boardId, targetId, token);
            var owner = await db.Boards.Where(b => b.Id == boardId).Select(b => b.OwnerId).SingleAsync(token);
            if (targetId == owner || (member.Role == BoardRole.Admin && (owner is null || userId != owner)))
                throw new ApiException(409, "admin_protected", "Власника або захищеного адміністратора не можна вилучити.");
            var columns = db.Columns.Where(x => x.BoardId == boardId).Select(x => x.Id);
            await db.Cards.Where(x => columns.Contains(x.ColumnId) && x.AssigneeId == targetId)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.AssigneeId, (Guid?)null)
                    .SetProperty(x => x.UpdatedAtUtc, DateTime.UtcNow), token);
            // Comments and CreatedById are intentionally preserved.
            db.BoardMembers.Remove(member);
            return true;
        }, token);

    public Task<BoardDetailsDto> ArchiveAsync(Guid boardId, Guid userId, SetArchiveRequest request, CancellationToken token) =>
        access.MutateAsync(boardId,userId,request.ExpectedBoardVersion,true,async _ =>
        {
            await db.Boards.Where(b=>b.Id==boardId).ExecuteUpdateAsync(set=>set.SetProperty(b=>b.IsArchived,request.Archived),token);
            return await GetAsync(boardId,userId,token);
        },token);
    public async Task FavoriteAsync(Guid boardId,Guid userId,bool favorite,CancellationToken token)
    {
        await access.RequireAsync(boardId,userId,false,token);
        await db.BoardMembers.Where(m=>m.BoardId==boardId && m.UserId==userId).ExecuteUpdateAsync(s=>s.SetProperty(m=>m.IsFavorite,favorite),token);
    }
    public async Task VisitAsync(Guid boardId,Guid userId,CancellationToken token)
    {
        await access.RequireAsync(boardId,userId,false,token);
        await db.BoardMembers.Where(m=>m.BoardId==boardId && m.UserId==userId).ExecuteUpdateAsync(s=>s.SetProperty(m=>m.LastOpenedAtUtc,DateTime.UtcNow),token);
    }
    public Task<IReadOnlyList<MemberDto>> SettingsAsync(Guid boardId,Guid userId,UpdateMembersRequest request,CancellationToken token) =>
        access.MutateAsync<IReadOnlyList<MemberDto>>(boardId,userId,request.ExpectedBoardVersion,true,async _ =>
        {
            if(request.Members.Select(m=>m.UserId).Distinct().Count()!=request.Members.Count)
                throw new ApiException(400,"validation_failed","Учасники не повинні повторюватися.");
            var owner=await db.Boards.Where(b=>b.Id==boardId).Select(b=>b.OwnerId).SingleAsync(token);
            var all=await db.BoardMembers.Where(m=>m.BoardId==boardId).ToListAsync(token);
            foreach(var setting in request.Members)
            {
                var target=all.SingleOrDefault(m=>m.UserId==setting.UserId)??throw BoardAccess.NotFound();
                if((target.UserId==owner || owner is null && target.Role==BoardRole.Admin) &&
                   (setting.Role!=BoardRole.Admin || !setting.CanCreateCards || !setting.CanDeleteCards))
                    throw new ApiException(409,"admin_protected","Права власника дошки захищені.");
                target.Role=setting.Role;target.CanCreateCards=setting.Role==BoardRole.Admin || setting.CanCreateCards;
                target.CanDeleteCards=setting.Role==BoardRole.Admin || setting.CanDeleteCards;
            }
            if(!all.Any(m=>m.Role==BoardRole.Admin))throw new ApiException(409,"admin_protected","Дошка повинна мати адміністратора.");
            await db.SaveChangesAsync(token);return await MembersAsync(boardId,userId,token);
        },token);

    private async Task<BoardMember> TargetAsync(Guid boardId, Guid targetId, CancellationToken token) =>
        await db.BoardMembers.SingleOrDefaultAsync(x => x.BoardId == boardId && x.UserId == targetId, token) ?? throw BoardAccess.NotFound();
    private static void ProtectAdmin(BoardMember member)
    {
        if (member.Role == BoardRole.Admin)
            throw new ApiException(409, "admin_protected", "Не можна відкликати доступ або змінити дозволи адміністратора.");
    }
    private static string? Description(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
