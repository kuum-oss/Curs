using System.Globalization;
using System.Text.Json;
using KanbanApi.Domain;
using KanbanApi.Infrastructure;
using KanbanContracts;
using Microsoft.EntityFrameworkCore;
namespace KanbanApi.Application;
public sealed class InsightsService(KanbanDbContext db, BoardAccess access, ActivityRecorder recorder, TimeProvider clock)
{
    private async Task<List<ActivityDto>> Present(List<BoardActivity> rows, CancellationToken token)
    {
        var actors=rows.Where(r=>r.ActorId!=null).Select(r=>r.ActorId!.Value).Distinct().ToArray();
        var users=await db.Users.AsNoTracking().Where(u=>actors.Contains(u.Id)).ToDictionaryAsync(u=>u.Id,u=>new UserDto(u.Id,"",u.Name,Convert.ToBase64String(u.RowVersion),u.AvatarData!=null),token);
        var ids=rows.Select(r=>r.CardId).Distinct().ToArray();var live=await db.Cards.Where(c=>ids.Contains(c.Id)).Select(c=>c.Id).ToListAsync(token);
        return rows.Select(r=>new ActivityDto(r.Id,r.BoardId,r.CardId,r.CardTitle,
            r.ActorId is {} id && users.TryGetValue(id,out var user)?user:new UserDto(Guid.Empty,"","Видалений користувач"),
            r.Field,r.Before,r.After,r.CreatedAtUtc,live.Contains(r.CardId))).ToList();
    }
    public Task<PagedResult<ActivityDto>> History(Guid board, Guid user, HistoryQuery query, CancellationToken token) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async()=>
    {
        await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,token);
        await access.RequireAsync(board,user,false,token);
        var since=clock.GetUtcNow().UtcDateTime.AddDays(-query.Days);
        var rows=db.BoardActivities.AsNoTracking().Where(a=>a.BoardId==board);
        if(query.Days>0)rows=rows.Where(a=>a.CreatedAtUtc>=since);
        if(query.CardId is {} card)rows=rows.Where(a=>a.CardId==card);
        if(query.ActorId is {} actor)rows=rows.Where(a=>a.ActorId==actor);
        if(query.Field is {} field)rows=rows.Where(a=>a.Field==field);
        if(!string.IsNullOrWhiteSpace(query.Search)){var term=query.Search.Trim();rows=rows.Where(a=>EF.Functions.Collate(a.CardTitle,"Latin1_General_100_CI_AS_SC").Contains(term));}
        var count=await rows.CountAsync(token);
        var result=await Present(await rows.OrderByDescending(a=>a.CreatedAtUtc).ThenByDescending(a=>a.Id).Skip(query.Offset).Take(query.Limit).ToListAsync(token),token);
        await access.RequireAsync(board,user,false,token);await tx.CommitAsync(token);
        return new PagedResult<ActivityDto>(result,count,query.Offset,query.Limit);
    });
    public Task SetCompletion(Guid board, Guid user, CompletionColumnRequest request, CancellationToken token) =>
        access.MutateAsync(board,user,request.ExpectedBoardVersion,true,async _=>
        {
            if(request.ColumnId is {} id && !await db.Columns.AnyAsync(c=>c.BoardId==board&&c.Id==id,token))throw BoardAccess.NotFound();
            await db.Boards.Where(b=>b.Id==board).ExecuteUpdateAsync(s=>s.SetProperty(b=>b.CompletionColumnId,request.ColumnId),token);
            return true;
        },token);
    public Task<BoardStatisticsDto> Statistics(Guid board, Guid user, StatisticsQuery request, CancellationToken token) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async()=>
    {
        await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,token);
        await access.RequireAsync(board,user,false,token);
        var b=await db.Boards.AsNoTracking().SingleAsync(b=>b.Id==board,token);
        var columns=await db.Columns.AsNoTracking().Where(c=>c.BoardId==board).OrderBy(c=>c.Position).Select(c=>new ColumnDto(c.Id,c.BoardId,c.Name,c.Position,Convert.ToBase64String(c.RowVersion))).ToListAsync(token);
        var current=await recorder.Capture(board,token);var today=request.Today;
        var first=today.AddDays(-6);var previousFirst=first.AddDays(-7);
        DateTime Utc(DateOnly d)=>DateTime.SpecifyKind(d.ToDateTime(TimeOnly.MinValue).AddMinutes(-request.UtcOffsetMinutes),DateTimeKind.Utc);
        var start=Utc(first);var end=Utc(today.AddDays(1));var previousStart=Utc(previousFirst);
        var hasPrevious=b.InsightsSinceUtc<=previousStart;
        var done=b.CompletionColumnId;
        bool Active(ActivityRecorder.State c)=>!c.Archived&&c.ColumnId!=done;
        var active=current.Where(Active).ToList();
        int Overdue(IEnumerable<ActivityRecorder.State> cards,DateOnly date)=>cards.Count(c=>Active(c)&&c.DueDate<date);
        // First observed completion per card: reopening/reclosing cannot inflate throughput.
        var completions=done is null?[]:await db.BoardActivities.AsNoTracking()
            .Where(a=>a.BoardId==board&&a.ToColumnId==done&&(a.Field=="column"||a.Field=="created"))
            .GroupBy(a=>a.CardId).Select(g=>new{At=g.Min(a=>a.CreatedAtUtc),Created=g.Min(a=>a.CardCreatedAtUtc)}).ToListAsync(token);
        var finished=completions.Where(c=>c.At>=start&&c.At<end).ToList();
        var previousFinished=completions.Where(c=>c.At>=previousStart&&c.At<start).ToList();
        double? Mean(IEnumerable<double> values){var a=values.ToArray();return a.Length==0?null:a.Average();}
        List<ActivityRecorder.State> previous=[];
        if(hasPrevious)
        {
            var events=await db.BoardActivities.AsNoTracking().Where(a=>a.BoardId==board)
                .Select(a=>new{a.Id,a.CardId,a.CreatedAtUtc,a.BeforeStateJson,a.AfterStateJson}).ToListAsync(token);
            var now=current.ToDictionary(c=>c.Id);
            foreach(var id in now.Keys.Concat(events.Select(e=>e.CardId)).Distinct())
            {
                var rows=events.Where(e=>e.CardId==id).OrderBy(e=>e.CreatedAtUtc).ThenBy(e=>e.Id).ToList();
                var prior=rows.LastOrDefault(e=>e.CreatedAtUtc<start);
                var json=prior is not null?prior.AfterStateJson:rows.FirstOrDefault()?.BeforeStateJson;
                var state=json is not null?JsonSerializer.Deserialize<ActivityRecorder.State>(json):rows.Count==0?now.GetValueOrDefault(id):null;
                if(state is not null&&state.Created<start)previous.Add(state);
            }
        }
        var members=await (from m in db.BoardMembers.AsNoTracking() join u in db.Users on m.UserId equals u.Id where m.BoardId==board orderby u.Name select new UserDto(u.Id,"",u.Name,Convert.ToBase64String(u.RowVersion),u.AvatarData!=null)).ToListAsync(token);
        var workload=members.Select(u=>new WorkloadDto(u,active.Count(c=>c.AssigneeId==u.Id),current.Count(c=>!c.Archived&&c.AssigneeId==u.Id))).ToList();
        if(current.Any(c=>!c.Archived&&c.AssigneeId==null))workload.Add(new(new UserDto(Guid.Empty,"","Без відповідального"),active.Count(c=>c.AssigneeId==null),current.Count(c=>!c.Archived&&c.AssigneeId==null)));
        var recent=await Present(await db.BoardActivities.AsNoTracking().Where(a=>a.BoardId==board).OrderByDescending(a=>a.CreatedAtUtc).ThenByDescending(a=>a.Id).Take(4).ToListAsync(token),token);
        var days=Enumerable.Range(0,7).Select(i=>first.AddDays(i)).Select(d=>new DailyCompletionDto(d,finished.Count(c=>c.At>=Utc(d)&&c.At<Utc(d.AddDays(1))))).ToList();
        await access.RequireAsync(board,user,false,token);await tx.CommitAsync(token);
        return new BoardStatisticsDto(board,Convert.ToBase64String(b.RowVersion),done,columns,b.InsightsSinceUtc,hasPrevious,
            finished.Count,hasPrevious?previousFinished.Count:null,Overdue(current,today),hasPrevious?Overdue(previous,today.AddDays(-7)):null,
            active.Count,hasPrevious?previous.Count(Active):null,Mean(finished.Select(c=>Math.Max(0,(c.At-c.Created).TotalDays))),
            hasPrevious?Mean(previousFinished.Select(c=>Math.Max(0,(c.At-c.Created).TotalDays))):null,
            active.Count(c=>c.DueDate<today.AddDays(-7)),days,workload,recent);
    });
}
