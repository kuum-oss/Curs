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
        if (!await Mine(user).AnyAsync(c => c.Id == chatId, token)) throw Missing();
        var query = db.DirectMessages.AsNoTracking().Where(m => m.ChatId == chatId);
        if (before is {} b) query = query.Where(m => m.Id < b);
        if (after is {} a) query = query.Where(m => m.Id > a);
        var rows = await (after.HasValue ? query.OrderBy(m => m.Id) : query.OrderByDescending(m => m.Id))
            .Take(51).Select(m => new ChatMessageDto(m.Id, m.SenderId, m.Text, m.CreatedAtUtc)).ToListAsync(token);
        var more = rows.Count > 50; var page = rows.Take(50).OrderBy(m => m.Id).ToList();
        return new(page, more);
    }
    public Task<ChatMessageDto> Send(Guid user, Guid chatId, SendChatMessageRequest request, CancellationToken token)
    {
        if (request.ClientMessageId == Guid.Empty || string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 2000)
            throw new ApiException(400, "invalid_message", "Повідомлення: від 1 до 2000 символів.");
        return db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
            var chat = await Lock(user, chatId, token);
            var previous = await db.DirectMessages.SingleOrDefaultAsync(m => m.ChatId == chatId && m.ClientMessageId == request.ClientMessageId, token);
            if (previous is not null)
            {
                if (previous.SenderId != user || previous.Text != request.Text.Trim()) throw new ApiException(409, "message_id_conflict", "Це повідомлення вже надіслано з іншим вмістом.");
                return Dto(previous);
            }
            var peer = Peer(chat, user);
            if (peer is null || !await db.Users.AnyAsync(u => u.Id == peer && u.DeletionDueAtUtc == null, token))
                throw new ApiException(409, "recipient_unavailable", "Зараз цьому користувачу не можна написати.");
            var message = new DirectMessage { ChatId = chatId, SenderId = user, ClientMessageId = request.ClientMessageId, Text = request.Text.Trim(), CreatedAtUtc = clock.GetUtcNow().UtcDateTime };
            db.DirectMessages.Add(message); await db.SaveChangesAsync(token);
            notifications.Messaged(peer.Value,user,message);await db.SaveChangesAsync(token);await tx.CommitAsync(token); return Dto(message);
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
    private static ChatMessageDto Dto(DirectMessage message) => new(message.Id, message.SenderId, message.Text, message.CreatedAtUtc);
}
