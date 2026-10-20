using System.Data;
using KanbanApi.Domain;
using KanbanApi.Infrastructure;
using KanbanContracts;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace KanbanApi.Application;

public sealed class ChatService(KanbanDbContext db, TimeProvider clock, NotificationService notifications)
{
    private static ApiException Missing() => new(404, "chat_not_found", "Чат не знайдено.");
    private static readonly UserDto Deleted = new(Guid.Empty, "", "Видалений користувач");
    private IQueryable<DirectChat> Mine(Guid user) => db.DirectChats.Where(c => c.FirstUserId == user || c.SecondUserId == user);
    private static Guid? Peer(DirectChat chat, Guid user) => chat.FirstUserId == user ? chat.SecondUserId : chat.FirstUserId;
    private static UserDto Present(User user) => new(user.Id, user.Email, user.Name, Convert.ToBase64String(user.RowVersion), user.AvatarData != null);
    public async Task<IReadOnlyList<ChatDto>> List(Guid user, CancellationToken token)
    {
        var rows = await Mine(user).AsNoTracking().Select(c => new
        {
            Chat = c,
            Last = db.DirectMessages.Where(m => m.ChatId == c.Id).OrderByDescending(m => m.Id).Select(m => new { m.Text, m.CreatedAtUtc }).FirstOrDefault(),
            Unread = db.DirectMessages.Count(m => m.ChatId == c.Id && m.SenderId != user && m.Id > (c.FirstUserId == user ? c.FirstReadId : c.SecondReadId))
        }).ToListAsync(token);
        var peers = rows.Select(r => Peer(r.Chat, user)).OfType<Guid>().Distinct().ToArray();
        var people = await db.Users.AsNoTracking().Where(u => peers.Contains(u.Id)).ToDictionaryAsync(u => u.Id, token);
        return rows.OrderByDescending(r => r.Last?.CreatedAtUtc ?? r.Chat.CreatedAtUtc).Select(r =>
        {
            var person = Peer(r.Chat, user) is {} id ? people.GetValueOrDefault(id) : null;
            return new ChatDto(r.Chat.Id, person is null ? Deleted : Present(person), person is not null && person.DeletionDueAtUtc is null, r.Last?.Text, r.Last?.CreatedAtUtc, r.Unread);
        }).ToList();
    }
    public async Task<ChatDto> Start(Guid user, Guid recipient, CancellationToken token)
    {
        if (recipient == Guid.Empty || recipient == user) throw new ApiException(400, "invalid_recipient", "Оберіть іншого учасника команди.");
        var person = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == recipient && u.DeletionDueAtUtc == null, token);
        if (person is null) throw Missing();
        var first = string.CompareOrdinal(user.ToString("N"), recipient.ToString("N")) < 0 ? user : recipient;
        var second = first == user ? recipient : user;
        var existing = await db.DirectChats.SingleOrDefaultAsync(c => c.FirstUserId == first && c.SecondUserId == second, token);
        if (existing is null)
        {
            if (!await db.BoardMembers.AnyAsync(m => m.UserId == user && db.BoardMembers.Any(p => p.UserId == recipient && p.BoardId == m.BoardId), token)) throw Missing();
            var chat = new DirectChat { FirstUserId = first, SecondUserId = second, CreatedAtUtc = clock.GetUtcNow().UtcDateTime };
            db.DirectChats.Add(chat);
            try { await db.SaveChangesAsync(token); }
            catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 }) { db.Entry(chat).State = EntityState.Detached; }
        }
        return (await List(user, token)).Single(c => c.Peer.Id == recipient);
    }
    public async Task<ChatMessagesDto> Messages(Guid user, Guid chatId, long? before, long? after, CancellationToken token)
    {
        if (before is <= 0 || after is < 0 || before.HasValue && after.HasValue) throw new ApiException(400, "invalid_cursor", "Некоректний запит історії.");
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async()=>
        {
        await using var tx=await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead,token);
        if (!await Mine(user).AnyAsync(c => c.Id == chatId, token)) throw Missing();
        var query = db.DirectMessages.AsNoTracking().Where(m => m.ChatId == chatId);
        if (before is {} b) query = query.Where(m => m.Id < b);
        if (after is {} a) query = query.Where(m => m.Id > a);
        var rows = await (after.HasValue ? query.OrderBy(m => m.Id) : query.OrderByDescending(m => m.Id))
            .Take(51).ToListAsync(token);
        var more = rows.Count > 50; var page = rows.Take(50).OrderBy(m => m.Id).ToList();
        var result=new ChatMessagesDto(await Present(user,page,token),more);await tx.CommitAsync(token);return result;
        });
    }
    public Task<ChatMessageDto> Send(Guid user, Guid chatId, SendChatMessageRequest request, CancellationToken token)
    {
        var text=(request.Text??"").Trim();
        if(request.ClientMessageId==Guid.Empty||request.Text is {Length:>2000}||request.SharedCardId==Guid.Empty||text.Length==0&&request.SharedCardId is null)
            throw new ApiException(400,"invalid_message","Введіть повідомлення до 2000 символів або оберіть картку.");
        if(text.Length==0)text="Поділилися карткою";
        return db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
            var chat = await Lock(user, chatId, token);
            var previous = await db.DirectMessages.SingleOrDefaultAsync(m => m.ChatId == chatId && m.ClientMessageId == request.ClientMessageId, token);
            if (previous is not null)
            {
                if (previous.SenderId != user || previous.Text != text || previous.SharedCardId != request.SharedCardId) throw new ApiException(409, "message_id_conflict", "Це повідомлення вже надіслано з іншим вмістом.");
                return (await Present(user,[previous],token))[0];
            }
            var peer = Peer(chat, user);
            if (peer is null || !await db.Users.AnyAsync(u => u.Id == peer && u.DeletionDueAtUtc == null, token))
                throw new ApiException(409, "recipient_unavailable", "Зараз цьому користувачу не можна написати.");
            if(request.SharedCardId is {} shared&&!await AvailableCards(user,[shared]).AnyAsync(token))throw CardMissing();
            var message = new DirectMessage { ChatId = chatId, SenderId = user, ClientMessageId = request.ClientMessageId, SharedCardId=request.SharedCardId, Text = text, CreatedAtUtc = clock.GetUtcNow().UtcDateTime };
            db.DirectMessages.Add(message); await db.SaveChangesAsync(token);
            notifications.Messaged(peer.Value,user,message);await db.SaveChangesAsync(token);var result=(await Present(user,[message],token))[0];await tx.CommitAsync(token);return result;
        });
    }
    public Task Read(Guid user, Guid chatId, long messageId, CancellationToken token) => db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(token);
        var chat = await Lock(user, chatId, token);
        if (!await db.DirectMessages.AnyAsync(m => m.ChatId == chatId && m.Id == messageId, token)) throw Missing();
        if (chat.FirstUserId == user) chat.FirstReadId = Math.Max(chat.FirstReadId, messageId);
        else chat.SecondReadId = Math.Max(chat.SecondReadId, messageId);
        await db.SaveChangesAsync(token);
        await db.Notifications.Where(n=>n.UserId==user&&n.Kind==NotificationKind.Message&&n.ChatId==chatId&&n.MessageId<=messageId&&n.ReadAtUtc==null)
            .ExecuteUpdateAsync(s=>s.SetProperty(n=>n.ReadAtUtc,clock.GetUtcNow().UtcDateTime),token);
        await tx.CommitAsync(token);
    });
    private async Task<DirectChat> Lock(Guid user, Guid chatId, CancellationToken token)
    {
        var chat = await db.DirectChats.FromSqlInterpolated($"SELECT * FROM DirectChats WITH (UPDLOCK, HOLDLOCK) WHERE Id = {chatId}").SingleOrDefaultAsync(token);
        if (chat is null || chat.FirstUserId != user && chat.SecondUserId != user) throw Missing();
        return chat;
    }
    private static ApiException CardMissing()=>new(404,"shared_card_unavailable","Картка недоступна. Перевірте доступ до дошки.");
    private IQueryable<SharedCardDto> AvailableCards(Guid user,Guid[] ids)=>
        from c in db.Cards.AsNoTracking() join col in db.Columns on c.ColumnId equals col.Id
        join b in db.Boards on col.BoardId equals b.Id join m in db.BoardMembers on b.Id equals m.BoardId
        where ids.Contains(c.Id)&&m.UserId==user&&!c.IsArchived&&!b.IsArchived
        select new SharedCardDto(c.Id,b.Id,c.Title,b.Name+" · "+col.Name,true);
    private async Task<IReadOnlyList<ChatMessageDto>> Present(Guid user,IReadOnlyList<DirectMessage> messages,CancellationToken token)
    {
        var ids=messages.Select(m=>m.SharedCardId).OfType<Guid>().Distinct().ToArray();
        var cards=await AvailableCards(user,ids).ToDictionaryAsync(c=>c.CardId!.Value,token);
        return messages.Select(m=>new ChatMessageDto(m.Id,m.SenderId,m.Text,m.CreatedAtUtc,m.SharedCardId is {} id?
            cards.GetValueOrDefault(id)??new SharedCardDto(null,null,"Картка недоступна","Немає доступу, картку видалено або архівовано.",false):null)).ToList();
    }
    public async Task<SharedCardDto> OpenShared(Guid user,Guid chat,long message,CancellationToken token)=>
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async()=>
        {
            await using var tx=await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead,token);
            if(!await Mine(user).AnyAsync(c=>c.Id==chat,token))throw Missing();
            var target=await db.DirectMessages.AsNoTracking().Where(m=>m.ChatId==chat&&m.Id==message).Select(m=>m.SharedCardId).SingleOrDefaultAsync(token);
            if(target is null)throw CardMissing();
            var result=await AvailableCards(user,[target.Value]).SingleOrDefaultAsync(token)??throw CardMissing();
            await tx.CommitAsync(token);return result;
        });
}
