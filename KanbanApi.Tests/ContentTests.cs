using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using KanbanApi.Domain;
using KanbanApi.Infrastructure;
using KanbanContracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KanbanApi.Tests;

[Collection("sql")]
public sealed class ContentTests(SqlFixture fixture)
{
    private async Task<User> User()
    {
        var email = Guid.NewGuid().ToString("N") + "@example.test";
        var user = new User { Email = email, NormalizedEmail = email.ToUpperInvariant(), Name = "Учасник", PasswordHash = "unused" };
        await using var db = fixture.CreateContext(); db.Users.Add(user); await db.SaveChangesAsync(); return user;
    }
    private static HttpClient Client(WebApplicationFactory<Program> app, User user)
    {
        var client = app.CreateClient(); var jwt = app.Services.GetRequiredService<JwtService>().Issue(new(user.Id,user.Email,user.Name));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",jwt.AccessToken); return client;
    }
    private static async Task<T> Created<T>(HttpResponseMessage response)
    { Assert.Equal(HttpStatusCode.Created,response.StatusCode); return (await response.Content.ReadFromJsonAsync<T>())!; }
    // Separate helper keeps every mutation's expected version fresh.
    private static async Task<BoardDetailsDto> Create(HttpClient c) => await Created<BoardDetailsDto>(await c.PostAsJsonAsync("/api/boards",new CreateBoardRequest { Name="Дошка" }));
    private static async Task<string> V(HttpClient c,Guid b) => (await c.GetFromJsonAsync<BoardDetailsDto>($"/api/boards/{b}"))!.Version;
    private static string Q(string board,string resource) => "?expectedBoardVersion="+Uri.EscapeDataString(board)+"&expectedVersion="+Uri.EscapeDataString(resource);
    private static async Task<ColumnDto> Col(HttpClient c,Guid b,string name="Колонка") => await Created<ColumnDto>(await c.PostAsJsonAsync($"/api/boards/{b}/columns",new CreateColumnRequest { Name=name,ExpectedBoardVersion=await V(c,b) }));
    private static async Task<CardDetailsDto> Card(HttpClient c,Guid b,Guid col,string title="Картка") => await Created<CardDetailsDto>(await c.PostAsJsonAsync($"/api/boards/{b}/cards",new CreateCardRequest { ColumnId=col,Title=title,ExpectedBoardVersion=await V(c,b) }));
    private static async Task<TagDto> Tag(HttpClient c,Guid b,string name="Тег") => await Created<TagDto>(await c.PostAsJsonAsync($"/api/boards/{b}/tags",new CreateTagRequest { Name=name,ExpectedBoardVersion=await V(c,b) }));

    [SqlFact]
    public async Task CardFieldsTagsAssigneeAndCommentsPersistAndPagesCarryVersion()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);var user=await User();using var c=Client(app,user);var b=await Create(c);var col=await Col(c,b.Id);var tag=await Tag(c,b.Id);
        var card=await Created<CardDetailsDto>(await c.PostAsJsonAsync($"/api/boards/{b.Id}/cards",new CreateCardRequest { ColumnId=col.Id,Title="  Українська назва  ",Description="  Опис  ",DueDate=new(2027,1,2),Priority=CardPriority.Critical,AssigneeId=user.Id,TagIds=[tag.Id],ExpectedBoardVersion=await V(c,b.Id) }));
        Assert.Equal("Українська назва",card.Card.Title);Assert.Equal("Опис",card.Description);Assert.Equal(user.Id,card.Card.Assignee!.Id);Assert.Single(card.Card.Tags);
        var update=await c.PutAsJsonAsync($"/api/cards/{card.Card.Id}",new UpdateCardRequest { Title="Нова",Description="Збережений опис",Priority=CardPriority.Low,DueDate=new(2027,2,3),TagIds=[],ExpectedBoardVersion=await V(c,b.Id),ExpectedVersion=card.Card.Version });Assert.Equal(HttpStatusCode.OK,update.StatusCode);
        card=(await update.Content.ReadFromJsonAsync<CardDetailsDto>())!;Assert.Null(card.Card.Assignee);Assert.Empty(card.Card.Tags);Assert.Equal(CardPriority.Low,card.Card.Priority);
        var comment=await Created<CommentDto>(await c.PostAsJsonAsync($"/api/cards/{card.Card.Id}/comments",new CreateCommentRequest { Text="  Коментар  ",ExpectedBoardVersion=await V(c,b.Id),ExpectedVersion=card.Card.Version }));Assert.Equal("Коментар",comment.Text);Assert.Equal(user.Id,comment.Author.Id);Assert.Equal(DateTimeKind.Utc,comment.CreatedAtUtc.Kind);
        var reopened=(await c.GetFromJsonAsync<CardDetailsDto>($"/api/cards/{card.Card.Id}"))!;Assert.NotEqual(card.Card.Version,reopened.Card.Version);Assert.Equal(new DateOnly(2027,2,3),reopened.Card.DueDate);
        var comments=(await c.GetFromJsonAsync<PagedResult<CommentDto>>($"/api/cards/{card.Card.Id}/comments?limit=1"))!;Assert.Equal(1,comments.TotalCount);Assert.Equal(comment.Id,comments.Items[0].Id);
        await Card(c,b.Id,col.Id,"Друга");var page=(await c.GetFromJsonAsync<PagedResult<CardSummaryDto>>($"/api/boards/{b.Id}/cards?offset=1&limit=1"))!;Assert.Equal(2,page.TotalCount);Assert.Single(page.Items);Assert.Equal("Друга",page.Items[0].Title);Assert.Equal(await V(c,b.Id),page.BoardVersion);
    }
    [SqlFact]
    public async Task ColumnRenameAndDeletesKeepPositionsAndRemoveOnlyItsSubtree()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);using var c=Client(app,await User());var b=await Create(c);var first=await Col(c,b.Id,"Перша");var second=await Col(c,b.Id,"Друга");var tag=await Tag(c,b.Id);var card=await Card(c,b.Id,first.Id);var keep=await Card(c,b.Id,second.Id);
        Assert.Equal(HttpStatusCode.OK,(await c.PutAsJsonAsync($"/api/boards/{b.Id}/columns/{first.Id}",new UpdateColumnRequest { Name="  Оновлена  ",ExpectedBoardVersion=await V(c,b.Id),ExpectedVersion=first.Version })).StatusCode);
        first=(await c.GetFromJsonAsync<List<ColumnDto>>($"/api/boards/{b.Id}/columns"))!.Single(x=>x.Id==first.Id);Assert.Equal("Оновлена",first.Name);
        await c.PostAsJsonAsync($"/api/cards/{card.Card.Id}/comments",new CreateCommentRequest { Text="Історія",ExpectedBoardVersion=await V(c,b.Id),ExpectedVersion=card.Card.Version });
        await using(var db=fixture.CreateContext()) { db.CardTags.Add(new CardTag { CardId=card.Card.Id,TagId=tag.Id });db.CardImages.Add(new CardImage { CardId=card.Card.Id,ContentType="image/png",OriginalName="test.png",SizeBytes=1,OriginalData=[1],ThumbnailData=[1],ThumbnailContentType="image/png" });await db.SaveChangesAsync(); }
        Assert.Equal(HttpStatusCode.NoContent,(await c.DeleteAsync($"/api/boards/{b.Id}/columns/{first.Id}"+Q(await V(c,b.Id),first.Version))).StatusCode);
        var columns=(await c.GetFromJsonAsync<List<ColumnDto>>($"/api/boards/{b.Id}/columns"))!;Assert.Single(columns);Assert.Equal(0,columns[0].Position);
        await using var verify=fixture.CreateContext();Assert.False(await verify.Cards.AnyAsync(x=>x.Id==card.Card.Id));Assert.False(await verify.Comments.AnyAsync(x=>x.CardId==card.Card.Id));Assert.False(await verify.CardImages.AnyAsync(x=>x.CardId==card.Card.Id));Assert.False(await verify.CardTags.AnyAsync(x=>x.CardId==card.Card.Id));Assert.True(await verify.Tags.AnyAsync(x=>x.Id==tag.Id));Assert.True(await verify.Cards.AnyAsync(x=>x.Id==keep.Card.Id));
    }
    [SqlFact]
    public async Task RestrictedMemberCannotCreateOrDeleteCardsOrNonemptyColumnButCanEditCommentAndDeleteEmpty()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);var member=await User();using var admin=Client(app,await User());using var c=Client(app,member);var b=await Create(admin);var col=await Col(admin,b.Id);var empty=await Col(admin,b.Id);var card=await Card(admin,b.Id,col.Id);
        await admin.PostAsJsonAsync($"/api/boards/{b.Id}/members",new AddMemberRequest { Email=member.Email,ExpectedBoardVersion=await V(admin,b.Id) });
        await admin.PatchAsJsonAsync($"/api/boards/{b.Id}/members/{member.Id}",new UpdateMemberPermissionsRequest { CanCreateCards=false,CanDeleteCards=false,ExpectedBoardVersion=await V(admin,b.Id) });
        var version=await V(admin,b.Id);
        Assert.Equal(HttpStatusCode.Forbidden,(await c.PostAsJsonAsync($"/api/boards/{b.Id}/cards",new CreateCardRequest { ColumnId=col.Id,Title="Заборонено",ExpectedBoardVersion=version })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await c.DeleteAsync($"/api/cards/{card.Card.Id}"+Q(version,card.Card.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await c.DeleteAsync($"/api/boards/{b.Id}/columns/{col.Id}"+Q(version,col.Version))).StatusCode);Assert.Equal(version,await V(admin,b.Id));
        Assert.Equal(HttpStatusCode.OK,(await c.PutAsJsonAsync($"/api/cards/{card.Card.Id}",new UpdateCardRequest { Title="Редагування дозволено",ExpectedBoardVersion=version,ExpectedVersion=card.Card.Version })).StatusCode);
        card=(await c.GetFromJsonAsync<CardDetailsDto>($"/api/cards/{card.Card.Id}"))!;await Created<CommentDto>(await c.PostAsJsonAsync($"/api/cards/{card.Card.Id}/comments",new CreateCommentRequest { Text="Дозволено",ExpectedBoardVersion=await V(c,b.Id),ExpectedVersion=card.Card.Version }));
        Assert.Equal(HttpStatusCode.NoContent,(await c.DeleteAsync($"/api/boards/{b.Id}/columns/{empty.Id}"+Q(await V(c,b.Id),empty.Version))).StatusCode);
    }
    [SqlFact]
    public async Task ForeignColumnTagsAssigneeAndPrivateCardIdentifiersCannotBypassBoardBoundary()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);using var c=Client(app,await User());var stranger=await User();using var outsider=Client(app,stranger);var a=await Create(c);var b=await Create(c);var col=await Col(c,a.Id);var foreign=await Col(c,b.Id);var tag=await Tag(c,b.Id);var card=await Card(c,a.Id,col.Id);var version=await V(c,a.Id);
        Assert.Equal(HttpStatusCode.NotFound,(await c.PostAsJsonAsync($"/api/boards/{a.Id}/cards",new CreateCardRequest { ColumnId=foreign.Id,Title="Чужа колонка",ExpectedBoardVersion=version })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await c.PutAsJsonAsync($"/api/cards/{card.Card.Id}",new UpdateCardRequest { Title="Тег",TagIds=[tag.Id],ExpectedBoardVersion=version,ExpectedVersion=card.Card.Version })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await c.PutAsJsonAsync($"/api/cards/{card.Card.Id}",new UpdateCardRequest { Title="Відповідальний",AssigneeId=stranger.Id,ExpectedBoardVersion=version,ExpectedVersion=card.Card.Version })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await c.DeleteAsync($"/api/boards/{a.Id}/columns/{foreign.Id}"+Q(version,foreign.Version))).StatusCode);Assert.Equal(version,await V(c,a.Id));
        foreach(var path in new[] {$"/api/cards/{card.Card.Id}",$"/api/cards/{card.Card.Id}/comments",$"/api/boards/{a.Id}/columns",$"/api/boards/{a.Id}/cards",$"/api/boards/{a.Id}/tags"}) Assert.Equal(HttpStatusCode.NotFound,(await outsider.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await outsider.DeleteAsync($"/api/cards/{card.Card.Id}"+Q(version,card.Card.Version))).StatusCode);
    }
    [SqlFact]
    public async Task ResourceVersionConflictsRollbackBoardAndDeletingMiddleCardClosesPositionGap()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);using var c=Client(app,await User());var b=await Create(c);var col=await Col(c,b.Id);var first=await Card(c,b.Id,col.Id,"1");var middle=await Card(c,b.Id,col.Id,"2");var last=await Card(c,b.Id,col.Id,"3");var version=await V(c,b.Id);
        Assert.Equal(HttpStatusCode.Conflict,(await c.PutAsJsonAsync($"/api/cards/{first.Card.Id}",new UpdateCardRequest { Title="Конфлікт",ExpectedBoardVersion=version,ExpectedVersion=Convert.ToBase64String(new byte[8]) })).StatusCode);Assert.Equal(version,await V(c,b.Id));
        Assert.Equal(HttpStatusCode.NoContent,(await c.DeleteAsync($"/api/cards/{middle.Card.Id}"+Q(version,middle.Card.Version))).StatusCode);
        var cards=(await c.GetFromJsonAsync<PagedResult<CardSummaryDto>>($"/api/boards/{b.Id}/cards"))!;Assert.Equal(new[]{0,1},cards.Items.Select(x=>x.Position));Assert.Equal(new[]{first.Card.Id,last.Card.Id},cards.Items.Select(x=>x.Id));
    }
    [SqlFact]
    public async Task ValidationAndDuplicateTagsDoNotChangeBoard()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);using var c=Client(app,await User());var b=await Create(c);var col=await Col(c,b.Id);var tag=await Tag(c,b.Id,"  ТЕГ  ");var version=await V(c,b.Id);
        foreach(var body in new object[]{new {columnId=col.Id,title=" ",expectedBoardVersion=version},new{columnId=col.Id,title="Невалідний",priority=99,expectedBoardVersion=version},new{columnId=col.Id,title="Версія відсутня"},new{columnId=col.Id,title="Дата",dueDate="not-date",expectedBoardVersion=version},new{columnId=col.Id,title="Дублі",tagIds=new[]{tag.Id,tag.Id},expectedBoardVersion=version}}) Assert.Equal(HttpStatusCode.BadRequest,(await c.PostAsJsonAsync($"/api/boards/{b.Id}/cards",body)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await c.PostAsJsonAsync($"/api/boards/{b.Id}/tags",new CreateTagRequest { Name="тег",ExpectedBoardVersion=version })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await c.PostAsJsonAsync($"/api/boards/{b.Id}/tags",new CreateTagRequest { Name="Колір",Color="red",ExpectedBoardVersion=version })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await c.GetAsync($"/api/boards/{b.Id}/cards?limit=501")).StatusCode);Assert.Equal(HttpStatusCode.BadRequest,(await c.DeleteAsync($"/api/boards/{b.Id}/columns/{col.Id}")).StatusCode);Assert.Equal(version,await V(c,b.Id));
    }
    [SqlFact]
    public async Task RevokedMemberCannotUseExistingCardAndCommentRoutesWithOldJwt()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);var person=await User();using var a=Client(app,await User());using var m=Client(app,person);var b=await Create(a);var col=await Col(a,b.Id);var card=await Card(a,b.Id,col.Id);
        await a.PostAsJsonAsync($"/api/boards/{b.Id}/members",new AddMemberRequest { Email=person.Email,ExpectedBoardVersion=await V(a,b.Id) });
        var comment=await Created<CommentDto>(await m.PostAsJsonAsync($"/api/cards/{card.Card.Id}/comments",new CreateCommentRequest { Text="Зберегти історію",ExpectedBoardVersion=await V(a,b.Id),ExpectedVersion=card.Card.Version }));
        card=(await a.GetFromJsonAsync<CardDetailsDto>($"/api/cards/{card.Card.Id}"))!;var v=await V(a,b.Id);await a.DeleteAsync($"/api/boards/{b.Id}/members/{person.Id}?expectedBoardVersion="+Uri.EscapeDataString(v));
        Assert.Equal(HttpStatusCode.NotFound,(await m.GetAsync($"/api/cards/{card.Card.Id}")).StatusCode);Assert.Equal(HttpStatusCode.NotFound,(await m.PostAsJsonAsync($"/api/cards/{card.Card.Id}/comments",new CreateCommentRequest { Text="Немає доступу",ExpectedBoardVersion=v,ExpectedVersion=card.Card.Version })).StatusCode);
        Assert.Equal(comment.Id,(await a.GetFromJsonAsync<PagedResult<CommentDto>>($"/api/cards/{card.Card.Id}/comments"))!.Items[0].Id);
    }
    [SqlFact]
    public async Task ConcurrentCardCreationWithSameBoardVersionHasOneWinner()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);using var c=Client(app,await User());var b=await Create(c);var col=await Col(c,b.Id);var v=await V(c,b.Id);
        var results=await Task.WhenAll(Enumerable.Range(0,2).Select(i=>c.PostAsJsonAsync($"/api/boards/{b.Id}/cards",new CreateCardRequest { ColumnId=col.Id,Title="Картка "+i,ExpectedBoardVersion=v })));
        Assert.Single(results,x=>x.StatusCode==HttpStatusCode.Created);Assert.Single(results,x=>x.StatusCode==HttpStatusCode.Conflict);Assert.Equal(1,(await c.GetFromJsonAsync<PagedResult<CardSummaryDto>>($"/api/boards/{b.Id}/cards"))!.TotalCount);
    }
}
