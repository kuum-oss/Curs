using System.Net;
using System.Net.Http.Json;
using KanbanApi.Domain;
using KanbanApi.Infrastructure;
using KanbanContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace KanbanApi.Tests;
[Collection("sql")]
public sealed class NotificationTests(SqlFixture fixture)
{
    internal async Task<(User Owner,User Peer,Board Board,BoardColumn Column)> Pair()
    {
        var helper=new ChecklistTests(fixture);var a=await helper.Seed();var b=await helper.Seed();
        await using var db=fixture.CreateContext();db.BoardMembers.Add(new(){BoardId=a.Board.Id,UserId=b.User.Id});await db.SaveChangesAsync();return(a.User,b.User,a.Board,a.Col);
    }
    internal static async Task<NotificationsDto> List(HttpClient c,string query="")=>(await c.GetFromJsonAsync<NotificationsDto>("/api/notifications"+query))!;
    internal static async Task<CardDetailsDto> Assigned(HttpClient c,Guid board,Guid column,Guid peer,DateOnly? due=null)
    {
        var r=await c.PostAsJsonAsync($"/api/boards/{board}/cards",new CreateCardRequest{Title="Приватна картка",ColumnId=column,AssigneeId=peer,DueDate=due,ExpectedBoardVersion=await ChecklistTests.V(c,board)});r.EnsureSuccessStatusCode();return(await r.Content.ReadFromJsonAsync<CardDetailsDto>())!;
    }
    [SqlFact]
    public async Task AssignmentOnlyForChangedRecipientAndFailedVersionNeverNotifies()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);var g=await Pair();using var a=ChecklistTests.Client(app,g.Owner);using var b=ChecklistTests.Client(app,g.Peer);
        var card=await Assigned(a,g.Board.Id,g.Column.Id,g.Peer.Id);var first=await List(b);Assert.Single(first.Items);Assert.Equal(NotificationKind.Assignment,first.Items[0].Kind);Assert.Equal(card.Card.Id,first.Items[0].CardId);
        var body=await ChecklistTests.Body(a,g.Board.Id,card,null);body.AssigneeId=g.Peer.Id;var update=await a.PutAsJsonAsync($"/api/cards/{card.Card.Id}",body);update.EnsureSuccessStatusCode();Assert.Single((await List(b)).Items);
        body.AssigneeId=g.Owner.Id;Assert.Equal(HttpStatusCode.Conflict,(await a.PutAsJsonAsync($"/api/cards/{card.Card.Id}",body)).StatusCode);Assert.Empty((await List(a)).Items);Assert.Single((await List(b)).Items);
        Assert.Equal(HttpStatusCode.Unauthorized,(await app.CreateClient().GetAsync("/api/notifications")).StatusCode);
    }
    [SqlFact]
    public async Task MentionsMatchMemberEmailExactlyAndEditsDoNotRepeatNotification()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);var g=await Pair();var outside=await new ChecklistTests(fixture).Seed();using var a=ChecklistTests.Client(app,g.Owner);using var b=ChecklistTests.Client(app,g.Peer);using var stranger=ChecklistTests.Client(app,outside.User);
        var card=await ChecklistTests.Create(a,g.Board.Id,g.Column.Id);var path=$"/api/cards/{card.Card.Id}";
        var r=await a.PostAsJsonAsync(path+"/comments",new CreateCommentRequest{Text=$"@{g.Peer.Email.ToUpperInvariant()}, перевірте. @{g.Peer.Email} @{outside.User.Email}",ExpectedVersion=card.Card.Version,ExpectedBoardVersion=await ChecklistTests.V(a,g.Board.Id)});r.EnsureSuccessStatusCode();var comment=(await r.Content.ReadFromJsonAsync<CommentDto>())!;
        Assert.Single((await List(b)).Items);Assert.Empty((await List(stranger)).Items);
        card=(await a.GetFromJsonAsync<CardDetailsDto>(path))!;
        var edit=await a.PutAsJsonAsync(path+$"/comments/{comment.Id}",new UpdateCommentRequest{Text=$"Знову @{g.Peer.Email}",ExpectedBoardVersion=await ChecklistTests.V(a,g.Board.Id),ExpectedVersion=card.Card.Version,ExpectedCommentVersion=comment.Version!});edit.EnsureSuccessStatusCode();Assert.Single((await List(b)).Items);
        card=(await a.GetFromJsonAsync<CardDetailsDto>(path))!;
        var prefix=await a.PostAsJsonAsync(path+"/comments",new CreateCommentRequest{Text=$"x@{g.Peer.Email} @{g.Peer.Email}.other",ExpectedVersion=card.Card.Version,ExpectedBoardVersion=await ChecklistTests.V(a,g.Board.Id)});prefix.EnsureSuccessStatusCode();Assert.Single((await List(b)).Items);
    }
    [SqlFact]
    public async Task MessageRetryReadAndReadAllWatermarkAreIsolatedAndIdempotent()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);var g=await Pair();using var a=ChecklistTests.Client(app,g.Owner);using var b=ChecklistTests.Client(app,g.Peer);
        var chat=(await (await a.PostAsJsonAsync("/api/chats",new StartChatRequest{RecipientId=g.Peer.Id})).Content.ReadFromJsonAsync<ChatDto>())!;
        var body=new SendChatMessageRequest{Text="Особистий текст",ClientMessageId=Guid.NewGuid()};var path=$"/api/chats/{chat.Id}";
        var message=(await(await a.PostAsJsonAsync(path+"/messages",body)).Content.ReadFromJsonAsync<ChatMessageDto>())!;await a.PostAsJsonAsync(path+"/messages",body);
        var first=await List(b);Assert.Single(first.Items);Assert.Equal(chat.Id,first.Items[0].ChatId);
        Assert.Equal(HttpStatusCode.NotFound,(await a.PostAsync($"/api/notifications/{first.Items[0].Id}/open",null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await a.PostAsync($"/api/notifications/{first.Items[0].Id}/read",null)).StatusCode);
        (await b.PostAsJsonAsync(path+"/read",new ChatReadRequest{LastReadMessageId=message.Id})).EnsureSuccessStatusCode();Assert.Equal(0,(await List(b)).UnreadCount);
        await a.PostAsJsonAsync(path+"/messages",new SendChatMessageRequest{Text="Друге",ClientMessageId=Guid.NewGuid()});
        (await b.PostAsJsonAsync("/api/notifications/read-all",new ReadNotificationsRequest{ThroughId=first.LatestId})).EnsureSuccessStatusCode();Assert.Equal(1,(await List(b)).UnreadCount);
        var next=await List(b);(await b.PostAsJsonAsync("/api/notifications/read-all",new ReadNotificationsRequest{ThroughId=next.LatestId})).EnsureSuccessStatusCode();Assert.Empty((await List(b,"?unreadOnly=true")).Items);
    }
    [SqlFact]
    public async Task DeadlineGenerationIsConcurrentSafeAndRescheduleCannotOpenOldReminder()
    {
        var clock=new AccountDeletionTestClock();using var app=clock.Factory(fixture.ConnectionString);var g=await Pair();using var a=ChecklistTests.Client(app,g.Owner);using var b=ChecklistTests.Client(app,g.Peer);var today=DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var card=await Assigned(a,g.Board.Id,g.Column.Id,g.Peer.Id,today.AddDays(1));await Task.WhenAll(List(b),List(b),List(b));var rows=await List(b);Assert.Equal(2,rows.Items.Count);var due=Assert.Single(rows.Items,n=>n.Kind==NotificationKind.Deadline);
        var body=await ChecklistTests.Body(a,g.Board.Id,card,null);body.AssigneeId=g.Peer.Id;body.DueDate=today.AddDays(5);(await a.PutAsJsonAsync($"/api/cards/{card.Card.Id}",body)).EnsureSuccessStatusCode();
        var stale=(await List(b)).Items.Single(n=>n.Id==due.Id);Assert.False(stale.CanOpen);Assert.DoesNotContain("Приватна",stale.Context);Assert.Equal(HttpStatusCode.NotFound,(await b.PostAsync($"/api/notifications/{due.Id}/open",null)).StatusCode);
    }
    [SqlFact]
    public async Task PagingAndRevocationNeverExposeResourceSnapshot()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);var g=await Pair();using var a=ChecklistTests.Client(app,g.Owner);using var b=ChecklistTests.Client(app,g.Peer);var card=await Assigned(a,g.Board.Id,g.Column.Id,g.Peer.Id);
        await using(var db=fixture.CreateContext()){db.Notifications.AddRange(Enumerable.Range(0,55).Select(_=>new Notification{UserId=g.Peer.Id,Kind=NotificationKind.Assignment,CardId=card.Card.Id,EventKey=Guid.NewGuid().ToString(),CreatedAtUtc=DateTime.UtcNow}));await db.SaveChangesAsync();}
        var first=await List(b);Assert.Equal(50,first.Items.Count);Assert.True(first.HasMore);var rest=await List(b,$"?beforeId={first.Items.Last().Id}");Assert.Equal(6,rest.Items.Count);Assert.Empty(first.Items.Select(n=>n.Id).Intersect(rest.Items.Select(n=>n.Id)));
        await using(var db=fixture.CreateContext()){await db.BoardMembers.Where(m=>m.BoardId==g.Board.Id&&m.UserId==g.Peer.Id).ExecuteDeleteAsync();}
        var hidden=await List(b);Assert.All(hidden.Items,n=>{Assert.False(n.CanOpen);Assert.Null(n.CardId);Assert.Null(n.BoardId);Assert.DoesNotContain("Приватна",n.Context);});Assert.Equal(HttpStatusCode.NotFound,(await b.PostAsync($"/api/notifications/{first.Items[0].Id}/open",null)).StatusCode);
        (await b.PostAsJsonAsync("/api/notifications/read-all",new ReadNotificationsRequest{ThroughId=hidden.LatestId})).EnsureSuccessStatusCode();Assert.Equal(0,(await List(b)).UnreadCount);
    }
}
