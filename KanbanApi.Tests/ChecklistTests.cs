using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using KanbanApi.Domain;
using KanbanContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace KanbanApi.Tests;
[Collection("sql")]
public sealed class ChecklistTests(SqlFixture fixture)
{
    internal static List<ChecklistItemInput> Items(int count=5)=>Enumerable.Range(0,count).Select(i=>new ChecklistItemInput{Id=Guid.NewGuid(),Title=$"Пункт {i+1}",IsCompleted=i<3}).ToList();
    internal async Task<(User User,Board Board,BoardColumn Col)> Seed()
    {
        var email=Guid.NewGuid()+"@example.test";var user=new User{Email=email,NormalizedEmail=email.ToUpperInvariant(),Name="Чек-лист",PasswordHash="unused"};var board=new Board{Name="Checklist test",OwnerId=user.Id};var col=new BoardColumn{BoardId=board.Id,Name="План"};
        await using var db=fixture.CreateContext();db.AddRange(user,board,col,new BoardMember{BoardId=board.Id,UserId=user.Id,Role=BoardRole.Admin});await db.SaveChangesAsync();return(user,board,col);
    }
    internal static HttpClient Client(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app,User user)
    {var c=app.CreateClient();c.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",app.Services.GetRequiredService<KanbanApi.Infrastructure.JwtService>().Issue(new(user.Id,user.Email,user.Name)).AccessToken);return c;}
    internal static async Task<string> V(HttpClient c,Guid board)=>(await c.GetFromJsonAsync<BoardDetailsDto>($"/api/boards/{board}"))!.Version;
    internal static async Task<CardDetailsDto> Create(HttpClient c,Guid board,Guid col,List<ChecklistItemInput>? items=null)
    {
        var r=await c.PostAsJsonAsync($"/api/boards/{board}/cards",new CreateCardRequest{Title="Картка",ColumnId=col,ExpectedBoardVersion=await V(c,board),Checklist=items});r.EnsureSuccessStatusCode();return (await r.Content.ReadFromJsonAsync<CardDetailsDto>())!;
    }
    internal static async Task<UpdateCardRequest> Body(HttpClient c,Guid board,CardDetailsDto card,List<ChecklistItemInput>? items)=>new(){Title=card.Card.Title,Description=card.Description,Priority=card.Card.Priority,ExpectedBoardVersion=await V(c,board),ExpectedVersion=card.Card.Version,Checklist=items};
    [SqlFact]
    public async Task CreateEditReorderRemoveAndProgressRoundTripWithLegacyPreservation()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);var g=await Seed();using var c=Client(app,g.User);var items=Items();var card=await Create(c,g.Board.Id,g.Col.Id,items);Assert.Equal(3,card.Card.ChecklistCompleted);Assert.Equal(5,card.Card.ChecklistTotal);Assert.Equal(items.Select(i=>i.Id),card.Checklist!.Select(i=>i.Id));
        var old=card.Card.Version;items.Reverse();items.RemoveAt(0);items[0].Title="  Відредаговано  ";items[0].IsCompleted=true;
        var r=await c.PutAsJsonAsync($"/api/cards/{card.Card.Id}",await Body(c,g.Board.Id,card,items));r.EnsureSuccessStatusCode();card=(await r.Content.ReadFromJsonAsync<CardDetailsDto>())!;
        Assert.NotEqual(old,card.Card.Version);Assert.Equal(4,card.Card.ChecklistTotal);Assert.Equal("Відредаговано",card.Checklist![0].Title);Assert.Equal(new[]{0,1,2,3},card.Checklist.Select(i=>i.Position));
        var legacy=await c.PutAsJsonAsync($"/api/cards/{card.Card.Id}",await Body(c,g.Board.Id,card,null));legacy.EnsureSuccessStatusCode();card=(await legacy.Content.ReadFromJsonAsync<CardDetailsDto>())!;Assert.Equal(4,card.Checklist!.Count);
        var summaries=(await c.GetFromJsonAsync<PagedResult<CardSummaryDto>>($"/api/boards/{g.Board.Id}/cards"))!;Assert.Equal(4,summaries.Items.Single().ChecklistTotal);
        var clear=await c.PutAsJsonAsync($"/api/cards/{card.Card.Id}",await Body(c,g.Board.Id,card,[]));clear.EnsureSuccessStatusCode();var empty=(await clear.Content.ReadFromJsonAsync<CardDetailsDto>())!;Assert.Empty(empty.Checklist!);Assert.Equal(0,empty.Card.ChecklistCompleted);
    }
    [SqlFact]
    public async Task InvalidChecklistNeverPartiallyUpdatesCardOrBoardAndForeignIdsAreRejected()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);var g=await Seed();using var c=Client(app,g.User);var card=await Create(c,g.Board.Id,g.Col.Id,Items(1));var other=await Create(c,g.Board.Id,g.Col.Id,Items(1));
        List<List<ChecklistItemInput>> invalid=[[new(){Id=Guid.Empty,Title="Ні"}],[new(){Id=Guid.NewGuid(),Title="  "}],[new(){Id=Guid.NewGuid(),Title=new string('x',301)}],Items(101),[new(){Id=other.Checklist![0].Id,Title="Чужий пункт"}]];var duplicate=Items(2);duplicate[1].Id=duplicate[0].Id;invalid.Add(duplicate);
        foreach(var items in invalid)
        {
            var version=await V(c,g.Board.Id);var body=await Body(c,g.Board.Id,card,items);body.Title="Не зберігати";var r=await c.PutAsJsonAsync($"/api/cards/{card.Card.Id}",body);Assert.Equal(HttpStatusCode.BadRequest,r.StatusCode);Assert.Equal(version,await V(c,g.Board.Id));var fresh=(await c.GetFromJsonAsync<CardDetailsDto>($"/api/cards/{card.Card.Id}"))!;Assert.Equal(card.Card.Version,fresh.Card.Version);Assert.Equal("Картка",fresh.Card.Title);Assert.Single(fresh.Checklist!);
        }
    }
    [SqlFact]
    public async Task ConcurrentEditsHaveOneWinnerAndPrivateCardCannotBeReadOrOverwritten()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);var g=await Seed();var stranger=await Seed();using var c=Client(app,g.User);using var outsider=Client(app,stranger.User);var card=await Create(c,g.Board.Id,g.Col.Id,Items());var body=await Body(c,g.Board.Id,card,Items(2));
        var results=await Task.WhenAll(c.PutAsJsonAsync($"/api/cards/{card.Card.Id}",body),c.PutAsJsonAsync($"/api/cards/{card.Card.Id}",body));Assert.Single(results,r=>r.StatusCode==HttpStatusCode.OK);Assert.Single(results,r=>r.StatusCode==HttpStatusCode.Conflict);
        Assert.Equal(HttpStatusCode.NotFound,(await outsider.GetAsync($"/api/cards/{card.Card.Id}")).StatusCode);Assert.Equal(HttpStatusCode.NotFound,(await outsider.PutAsJsonAsync($"/api/cards/{card.Card.Id}",body)).StatusCode);
        await using var db=fixture.CreateContext();Assert.Equal(2,await db.ChecklistItems.CountAsync(i=>i.CardId==card.Card.Id));
    }
    [SqlFact]
    public async Task DeletingCardColumnOrBoardCleansChecklistWithoutCascadePaths()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);
        foreach(var kind in new[]{"card","column","board"})
        {
            var g=await Seed();using var c=Client(app,g.User);var card=await Create(c,g.Board.Id,g.Col.Id,Items());var v=Uri.EscapeDataString(await V(c,g.Board.Id));var path=kind=="card"?$"/api/cards/{card.Card.Id}":kind=="column"?$"/api/boards/{g.Board.Id}/columns/{g.Col.Id}":$"/api/boards/{g.Board.Id}";var version=kind=="column"?Convert.ToBase64String(g.Col.RowVersion):card.Card.Version;
            var r=await c.DeleteAsync(path+$"?expectedBoardVersion={v}&expectedVersion={Uri.EscapeDataString(version)}");Assert.Equal(HttpStatusCode.NoContent,r.StatusCode);await using var db=fixture.CreateContext();Assert.False(await db.ChecklistItems.AnyAsync(i=>i.CardId==card.Card.Id));
        }
    }
    [SqlFact]
    public async Task RestrictedMemberCanEditChecklistAsCardFieldsAndArchiveKeepsItems()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);var g=await Seed();var peer=await Seed();using var c=Client(app,g.User);using var p=Client(app,peer.User);await using(var db=fixture.CreateContext()){db.Add(new BoardMember{BoardId=g.Board.Id,UserId=peer.User.Id,CanCreateCards=false,CanDeleteCards=false});await db.SaveChangesAsync();}
        var card=await Create(c,g.Board.Id,g.Col.Id,Items());var r=await p.PutAsJsonAsync($"/api/cards/{card.Card.Id}",await Body(p,g.Board.Id,card,Items(2)));r.EnsureSuccessStatusCode();card=(await r.Content.ReadFromJsonAsync<CardDetailsDto>())!;Assert.Equal(2,card.Card.ChecklistTotal);
        var archive=await c.PutAsJsonAsync($"/api/cards/{card.Card.Id}/archive",new ArchiveCardRequest{Archived=true,ExpectedBoardVersion=await V(c,g.Board.Id),ExpectedVersion=card.Card.Version});archive.EnsureSuccessStatusCode();var fresh=(await c.GetFromJsonAsync<CardDetailsDto>($"/api/cards/{card.Card.Id}"))!;Assert.True(fresh.Card.IsArchived);Assert.Equal(2,fresh.Checklist!.Count);
    }
}
