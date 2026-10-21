using System.Net;
using System.Net.Http.Json;
using KanbanApi.Domain;
using KanbanContracts;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace KanbanApi.Tests;
[Collection("sql")]
public sealed class CalendarTests(SqlFixture fixture)
{
    [SqlFact]
    public async Task RangeFilterPagingAndPrivacyAreAppliedBeforePagination()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);var g=await new NotificationTests(fixture).Pair();using var a=ChecklistTests.Client(app,g.Owner);var card=await ChecklistTests.Create(a,g.Board.Id,g.Column.Id);
        await using(var db=fixture.CreateContext())
        {var c=await db.Cards.SingleAsync(c=>c.Id==card.Card.Id);c.DueDate=new(2026,10,21);c.AssigneeId=g.Peer.Id;db.Cards.AddRange(new Card{ColumnId=g.Column.Id,Title="Без дати"},new Card{ColumnId=g.Column.Id,Title="Межа",DueDate=new(2026,11,1)},new Card{ColumnId=g.Column.Id,Title="Архів",DueDate=new(2026,10,22),IsArchived=true});await db.SaveChangesAsync();}
        var page=(await a.GetFromJsonAsync<PagedResult<CalendarCardDto>>("/api/me/calendar?from=2026-10-01&to=2026-11-01&limit=1"))!;Assert.Equal(2,page.TotalCount);Assert.Single(page.Items);
        var filtered=(await a.GetFromJsonAsync<PagedResult<CalendarCardDto>>($"/api/me/calendar?from=2026-10-01&to=2026-11-01&assigneeId={g.Peer.Id}"))!;Assert.Equal(card.Card.Id,Assert.Single(filtered.Items).Id);Assert.Equal(g.Peer.Id,filtered.Items[0].Assignee!.Id);
        Assert.Single((await a.GetFromJsonAsync<PagedResult<CalendarCardDto>>("/api/me/calendar?from=2026-10-01&to=2026-11-01&unassigned=true"))!.Items);
        var stranger=await new ChecklistTests(fixture).Seed();using var b=ChecklistTests.Client(app,stranger.User);Assert.Empty((await b.GetFromJsonAsync<PagedResult<CalendarCardDto>>("/api/me/calendar?from=2026-10-01&to=2026-11-01"))!.Items);
        Assert.Equal(HttpStatusCode.BadRequest,(await a.GetAsync("/api/me/calendar?from=2026-10-01&to=2027-11-01")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await a.GetAsync("/api/me/calendar?from=2026-10-22&to=2026-10-21")).StatusCode);
    }
    [SqlFact]
    public async Task DeadlineOnlyChangePreservesFieldsAndRejectsStaleOrRevokedWrites()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);var g=await new NotificationTests(fixture).Pair();using var a=ChecklistTests.Client(app,g.Owner);using var b=ChecklistTests.Client(app,g.Peer);var card=await ChecklistTests.Create(a,g.Board.Id,g.Column.Id);
        var request=new ChangeDeadlineRequest{DueDate=new(2026,10,23),ExpectedVersion=card.Card.Version,ExpectedBoardVersion=await ChecklistTests.V(a,g.Board.Id)};
        var path=$"/api/cards/{card.Card.Id}/deadline";Assert.Equal(HttpStatusCode.NoContent,(await b.PutAsJsonAsync(path,request)).StatusCode);var after=(await a.GetFromJsonAsync<CardDetailsDto>($"/api/cards/{card.Card.Id}"))!;Assert.Equal(request.DueDate,after.Card.DueDate);Assert.Equal(card.Card.Title,after.Card.Title);Assert.Equal(card.Checklist,after.Checklist);
        Assert.Equal(HttpStatusCode.Conflict,(await b.PutAsJsonAsync(path,request)).StatusCode);Assert.Equal(request.DueDate,(await a.GetFromJsonAsync<CardDetailsDto>($"/api/cards/{card.Card.Id}"))!.Card.DueDate);
        await using(var db=fixture.CreateContext()){await db.BoardMembers.Where(m=>m.BoardId==g.Board.Id&&m.UserId==g.Peer.Id).ExecuteDeleteAsync();}
        Assert.Equal(HttpStatusCode.NotFound,(await b.PutAsJsonAsync(path,request)).StatusCode);
        request.ExpectedVersion=after.Card.Version;request.ExpectedBoardVersion=await ChecklistTests.V(a,g.Board.Id);request.DueDate=null;Assert.Equal(HttpStatusCode.NoContent,(await a.PutAsJsonAsync(path,request)).StatusCode);
    }
    [SqlFact]
    public async Task ArchivedCardsCannotBeRescheduledAndAnonymousIsDenied()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);var g=await new NotificationTests(fixture).Pair();using var a=ChecklistTests.Client(app,g.Owner);var card=await ChecklistTests.Create(a,g.Board.Id,g.Column.Id);
        await using(var db=fixture.CreateContext()){await db.Boards.Where(b=>b.Id==g.Board.Id).ExecuteUpdateAsync(s=>s.SetProperty(b=>b.IsArchived,true));}
        Assert.Equal(HttpStatusCode.Conflict,(await a.PutAsJsonAsync($"/api/cards/{card.Card.Id}/deadline",new ChangeDeadlineRequest{DueDate=new(2026,10,23),ExpectedVersion=card.Card.Version,ExpectedBoardVersion=await ChecklistTests.V(a,g.Board.Id)})).StatusCode);
        Assert.Empty((await a.GetFromJsonAsync<PagedResult<CalendarCardDto>>("/api/me/calendar?from=2026-10-01&to=2026-11-01"))!.Items);using var anonymous=app.CreateClient();Assert.Equal(HttpStatusCode.Unauthorized,(await anonymous.GetAsync("/api/me/calendar?from=2026-10-01&to=2026-11-01")).StatusCode);
    }
}
