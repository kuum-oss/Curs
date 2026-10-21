using System.Globalization;
using System.Text.Json;
using KanbanApi.Domain;
using KanbanApi.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace KanbanApi.Application;

// Snapshotting under BoardAccess's write lock also covers ExecuteUpdate/Delete paths.
public sealed class ActivityRecorder(KanbanDbContext db, TimeProvider clock)
{
    public sealed record State(Guid Id, string Title, string? Description, DateOnly? DueDate, string Priority,
        Guid? AssigneeId, string? Assignee, Guid ColumnId, string Column, bool Archived, DateTime Created);
    public Task<List<State>> Capture(Guid board, CancellationToken token) =>
        (from c in db.Cards.AsNoTracking() join col in db.Columns on c.ColumnId equals col.Id
         join u in db.Users on c.AssigneeId equals u.Id into people from u in people.DefaultIfEmpty()
         where col.BoardId == board
         select new State(c.Id,c.Title,c.Description,c.DueDate,c.Priority.ToString(),c.AssigneeId,u==null?null:u.Name,
             col.Id,col.Name,c.IsArchived,c.CreatedAtUtc)).ToListAsync(token);
    public async Task Record(Guid board, Guid actor, List<State> before, CancellationToken token)
    {
        // Deleting the board itself cascades its history; never recreate orphan records.
        if (!await db.Boards.AnyAsync(b=>b.Id==board,token)) return;
        var after = await Capture(board,token); var old = before.ToDictionary(c=>c.Id); var current = after.ToDictionary(c=>c.Id);
        var now = clock.GetUtcNow().UtcDateTime;
        void Add(State c,string field,string? from,string? to,Guid? fromColumn=null,Guid? toColumn=null) =>
            db.BoardActivities.Add(new BoardActivity { BoardId=board,CardId=c.Id,CardTitle=c.Title,ActorId=actor,Field=field,
                Before=from,After=to,BeforeStateJson=old.TryGetValue(c.Id,out var prior)?JsonSerializer.Serialize(prior):null,
                AfterStateJson=current.TryGetValue(c.Id,out var next)?JsonSerializer.Serialize(next):null,FromColumnId=fromColumn,ToColumnId=toColumn,CardCreatedAtUtc=c.Created,CreatedAtUtc=now });
        foreach(var c in after)
        {
            if(!old.TryGetValue(c.Id,out var b)) { Add(c,"created",null,c.Title,null,c.ColumnId); continue; }
            if(b.Title!=c.Title)Add(c,"title",b.Title,c.Title);
            if(b.Description!=c.Description)Add(c,"description",b.Description,c.Description);
            if(b.DueDate!=c.DueDate)Add(c,"deadline",b.DueDate?.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),c.DueDate?.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture));
            if(b.Priority!=c.Priority)Add(c,"priority",b.Priority,c.Priority);
            if(b.AssigneeId!=c.AssigneeId)Add(c,"assignee",b.Assignee,c.Assignee);
            if(b.ColumnId!=c.ColumnId)Add(c,"column",b.Column,c.Column,b.ColumnId,c.ColumnId);
            if(b.Archived!=c.Archived)Add(c,"archive",b.Archived?"Архів":"Активна",c.Archived?"Архів":"Активна");
        }
        foreach(var c in before.Where(c=>!current.ContainsKey(c.Id)))Add(c,"deleted",c.Title,null);
        var completion=await db.Boards.Where(b=>b.Id==board).Select(b=>b.CompletionColumnId).SingleAsync(token);
        if(completion is {} id && !await db.Columns.AnyAsync(c=>c.Id==id&&c.BoardId==board,token))
            await db.Boards.Where(b=>b.Id==board).ExecuteUpdateAsync(s=>s.SetProperty(b=>b.CompletionColumnId,(Guid?)null),token);
        await db.SaveChangesAsync(token);
    }
}
