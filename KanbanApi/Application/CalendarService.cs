using KanbanApi.Infrastructure;
using KanbanContracts;
using Microsoft.EntityFrameworkCore;
namespace KanbanApi.Application;
public sealed class CalendarService(KanbanDbContext db,BoardAccess access)
{
    public Task<PagedResult<CalendarCardDto>> List(Guid user,CalendarQuery request,CancellationToken token)=>db.Database.CreateExecutionStrategy().ExecuteAsync(async()=>
    {
        if(request.To<=request.From||request.To.DayNumber-request.From.DayNumber>42||request.Offset<0||request.Limit is <1 or >500||request.AssigneeId==Guid.Empty||request.Unassigned&&request.AssigneeId is not null)
            throw new ApiException(400,"invalid_calendar","Оберіть період до 42 днів та одного виконавця.");
        await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,token);
        var query=from c in db.Cards.AsNoTracking() join col in db.Columns on c.ColumnId equals col.Id
                  join b in db.Boards on col.BoardId equals b.Id join m in db.BoardMembers on b.Id equals m.BoardId
                  join u in db.Users on c.AssigneeId equals u.Id into people from u in people.DefaultIfEmpty()
                  where m.UserId==user&&!b.IsArchived&&!c.IsArchived&&(c.DueDate==null||c.DueDate>=request.From&&c.DueDate<request.To)
                    &&(!request.Unassigned||c.AssigneeId==null)&&(request.AssigneeId==null||c.AssigneeId==request.AssigneeId)
                  orderby c.DueDate,b.Name,col.Position,c.Position,c.Id
                  select new CalendarCardDto(c.Id,b.Id,c.Title,b.Name,col.Name,c.DueDate,c.Priority,
                      u==null?null:new UserDto(u.Id,u.Email,u.Name,Convert.ToBase64String(u.RowVersion),u.AvatarData!=null),Convert.ToBase64String(c.RowVersion),Convert.ToBase64String(b.RowVersion));
        var count=await query.CountAsync(token);var result=await query.Skip(request.Offset).Take(request.Limit).ToListAsync(token);
        await tx.CommitAsync(token);return new PagedResult<CalendarCardDto>(result,count,request.Offset,request.Limit);
    });
    public async Task Change(Guid user,Guid id,ChangeDeadlineRequest request,CancellationToken token)
    {
        var board=await (from c in db.Cards join col in db.Columns on c.ColumnId equals col.Id join m in db.BoardMembers on col.BoardId equals m.BoardId where c.Id==id&&m.UserId==user select (Guid?)col.BoardId).SingleOrDefaultAsync(token)??throw BoardAccess.NotFound();
        await access.MutateAsync(board,user,request.ExpectedBoardVersion,false,async _=>
        {
            var card=await db.Cards.SingleOrDefaultAsync(c=>c.Id==id,token)??throw BoardAccess.NotFound();
            if(card.IsArchived||await db.Boards.AnyAsync(b=>b.Id==board&&b.IsArchived,token))throw new ApiException(409,"archived","Спочатку відновіть картку та дошку з архіву.");
            if(Convert.ToBase64String(card.RowVersion)!=request.ExpectedVersion)throw new ApiException(409,"concurrency_conflict","Картку вже змінено. Оновіть календар і повторіть дію.");
            card.DueDate=request.DueDate;card.UpdatedAtUtc=DateTime.UtcNow;return true;
        },token);
    }
}
