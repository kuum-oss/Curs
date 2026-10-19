using System.Data;
using System.Text.RegularExpressions;
using KanbanApi.Domain;
using KanbanApi.Infrastructure;
using KanbanContracts;
using Microsoft.EntityFrameworkCore;
namespace KanbanApi.Application;

public sealed class NotificationService(KanbanDbContext db, TimeProvider clock)
{
    public void Assigned(Card card, Guid actor)
    {
        if (card.AssigneeId is {} user && user != actor)
            db.Notifications.Add(new() { UserId=user, ActorId=actor, Kind=NotificationKind.Assignment,
                CardId=card.Id, EventKey="assignment:"+Guid.NewGuid(), CreatedAtUtc=clock.GetUtcNow().UtcDateTime });
    }
    private static bool Mentions(string text,string email) => Regex.IsMatch(text,
        @"(?<![\w@.+-])@"+Regex.Escape(email)+@"(?![\w@+-]|\.[\w])",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
    public async Task Mentioned(Comment comment,Guid board,Guid actor,string previous,CancellationToken token)
    {
        var people=await (from m in db.BoardMembers join u in db.Users on m.UserId equals u.Id
            where m.BoardId==board && u.Id!=actor && u.DeletionDueAtUtc==null select new{u.Id,u.Email}).ToListAsync(token);
        var existing=await db.Notifications.Where(n=>n.Kind==NotificationKind.Mention&&n.CommentId==comment.Id).Select(n=>n.UserId).ToListAsync(token);
        foreach(var person in people.Where(p=>Mentions(comment.Text,p.Email)&&!Mentions(previous,p.Email)&&!existing.Contains(p.Id)))
            db.Notifications.Add(new(){UserId=person.Id,ActorId=actor,Kind=NotificationKind.Mention,CardId=comment.CardId,
                CommentId=comment.Id,EventKey="mention:"+comment.Id,CreatedAtUtc=clock.GetUtcNow().UtcDateTime});
    }
    public void Messaged(Guid recipient,Guid actor,DirectMessage message) => db.Notifications.Add(new(){UserId=recipient,
        ActorId=actor,Kind=NotificationKind.Message,ChatId=message.ChatId,MessageId=message.Id,
        EventKey="message:"+message.Id,CreatedAtUtc=clock.GetUtcNow().UtcDateTime});

    private Task Deadlines(Guid user,CancellationToken token)
    {
        var today=DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);var tomorrow=today.AddDays(1);var now=clock.GetUtcNow().UtcDateTime;
        // One atomic insert per user/card/due date, including concurrent clients and execution-strategy retries.
        return db.Database.CreateExecutionStrategy().ExecuteAsync(()=>db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO Notifications (UserId,Kind,EventKey,CardId,DueDate,CreatedAtUtc)
SELECT {user},2,CONCAT('due:',CONVERT(varchar(36),c.Id),':',CONVERT(char(10),c.DueDate,23)),c.Id,c.DueDate,{now}
FROM Cards c JOIN Columns col ON col.Id=c.ColumnId JOIN Boards b ON b.Id=col.BoardId
JOIN BoardMembers m ON m.BoardId=b.Id AND m.UserId={user}
WHERE c.AssigneeId={user} AND c.IsArchived=0 AND b.IsArchived=0 AND c.DueDate BETWEEN {today} AND {tomorrow}
AND NOT EXISTS (SELECT 1 FROM Notifications n WITH(UPDLOCK,HOLDLOCK)
 WHERE n.UserId={user} AND n.EventKey=CONCAT('due:',CONVERT(varchar(36),c.Id),':',CONVERT(char(10),c.DueDate,23)))",token));
    }
    public async Task<NotificationsDto> List(Guid user,bool unread,long? before,CancellationToken token)
    {
        if(before is <=0)throw new ApiException(400,"invalid_cursor","Некоректна сторінка сповіщень.");
        await Deadlines(user,token);
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async()=>
        {
            await using var tx=await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead,token);
            var mine=db.Notifications.AsNoTracking().Where(n=>n.UserId==user);
            var count=await mine.CountAsync(n=>n.ReadAtUtc==null,token);var latest=await mine.MaxAsync(n=>(long?)n.Id,token)??0;
            var query=unread?mine.Where(n=>n.ReadAtUtc==null):mine;
            if(before is {} id)query=query.Where(n=>n.Id<id);
            var rows=await query.OrderByDescending(n=>n.Id).Take(51).ToListAsync(token);
            var items=await Present(user,rows.Take(50).ToList(),token);await tx.CommitAsync(token);
            return new NotificationsDto(items,count,latest,rows.Count>50);
        });
    }
    private async Task<IReadOnlyList<NotificationDto>> Present(Guid user,List<Notification> rows,CancellationToken token)
    {
        var cardIds=rows.Select(n=>n.CardId).OfType<Guid>().ToArray();
        var cards=await (from c in db.Cards.AsNoTracking() join col in db.Columns on c.ColumnId equals col.Id
            join b in db.Boards on col.BoardId equals b.Id join m in db.BoardMembers on b.Id equals m.BoardId
            where cardIds.Contains(c.Id)&&m.UserId==user&&!c.IsArchived&&!b.IsArchived
            select new{c.Id,c.Title,c.DueDate,c.AssigneeId,BoardId=b.Id,BoardName=b.Name}).ToDictionaryAsync(c=>c.Id,token);
        var commentIds=rows.Select(n=>n.CommentId).OfType<Guid>().ToArray();
        var comments=await db.Comments.Where(c=>commentIds.Contains(c.Id)).ToDictionaryAsync(c=>c.Id,c=>c.CardId,token);
        var chatIds=rows.Select(n=>n.ChatId).OfType<Guid>().ToArray();
        var chats=await db.DirectChats.Where(c=>chatIds.Contains(c.Id)&&(c.FirstUserId==user||c.SecondUserId==user)).Select(c=>c.Id).ToListAsync(token);
        var messageIds=rows.Select(n=>n.MessageId).OfType<long>().ToArray();
        var messages=await db.DirectMessages.Where(m=>messageIds.Contains(m.Id)&&chats.Contains(m.ChatId)).ToDictionaryAsync(m=>m.Id,token);
        var actorIds=rows.Select(n=>n.ActorId).OfType<Guid>().ToArray();
        var people=await db.Users.Where(u=>actorIds.Contains(u.Id)).ToDictionaryAsync(u=>u.Id,u=>u.Name,token);
        return rows.Select(n=>
        {
            var c=n.CardId is {} cid?cards.GetValueOrDefault(cid):null;
            var message=n.MessageId is {} mid?messages.GetValueOrDefault(mid):null;
            var available=n.Kind==NotificationKind.Message?message is not null&&message.ChatId==n.ChatId:
                c is not null&&(n.Kind!=NotificationKind.Mention||(n.CommentId is {} co&&comments.GetValueOrDefault(co)==c.Id))&&
                (n.Kind!=NotificationKind.Deadline||c.DueDate==n.DueDate&&c.AssigneeId==user);
            var actor=n.ActorId is {} aid?people.GetValueOrDefault(aid)??"Видалений користувач":"Видалений користувач";
            var title=!available?"Сповіщення недоступне":n.Kind switch{
                NotificationKind.Assignment=>"Вам призначено завдання",NotificationKind.Mention=>actor+" згадує вас у коментарі",
                NotificationKind.Deadline=>"Наближається дедлайн",_=>"Нове повідомлення від "+actor};
            var context=!available?"Картку видалено, архівовано або доступ відкликано.":n.Kind==NotificationKind.Message?
                (message!.Text.Length>100?message.Text[..100]+"…":message.Text):c!.Title+" · "+c.BoardName+(n.Kind==NotificationKind.Deadline?" · "+n.DueDate?.ToString("dd.MM.yyyy"):"");
            return new NotificationDto(n.Id,n.Kind,title,context,n.CreatedAtUtc,n.ReadAtUtc!=null,available,
                available?c?.BoardId:null,available?n.CardId:null,available?n.ChatId:null);
        }).ToList();
    }
    public async Task<NotificationDto> Open(Guid user,long id,CancellationToken token)
    {
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async()=>
        {
            db.ChangeTracker.Clear();await using var tx=await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead,token);
            var row=await db.Notifications.SingleOrDefaultAsync(n=>n.Id==id&&n.UserId==user,token)??throw BoardAccess.NotFound();
            var result=(await Present(user,[row],token))[0];if(!result.CanOpen)throw BoardAccess.NotFound();
            row.ReadAtUtc??=clock.GetUtcNow().UtcDateTime;await db.SaveChangesAsync(token);await tx.CommitAsync(token);return result with{IsRead=true};
        });
    }
    public async Task Read(Guid user,long id,CancellationToken token)
    {
        var now=clock.GetUtcNow().UtcDateTime;
        if(await db.Notifications.Where(n=>n.Id==id&&n.UserId==user).ExecuteUpdateAsync(s=>s.SetProperty(n=>n.ReadAtUtc,n=>n.ReadAtUtc??now),token)==0)throw BoardAccess.NotFound();
    }
    public Task ReadAll(Guid user,long through,CancellationToken token)=>db.Notifications.Where(n=>n.UserId==user&&n.Id<=through&&n.ReadAtUtc==null)
        .ExecuteUpdateAsync(s=>s.SetProperty(n=>n.ReadAtUtc,clock.GetUtcNow().UtcDateTime),token);
}
