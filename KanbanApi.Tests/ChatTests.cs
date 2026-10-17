using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using KanbanApi.Application;
using KanbanApi.Domain;
using KanbanContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KanbanApi.Tests;

[Collection("sql")]
public sealed class ChatTests(SqlFixture fixture)
{
    private const string Password="chat-test-password123!";
    private static async Task<AuthResponse> Register(HttpClient c)
    {
        var r=await c.PostAsJsonAsync("/api/auth/register",new RegisterRequest{Email=Guid.NewGuid()+"@example.test",Name="Чат",Password=Password});r.EnsureSuccessStatusCode();var a=(await r.Content.ReadFromJsonAsync<AuthResponse>())!;c.DefaultRequestHeaders.Authorization=new("Bearer",a.AccessToken);return a;
    }
    private async Task Shared(Guid a,Guid b)
    {
        await using var db=fixture.CreateContext();var board=new Board{Name="Чат",OwnerId=a};db.AddRange(board,new BoardMember{BoardId=board.Id,UserId=a,Role=BoardRole.Admin},new BoardMember{BoardId=board.Id,UserId=b});await db.SaveChangesAsync();
    }
    private static async Task<ChatDto> Start(HttpClient c,Guid peer)
    {var r=await c.PostAsJsonAsync("/api/chats",new StartChatRequest{RecipientId=peer});r.EnsureSuccessStatusCode();return (await r.Content.ReadFromJsonAsync<ChatDto>())!;}
    private static async Task<ChatMessageDto> Send(HttpClient c,Guid chat,string text,Guid? nonce=null)
    {var r=await c.PostAsJsonAsync($"/api/chats/{chat}/messages",new SendChatMessageRequest{Text=text,ClientMessageId=nonce??Guid.NewGuid()});r.EnsureSuccessStatusCode();return (await r.Content.ReadFromJsonAsync<ChatMessageDto>())!;}

    [SqlFact]
    public async Task OnlyTheTwoParticipantsCanReadWriteOrMarkReadAndChatSurvivesBoardRemoval()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);using var a=app.CreateClient();using var b=app.CreateClient();using var other=app.CreateClient();
        var aa=await Register(a);var bb=await Register(b);await Register(other);await Shared(aa.User.Id,bb.User.Id);
        var chat=await Start(a,bb.User.Id);var reverse=await Start(b,aa.User.Id);Assert.Equal(chat.Id,reverse.Id);
        var sent=await Send(a,chat.Id,"Вітаю!\nДва рядки.");Assert.Equal(aa.User.Id,sent.SenderId);
        Assert.Equal(1,(await b.GetFromJsonAsync<List<ChatDto>>("/api/chats"))!.Single().UnreadCount);
        Assert.Empty((await other.GetFromJsonAsync<List<ChatDto>>("/api/chats"))!);
        Assert.Equal(HttpStatusCode.NotFound,(await other.GetAsync($"/api/chats/{chat.Id}/messages")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await other.PostAsJsonAsync($"/api/chats/{chat.Id}/messages",new SendChatMessageRequest{Text="Ні",ClientMessageId=Guid.NewGuid()})).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await other.PostAsJsonAsync($"/api/chats/{chat.Id}/read",new ChatReadRequest{LastReadMessageId=sent.Id})).StatusCode);
        using var anon=app.CreateClient();Assert.Equal(HttpStatusCode.Unauthorized,(await anon.GetAsync("/api/chats")).StatusCode);
        var history=await b.GetAsync($"/api/chats/{chat.Id}/messages");Assert.Contains("no-store",history.Headers.CacheControl!.ToString());Assert.Equal(sent,(await history.Content.ReadFromJsonAsync<ChatMessagesDto>())!.Items.Single());
        (await b.PostAsJsonAsync($"/api/chats/{chat.Id}/read",new ChatReadRequest{LastReadMessageId=sent.Id})).EnsureSuccessStatusCode();
        Assert.Equal(0,(await b.GetFromJsonAsync<List<ChatDto>>("/api/chats"))!.Single().UnreadCount);
        await using var db=fixture.CreateContext();await db.BoardMembers.Where(m=>m.UserId==bb.User.Id).ExecuteDeleteAsync();
        Assert.NotNull(await Send(b,chat.Id,"Особиста розмова збереглась."));
    }
    [SqlFact]
    public async Task CannotStartWithSelfOrStrangerAndInvalidBodiesOrCursorsAreRejected()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);using var a=app.CreateClient();using var b=app.CreateClient();var aa=await Register(a);var bb=await Register(b);
        Assert.Equal(HttpStatusCode.BadRequest,(await a.PostAsJsonAsync("/api/chats",new{recipientId=aa.User.Id})).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await a.PostAsJsonAsync("/api/chats",new{recipientId=bb.User.Id})).StatusCode);
        await Shared(aa.User.Id,bb.User.Id);var chat=await Start(a,bb.User.Id);
        foreach(var text in new[]{" ",new string('x',2001)})Assert.Equal(HttpStatusCode.BadRequest,(await a.PostAsJsonAsync($"/api/chats/{chat.Id}/messages",new{clientMessageId=Guid.NewGuid(),text})).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await a.PostAsJsonAsync($"/api/chats/{chat.Id}/messages",new{text="Немає nonce"})).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await a.GetAsync($"/api/chats/{chat.Id}/messages?beforeId=5&afterId=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await a.GetAsync($"/api/chats/{chat.Id}/messages?afterId=-1")).StatusCode);
    }
    [SqlFact]
    public async Task ConcurrentStartAndSendAreIdempotentAndReadsCannotGoBackwards()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);using var a=app.CreateClient();using var b=app.CreateClient();var aa=await Register(a);var bb=await Register(b);await Shared(aa.User.Id,bb.User.Id);
        var chats=await Task.WhenAll(Start(a,bb.User.Id),Start(b,aa.User.Id));Assert.Equal(chats[0].Id,chats[1].Id);var id=chats[0].Id;var nonce=Guid.NewGuid();
        var messages=await Task.WhenAll(Send(a,id,"Один раз",nonce),Send(a,id,"Один раз",nonce));Assert.Equal(messages[0].Id,messages[1].Id);
        var next=await Send(a,id,"Наступне");await b.PostAsJsonAsync($"/api/chats/{id}/read",new ChatReadRequest{LastReadMessageId=next.Id});await b.PostAsJsonAsync($"/api/chats/{id}/read",new ChatReadRequest{LastReadMessageId=messages[0].Id});
        Assert.Equal(0,(await b.GetFromJsonAsync<List<ChatDto>>("/api/chats"))!.Single().UnreadCount);
        Assert.Equal(2,(await b.GetFromJsonAsync<ChatMessagesDto>($"/api/chats/{id}/messages"))!.Items.Count);
        Assert.Equal(HttpStatusCode.Conflict,(await a.PostAsJsonAsync($"/api/chats/{id}/messages",new SendChatMessageRequest{ClientMessageId=nonce,Text="Інший текст"})).StatusCode);
    }
    [SqlFact]
    public async Task HistoryUsesStableBoundedPagesAndReadRejectsMessagesFromOtherChats()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);using var a=app.CreateClient();using var b=app.CreateClient();var aa=await Register(a);var bb=await Register(b);await Shared(aa.User.Id,bb.User.Id);var chat=await Start(a,bb.User.Id);
        await using(var db=fixture.CreateContext()){for(var i=0;i<120;i++)db.Add(new DirectMessage{ChatId=chat.Id,SenderId=aa.User.Id,ClientMessageId=Guid.NewGuid(),Text=$"Рядок {i}"});await db.SaveChangesAsync();}
        var last=(await a.GetFromJsonAsync<ChatMessagesDto>($"/api/chats/{chat.Id}/messages"))!;Assert.Equal(50,last.Items.Count);Assert.True(last.HasMore);Assert.True(last.Items.SequenceEqual(last.Items.OrderBy(m=>m.Id)));
        var older=(await a.GetFromJsonAsync<ChatMessagesDto>($"/api/chats/{chat.Id}/messages?beforeId={last.Items[0].Id}"))!;Assert.Equal(50,older.Items.Count);Assert.True(older.HasMore);Assert.Empty(last.Items.Select(m=>m.Id).Intersect(older.Items.Select(m=>m.Id)));
        var first=(await a.GetFromJsonAsync<ChatMessagesDto>($"/api/chats/{chat.Id}/messages?beforeId={older.Items[0].Id}"))!;Assert.Equal(20,first.Items.Count);Assert.False(first.HasMore);
        var after=(await b.GetFromJsonAsync<ChatMessagesDto>($"/api/chats/{chat.Id}/messages?afterId=0"))!;Assert.Equal(50,after.Items.Count);Assert.True(after.HasMore);
        Assert.Equal(HttpStatusCode.NotFound,(await a.PostAsJsonAsync($"/api/chats/{chat.Id}/read",new{lastReadMessageId=long.MaxValue})).StatusCode);
    }
    [SqlFact]
    public async Task AccountRecoveryKeepsChatsAndFinalPurgeAnonymizesHistoryThenRemovesOrphan()
    {
        var clock=new AccountDeletionTestClock();using var app=clock.Factory(fixture.ConnectionString);using var a=app.CreateClient();using var b=app.CreateClient();var aa=await Register(a);var bb=await Register(b);await Shared(aa.User.Id,bb.User.Id);var chat=await Start(a,bb.User.Id);await Send(a,chat.Id,"Історія");
        async Task Schedule(HttpClient client){var p=(await client.GetFromJsonAsync<AccountDeletionPreviewDto>("/api/profile/deletion-preview"))!;(await client.SendAsync(new(HttpMethod.Delete,"api/profile"){Content=JsonContent.Create(new DeleteAccountRequest{ConfirmationEmail=p.Email,CurrentPassword=Password,ExpectedVersion=p.ExpectedVersion,ConfirmationToken=p.ConfirmationToken})})).EnsureSuccessStatusCode();}
        await Schedule(a);Assert.False((await b.GetFromJsonAsync<List<ChatDto>>("/api/chats"))!.Single().CanSend);
        Assert.Equal(HttpStatusCode.Conflict,(await b.PostAsJsonAsync($"/api/chats/{chat.Id}/messages",new SendChatMessageRequest{Text="Тест",ClientMessageId=Guid.NewGuid()})).StatusCode);
        clock.Advance(TimeSpan.FromDays(7));using(var scope=app.Services.CreateScope())await scope.ServiceProvider.GetRequiredService<AccountDeletionService>().PurgeExpired(default);
        var row=(await b.GetFromJsonAsync<List<ChatDto>>("/api/chats"))!.Single();Assert.Equal(Guid.Empty,row.Peer.Id);Assert.False(row.CanSend);Assert.Null((await b.GetFromJsonAsync<ChatMessagesDto>($"/api/chats/{chat.Id}/messages"))!.Items.Single().SenderId);
        await Schedule(b);clock.Advance(TimeSpan.FromDays(7));using(var scope=app.Services.CreateScope())await scope.ServiceProvider.GetRequiredService<AccountDeletionService>().PurgeExpired(default);
        await using var db2=fixture.CreateContext();Assert.False(await db2.DirectChats.AnyAsync(c=>c.Id==chat.Id));Assert.False(await db2.DirectMessages.AnyAsync(m=>m.ChatId==chat.Id));
    }
}
