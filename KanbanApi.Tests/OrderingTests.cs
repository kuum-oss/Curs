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
public sealed class OrderingTests(SqlFixture fixture)
{
    private async Task<User> User()
    {
        var email=Guid.NewGuid().ToString("N")+"@example.test";var user=new User{Email=email,NormalizedEmail=email.ToUpperInvariant(),Name="Учасник",PasswordHash="unused"};
        await using var db=fixture.CreateContext();db.Users.Add(user);await db.SaveChangesAsync();return user;
    }
    private static HttpClient Client(WebApplicationFactory<Program> app,User user)
    { var c=app.CreateClient();c.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",app.Services.GetRequiredService<JwtService>().Issue(new(user.Id,user.Email,user.Name)).AccessToken);return c; }
    private static async Task<T> Create<T>(HttpResponseMessage response)
    { Assert.Equal(HttpStatusCode.Created,response.StatusCode);return (await response.Content.ReadFromJsonAsync<T>())!; }
    private static async Task<BoardDetailsDto> Board(HttpClient c)=>await Create<BoardDetailsDto>(await c.PostAsJsonAsync("/api/boards",new CreateBoardRequest{Name="Порядок"}));
    private static async Task<string> V(HttpClient c,Guid board)=>(await c.GetFromJsonAsync<BoardDetailsDto>($"/api/boards/{board}"))!.Version;
    private static async Task<ColumnDto> Col(HttpClient c,Guid board)=>await Create<ColumnDto>(await c.PostAsJsonAsync($"/api/boards/{board}/columns",new CreateColumnRequest{Name="Колонка",ExpectedBoardVersion=await V(c,board)}));
    private static async Task<CardDetailsDto> Card(HttpClient c,Guid board,Guid col,string title)=>await Create<CardDetailsDto>(await c.PostAsJsonAsync($"/api/boards/{board}/cards",new CreateCardRequest{ColumnId=col,Title=title,Description="Опис "+title,DueDate=new(2027,1,1),Priority=CardPriority.High,ExpectedBoardVersion=await V(c,board)}));
    private static async Task<MoveCardRequest> Move(HttpClient c,Guid board,Guid id,Guid target,Guid? before=null)=>new(){TargetColumnId=target,BeforeCardId=before,ExpectedBoardVersion=await V(c,board),ExpectedVersion=(await c.GetFromJsonAsync<CardDetailsDto>($"/api/cards/{id}"))!.Card.Version};
    private static async Task<PagedResult<CardSummaryDto>> Cards(HttpClient c,Guid board)=>(await c.GetFromJsonAsync<PagedResult<CardSummaryDto>>($"/api/boards/{board}/cards"))!;
    private static void Positions(IEnumerable<CardSummaryDto> cards)=>Assert.Equal(Enumerable.Range(0,cards.Count()),cards.Select(c=>c.Position));

    [SqlFact]
    public async Task MovesBetweenAndWithinColumnsPersistBeginningMiddleAndEnd()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);using var c=Client(app,await User());var b=await Board(c);var a=await Col(c,b.Id);var z=await Col(c,b.Id);var one=await Card(c,b.Id,a.Id,"1");var two=await Card(c,b.Id,a.Id,"2");var three=await Card(c,b.Id,z.Id,"3");
        await Create<CommentDto>(await c.PostAsJsonAsync($"/api/cards/{two.Card.Id}/comments",new CreateCommentRequest{Text="Історія",ExpectedBoardVersion=await V(c,b.Id),ExpectedVersion=two.Card.Version}));
        foreach(var step in new[]{(two.Card.Id,z.Id,(Guid?)three.Card.Id),(one.Card.Id,z.Id,(Guid?)null),(one.Card.Id,z.Id,(Guid?)two.Card.Id),(one.Card.Id,a.Id,(Guid?)null)})
        {
            var response=await c.PostAsJsonAsync($"/api/cards/{step.Item1}/move",await Move(c,b.Id,step.Item1,step.Item2,step.Item3));Assert.Equal(HttpStatusCode.OK,response.StatusCode);
            var result=(await response.Content.ReadFromJsonAsync<MoveResultDto>())!;Assert.Equal(await V(c,b.Id),result.BoardVersion);
            var rows=(await Cards(c,b.Id)).Items;Positions(rows.Where(x=>x.ColumnId==a.Id));Positions(rows.Where(x=>x.ColumnId==z.Id));
        }
        var final=(await Cards(c,b.Id)).Items;Assert.Equal(new[]{two.Card.Id,three.Card.Id},final.Where(x=>x.ColumnId==z.Id).Select(x=>x.Id));
        var details=(await c.GetFromJsonAsync<CardDetailsDto>($"/api/cards/{two.Card.Id}"))!;Assert.Equal(two.Description,details.Description);Assert.Equal(two.Card.Priority,details.Card.Priority);Assert.Equal(two.Card.DueDate,details.Card.DueDate);Assert.Equal(1,(await c.GetFromJsonAsync<PagedResult<CommentDto>>($"/api/cards/{two.Card.Id}/comments"))!.TotalCount);
    }
    [SqlFact]
    public async Task ColumnOrderRequiresCompleteUniquePermutationAndPersistsVersions()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);using var c=Client(app,await User());var b=await Board(c);var a=await Col(c,b.Id);var z=await Col(c,b.Id);var other=await Board(c);var foreign=await Col(c,other.Id);var version=await V(c,b.Id);
        foreach(var ids in new[]{new List<Guid>{a.Id},new List<Guid>{a.Id,a.Id},new List<Guid>{a.Id,foreign.Id}})
        { Assert.Equal(HttpStatusCode.BadRequest,(await c.PutAsJsonAsync($"/api/boards/{b.Id}/columns/order",new ReorderColumnsRequest{ColumnIds=ids,ExpectedBoardVersion=version})).StatusCode);Assert.Equal(version,await V(c,b.Id)); }
        Assert.Equal(HttpStatusCode.OK,(await c.PutAsJsonAsync($"/api/boards/{b.Id}/columns/order",new ReorderColumnsRequest{ColumnIds=[z.Id,a.Id],ExpectedBoardVersion=version})).StatusCode);
        var rows=(await c.GetFromJsonAsync<List<ColumnDto>>($"/api/boards/{b.Id}/columns"))!;Assert.Equal(new[]{z.Id,a.Id},rows.Select(x=>x.Id));Assert.Equal(new[]{0,1},rows.Select(x=>x.Position));Assert.NotEqual(z.Version,rows[0].Version);Assert.NotEqual(version,await V(c,b.Id));
        Assert.Equal(HttpStatusCode.Conflict,(await c.PutAsJsonAsync($"/api/boards/{b.Id}/columns/order",new ReorderColumnsRequest{ColumnIds=[a.Id,z.Id],ExpectedBoardVersion=version})).StatusCode);
    }
    [SqlFact]
    public async Task ForeignTargetsWrongAnchorsAndPrivateIdsCannotChangeOrder()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);using var c=Client(app,await User());using var stranger=Client(app,await User());using var anonymous=app.CreateClient();var b=await Board(c);var a=await Col(c,b.Id);var z=await Col(c,b.Id);var card=await Card(c,b.Id,a.Id,"Картка");var anchor=await Card(c,b.Id,a.Id,"Опора");var other=await Board(c);var foreign=await Col(c,other.Id);var version=await V(c,b.Id);
        Assert.Equal(HttpStatusCode.NotFound,(await c.PostAsJsonAsync($"/api/cards/{card.Card.Id}/move",await Move(c,b.Id,card.Card.Id,foreign.Id))).StatusCode);
        foreach(var before in new[]{anchor.Card.Id,card.Card.Id,Guid.NewGuid()})Assert.Equal(HttpStatusCode.BadRequest,(await c.PostAsJsonAsync($"/api/cards/{card.Card.Id}/move",await Move(c,b.Id,card.Card.Id,z.Id,before))).StatusCode);
        Assert.Equal(version,await V(c,b.Id));var valid=await Move(c,b.Id,card.Card.Id,z.Id);
        Assert.Equal(HttpStatusCode.NotFound,(await stranger.PostAsJsonAsync($"/api/cards/{card.Card.Id}/move",valid)).StatusCode);Assert.Equal(HttpStatusCode.NotFound,(await stranger.PutAsJsonAsync($"/api/boards/{b.Id}/columns/order",new ReorderColumnsRequest{ColumnIds=[z.Id,a.Id],ExpectedBoardVersion=version})).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await anonymous.PostAsJsonAsync($"/api/cards/{card.Card.Id}/move",valid)).StatusCode);Assert.Equal(HttpStatusCode.Unauthorized,(await anonymous.PutAsJsonAsync($"/api/boards/{b.Id}/columns/order",new ReorderColumnsRequest{ColumnIds=[z.Id,a.Id],ExpectedBoardVersion=version})).StatusCode);
        valid.ExpectedVersion=Convert.ToBase64String(new byte[8]);Assert.Equal(HttpStatusCode.Conflict,(await c.PostAsJsonAsync($"/api/cards/{card.Card.Id}/move",valid)).StatusCode);Assert.Equal(version,await V(c,b.Id));
        Assert.Equal(HttpStatusCode.BadRequest,(await c.PostAsJsonAsync($"/api/cards/{card.Card.Id}/move",new {targetColumnId=z.Id})).StatusCode);
    }
    [SqlFact]
    public async Task RestrictedMemberCanMoveAndReorderButRevokedMemberCannot()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);var person=await User();using var a=Client(app,await User());using var m=Client(app,person);var b=await Board(a);var col=await Col(a,b.Id);var target=await Col(a,b.Id);var card=await Card(a,b.Id,col.Id,"Картка");
        await a.PostAsJsonAsync($"/api/boards/{b.Id}/members",new AddMemberRequest{Email=person.Email,ExpectedBoardVersion=await V(a,b.Id)});await a.PatchAsJsonAsync($"/api/boards/{b.Id}/members/{person.Id}",new UpdateMemberPermissionsRequest{CanCreateCards=false,CanDeleteCards=false,ExpectedBoardVersion=await V(a,b.Id)});
        Assert.Equal(HttpStatusCode.OK,(await m.PostAsJsonAsync($"/api/cards/{card.Card.Id}/move",await Move(m,b.Id,card.Card.Id,target.Id))).StatusCode);Assert.Equal(HttpStatusCode.OK,(await m.PutAsJsonAsync($"/api/boards/{b.Id}/columns/order",new ReorderColumnsRequest{ColumnIds=[target.Id,col.Id],ExpectedBoardVersion=await V(m,b.Id)})).StatusCode);
        var body=await Move(m,b.Id,card.Card.Id,col.Id);await a.DeleteAsync($"/api/boards/{b.Id}/members/{person.Id}?expectedBoardVersion="+Uri.EscapeDataString(await V(a,b.Id)));Assert.Equal(HttpStatusCode.NotFound,(await m.PostAsJsonAsync($"/api/cards/{card.Card.Id}/move",body)).StatusCode);
    }
    [SqlFact]
    public async Task SimultaneousMovesHaveOneWinnerAndNoDuplicatePositions()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);using var c=Client(app,await User());var b=await Board(c);var a=await Col(c,b.Id);var z=await Col(c,b.Id);var one=await Card(c,b.Id,a.Id,"1");var two=await Card(c,b.Id,a.Id,"2");var first=await Move(c,b.Id,one.Card.Id,z.Id);var second=await Move(c,b.Id,two.Card.Id,z.Id);
        var responses=await Task.WhenAll(c.PostAsJsonAsync($"/api/cards/{one.Card.Id}/move",first),c.PostAsJsonAsync($"/api/cards/{two.Card.Id}/move",second));Assert.Single(responses,x=>x.StatusCode==HttpStatusCode.OK);Assert.Single(responses,x=>x.StatusCode==HttpStatusCode.Conflict);
        var rows=(await Cards(c,b.Id)).Items;Assert.Equal(2,rows.Select(x=>x.Id).Distinct().Count());Positions(rows.Where(x=>x.ColumnId==a.Id));Positions(rows.Where(x=>x.ColumnId==z.Id));
    }
    [SqlFact]
    public async Task LateMoveFailureRollsBackEveryPositionAndBothVersions()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);using var c=Client(app,await User());var b=await Board(c);var a=await Col(c,b.Id);var z=await Col(c,b.Id);var one=await Card(c,b.Id,a.Id,"1");await Card(c,b.Id,a.Id,"2");var before=(await Cards(c,b.Id)).Items;var body=await Move(c,b.Id,one.Card.Id,z.Id);
        await using var db=fixture.CreateContext();await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER Ph05MoveFailure ON [Cards] AFTER UPDATE AS BEGIN THROW 51000, 'Synthetic PH05 move failure', 1; END;");
        try { Assert.Equal(HttpStatusCode.InternalServerError,(await c.PostAsJsonAsync($"/api/cards/{one.Card.Id}/move",body)).StatusCode);Assert.Equal(body.ExpectedBoardVersion,await V(c,b.Id));Assert.Equal(before.Select(x=>(x.Id,x.ColumnId,x.Position,x.Version)),(await Cards(c,b.Id)).Items.Select(x=>(x.Id,x.ColumnId,x.Position,x.Version))); }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER Ph05MoveFailure;"); }
    }
    [SqlFact]
    public async Task LateColumnOrderFailureRollsBackWholePermutation()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);using var c=Client(app,await User());var b=await Board(c);var a=await Col(c,b.Id);var z=await Col(c,b.Id);var v=await V(c,b.Id);
        await using var db=fixture.CreateContext();await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER Ph05OrderFailure ON [Columns] AFTER UPDATE AS BEGIN THROW 51000, 'Synthetic PH05 order failure', 1; END;");
        try { Assert.Equal(HttpStatusCode.InternalServerError,(await c.PutAsJsonAsync($"/api/boards/{b.Id}/columns/order",new ReorderColumnsRequest{ColumnIds=[z.Id,a.Id],ExpectedBoardVersion=v})).StatusCode);Assert.Equal(v,await V(c,b.Id));Assert.Equal(new[]{a.Id,z.Id},(await c.GetFromJsonAsync<List<ColumnDto>>($"/api/boards/{b.Id}/columns"))!.Select(x=>x.Id)); }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER Ph05OrderFailure;"); }
    }
}
