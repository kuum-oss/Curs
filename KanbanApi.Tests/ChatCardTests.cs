using System.Net;
using System.Net.Http.Json;
using KanbanApi.Domain;
using KanbanContracts;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace KanbanApi.Tests;
[Collection("sql")]
public sealed class ChatCardTests(SqlFixture fixture)
{
    internal static async Task<ChatDto> Start(HttpClient c,Guid peer)=>(await(await c.PostAsJsonAsync("/api/chats",new StartChatRequest{RecipientId=peer})).Content.ReadFromJsonAsync<ChatDto>())!;
    internal static async Task<ChatMessageDto> Share(HttpClient c,Guid chat,Guid card,Guid? nonce=null)
    {var r=await c.PostAsJsonAsync($"/api/chats/{chat}/messages",new SendChatMessageRequest{SharedCardId=card,ClientMessageId=nonce??Guid.NewGuid()});r.EnsureSuccessStatusCode();return(await r.Content.ReadFromJsonAsync<ChatMessageDto>())!;}
    [SqlFact]
    public async Task ShareIsConcurrentIdempotentAndMetadataReflectsCurrentCard()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);var g=await new NotificationTests(fixture).Pair();using var a=ChecklistTests.Client(app,g.Owner);using var b=ChecklistTests.Client(app,g.Peer);var card=await ChecklistTests.Create(a,g.Board.Id,g.Column.Id);var chat=await Start(a,g.Peer.Id);var nonce=Guid.NewGuid();var sent=await Task.WhenAll(Share(a,chat.Id,card.Card.Id,nonce),Share(a,chat.Id,card.Card.Id,nonce));Assert.Equal(sent[0].Id,sent[1].Id);
        Assert.Equal(card.Card.Id,sent[0].SharedCard?.CardId);Assert.Equal(g.Board.Id,sent[0].SharedCard?.BoardId);
        var history=(await b.GetFromJsonAsync<ChatMessagesDto>($"/api/chats/{chat.Id}/messages"))!;Assert.Single(history.Items);Assert.True(history.Items[0].SharedCard!.CanOpen);Assert.Single((await NotificationTests.List(b)).Items);
        Assert.Equal(HttpStatusCode.Conflict,(await a.PostAsJsonAsync($"/api/chats/{chat.Id}/messages",new SendChatMessageRequest{SharedCardId=card.Card.Id,ClientMessageId=nonce,Text="Зміна"})).StatusCode);
        var body=await ChecklistTests.Body(a,g.Board.Id,card,null);body.Title="Нова назва";(await a.PutAsJsonAsync($"/api/cards/{card.Card.Id}",body)).EnsureSuccessStatusCode();var link=(await b.GetFromJsonAsync<SharedCardDto>($"/api/chats/{chat.Id}/messages/{sent[0].Id}/card"))!;Assert.Equal("Нова назва",link.Title);Assert.Equal("Нова назва",(await b.GetFromJsonAsync<ChatMessagesDto>($"/api/chats/{chat.Id}/messages"))!.Items[0].SharedCard!.Title);
    }
    [SqlFact]
    public async Task SharingNeverGrantsAccessAndEveryOpenRechecksMembership()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);var g=await new NotificationTests(fixture).Pair();using var a=ChecklistTests.Client(app,g.Owner);using var b=ChecklistTests.Client(app,g.Peer);var privateGraph=await new ChecklistTests(fixture).Seed();using var stranger=ChecklistTests.Client(app,privateGraph.User);var secret=await ChecklistTests.Create(stranger,privateGraph.Board.Id,privateGraph.Col.Id);var rename=await ChecklistTests.Body(stranger,privateGraph.Board.Id,secret,null);rename.Title="Секретний план";secret=(await(await stranger.PutAsJsonAsync($"/api/cards/{secret.Card.Id}",rename)).Content.ReadFromJsonAsync<CardDetailsDto>())!;var chat=await Start(a,g.Peer.Id);
        Assert.Equal(HttpStatusCode.NotFound,(await a.PostAsJsonAsync($"/api/chats/{chat.Id}/messages",new SendChatMessageRequest{SharedCardId=secret.Card.Id,ClientMessageId=Guid.NewGuid()})).StatusCode);
        // Give the sender, but not the recipient, access to a second board.
        await using(var db=fixture.CreateContext()){db.BoardMembers.Add(new(){BoardId=privateGraph.Board.Id,UserId=g.Owner.Id});await db.SaveChangesAsync();}
        var message=await Share(a,chat.Id,secret.Card.Id);var path=$"/api/chats/{chat.Id}/messages/{message.Id}/card";
        var hidden=(await b.GetFromJsonAsync<ChatMessagesDto>($"/api/chats/{chat.Id}/messages"))!.Items.Single().SharedCard!;Assert.False(hidden.CanOpen);Assert.Null(hidden.CardId);Assert.Null(hidden.BoardId);Assert.DoesNotContain(secret.Card.Title,hidden.Title);Assert.Equal(HttpStatusCode.NotFound,(await b.GetAsync(path)).StatusCode);
        await using(var db=fixture.CreateContext()){Assert.False(await db.BoardMembers.AnyAsync(m=>m.BoardId==privateGraph.Board.Id&&m.UserId==g.Peer.Id));db.BoardMembers.Add(new(){BoardId=privateGraph.Board.Id,UserId=g.Peer.Id});await db.SaveChangesAsync();}
        Assert.Equal(secret.Card.Id,(await b.GetFromJsonAsync<SharedCardDto>(path))!.CardId);
        await using(var db=fixture.CreateContext()){await db.BoardMembers.Where(m=>m.BoardId==privateGraph.Board.Id&&m.UserId==g.Peer.Id).ExecuteDeleteAsync();}
        Assert.Equal(HttpStatusCode.NotFound,(await b.GetAsync(path)).StatusCode);Assert.Equal(HttpStatusCode.NotFound,(await stranger.GetAsync(path)).StatusCode);Assert.Equal(secret.Card.Id,(await a.GetFromJsonAsync<SharedCardDto>(path))!.CardId);
    }
    [SqlFact]
    public async Task ArchivedOrDeletedCardLeavesSafeHistoryAndNoBrokenForeignKeys()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);var g=await new NotificationTests(fixture).Pair();using var a=ChecklistTests.Client(app,g.Owner);using var b=ChecklistTests.Client(app,g.Peer);var card=await ChecklistTests.Create(a,g.Board.Id,g.Column.Id);var chat=await Start(a,g.Peer.Id);var message=await Share(a,chat.Id,card.Card.Id);var path=$"/api/chats/{chat.Id}/messages/{message.Id}/card";
        var archive=await a.PutAsJsonAsync($"/api/cards/{card.Card.Id}/archive",new ArchiveCardRequest{Archived=true,ExpectedVersion=card.Card.Version,ExpectedBoardVersion=await ChecklistTests.V(a,g.Board.Id)});archive.EnsureSuccessStatusCode();Assert.Equal(HttpStatusCode.NotFound,(await b.GetAsync(path)).StatusCode);
        card=(await a.GetFromJsonAsync<CardDetailsDto>($"/api/cards/{card.Card.Id}"))!;
        var delete=await a.DeleteAsync($"/api/cards/{card.Card.Id}?expectedBoardVersion={Uri.EscapeDataString(await ChecklistTests.V(a,g.Board.Id))}&expectedVersion={Uri.EscapeDataString(card.Card.Version)}");delete.EnsureSuccessStatusCode();var history=(await b.GetFromJsonAsync<ChatMessagesDto>($"/api/chats/{chat.Id}/messages"))!;Assert.Single(history.Items);Assert.NotNull(history.Items[0].SharedCard);Assert.False(history.Items[0].SharedCard!.CanOpen);Assert.Null(history.Items[0].SharedCard!.CardId);
    }
    [SqlFact]
    public async Task OldSharedCardHistoryPagesAndWrongChatCannotResolveMessage()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);var g=await new NotificationTests(fixture).Pair();using var a=ChecklistTests.Client(app,g.Owner);using var b=ChecklistTests.Client(app,g.Peer);var card=await ChecklistTests.Create(a,g.Board.Id,g.Column.Id);var chat=await Start(a,g.Peer.Id);var share=await Share(a,chat.Id,card.Card.Id);
        await using(var db=fixture.CreateContext()){db.DirectMessages.AddRange(Enumerable.Range(0,54).Select(i=>new DirectMessage{ChatId=chat.Id,SenderId=g.Owner.Id,ClientMessageId=Guid.NewGuid(),Text="Повідомлення "+i}));await db.SaveChangesAsync();}
        var recent=(await b.GetFromJsonAsync<ChatMessagesDto>($"/api/chats/{chat.Id}/messages"))!;Assert.True(recent.HasMore);var older=(await b.GetFromJsonAsync<ChatMessagesDto>($"/api/chats/{chat.Id}/messages?beforeId={recent.Items[0].Id}"))!;Assert.Equal(5,older.Items.Count);Assert.Equal(card.Card.Id,older.Items[0].SharedCard!.CardId);
        Assert.Equal(HttpStatusCode.NotFound,(await b.GetAsync($"/api/chats/{Guid.NewGuid()}/messages/{share.Id}/card")).StatusCode);Assert.Equal(HttpStatusCode.NotFound,(await b.GetAsync($"/api/chats/{chat.Id}/messages/{recent.Items[0].Id}/card")).StatusCode);
    }
    [SqlFact]
    public async Task InvalidPayloadsNeverCreateMessagesOrNotifications()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);var g=await new NotificationTests(fixture).Pair();using var a=ChecklistTests.Client(app,g.Owner);using var b=ChecklistTests.Client(app,g.Peer);var card=await ChecklistTests.Create(a,g.Board.Id,g.Column.Id);var chat=await Start(a,g.Peer.Id);
        foreach(var request in new[]{new SendChatMessageRequest{SharedCardId=Guid.Empty,ClientMessageId=Guid.NewGuid()},new SendChatMessageRequest{SharedCardId=card.Card.Id,ClientMessageId=Guid.Empty},new SendChatMessageRequest{Text=new string('x',2001),SharedCardId=card.Card.Id,ClientMessageId=Guid.NewGuid()},new SendChatMessageRequest{Text="  ",ClientMessageId=Guid.NewGuid()}})
            Assert.Equal(HttpStatusCode.BadRequest,(await a.PostAsJsonAsync($"/api/chats/{chat.Id}/messages",request)).StatusCode);
        Assert.Empty((await b.GetFromJsonAsync<ChatMessagesDto>($"/api/chats/{chat.Id}/messages"))!.Items);Assert.Empty((await NotificationTests.List(b)).Items);
    }
}
