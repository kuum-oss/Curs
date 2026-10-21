using System.Net;
using System.Net.Http.Json;
using KanbanApi.Domain;
using KanbanContracts;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace KanbanApi.Tests;
[Collection("sql")]
public sealed class InsightsTests(SqlFixture fixture)
{
    [SqlFact]
    public async Task AllMutationPathsRecordAtomicBeforeAfterAndRejectStaleOrForeignWrites()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);var g=await new NotificationTests(fixture).Pair();using var a=ChecklistTests.Client(app,g.Owner);
        var card=await ChecklistTests.Create(a,g.Board.Id,g.Column.Id);
        var create=(await a.GetFromJsonAsync<PagedResult<ActivityDto>>($"/api/boards/{g.Board.Id}/history"))!;Assert.Equal("created",Assert.Single(create.Items).Field);
        var body=await ChecklistTests.Body(a,g.Board.Id,card,null);body.Title="Нова назва";body.DueDate=new(2026,10,24);body.AssigneeId=g.Peer.Id;body.Priority=CardPriority.Critical;
        (await a.PutAsJsonAsync($"/api/cards/{card.Card.Id}",body)).EnsureSuccessStatusCode();
        var history=(await a.GetFromJsonAsync<PagedResult<ActivityDto>>($"/api/boards/{g.Board.Id}/history?cardId={card.Card.Id}"))!;Assert.Equal(5,history.TotalCount);
        Assert.Contains(history.Items,e=>e.Field=="priority"&&e.Before=="Medium"&&e.After=="Critical"&&e.Actor.Id==g.Owner.Id);
        Assert.Contains(history.Items,e=>e.Field=="assignee"&&e.After==g.Peer.Name);
        Assert.Equal(HttpStatusCode.Conflict,(await a.PutAsJsonAsync($"/api/cards/{card.Card.Id}",body)).StatusCode);
        Assert.Equal(5,(await a.GetFromJsonAsync<PagedResult<ActivityDto>>($"/api/boards/{g.Board.Id}/history"))!.TotalCount);
        card=(await a.GetFromJsonAsync<CardDetailsDto>($"/api/cards/{card.Card.Id}"))!;
        (await a.PutAsJsonAsync($"/api/cards/{card.Card.Id}/deadline",new ChangeDeadlineRequest{DueDate=null,ExpectedVersion=card.Card.Version,ExpectedBoardVersion=await ChecklistTests.V(a,g.Board.Id)})).EnsureSuccessStatusCode();
        var filtered=(await a.GetFromJsonAsync<PagedResult<ActivityDto>>($"/api/boards/{g.Board.Id}/history?field=deadline&search=нова&limit=1"))!;Assert.Equal(2,filtered.TotalCount);Assert.Null(Assert.Single(filtered.Items).After);
        var other=await new ChecklistTests(fixture).Seed();using var outsider=ChecklistTests.Client(app,other.User);
        foreach(var suffix in new[]{"history","statistics"})Assert.Equal(HttpStatusCode.NotFound,(await outsider.GetAsync($"/api/boards/{g.Board.Id}/{suffix}")).StatusCode);
        using var anon=app.CreateClient();Assert.Equal(HttpStatusCode.Unauthorized,(await anon.GetAsync($"/api/boards/{g.Board.Id}/history")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await a.GetAsync($"/api/boards/{g.Board.Id}/history?days=1000000")).StatusCode);
    }
    [SqlFact]
    public async Task ExplicitCompletionCountsOnceAndHonestBaselineHandlesReopeningAndMissingConfiguration()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);var g=await new ChecklistTests(fixture).Seed();using var a=ChecklistTests.Client(app,g.User);
        var card=await ChecklistTests.Create(a,g.Board.Id,g.Col.Id);
        var colResponse=await a.PostAsJsonAsync($"/api/boards/{g.Board.Id}/columns",new CreateColumnRequest{Name="Будь-яка назва",ExpectedBoardVersion=await ChecklistTests.V(a,g.Board.Id)});colResponse.EnsureSuccessStatusCode();var done=(await colResponse.Content.ReadFromJsonAsync<ColumnDto>())!;
        var empty=(await a.GetFromJsonAsync<BoardStatisticsDto>($"/api/boards/{g.Board.Id}/statistics"))!;Assert.Null(empty.CompletionColumnId);Assert.Null(empty.PreviousCompleted);Assert.Null(empty.MeanDays);
        (await a.PutAsJsonAsync($"/api/boards/{g.Board.Id}/completion-column",new CompletionColumnRequest{ColumnId=done.Id,ExpectedBoardVersion=await ChecklistTests.V(a,g.Board.Id)})).EnsureSuccessStatusCode();
        async Task Move(Guid col){card=(await a.GetFromJsonAsync<CardDetailsDto>($"/api/cards/{card.Card.Id}"))!;(await a.PostAsJsonAsync($"/api/cards/{card.Card.Id}/move",new MoveCardRequest{TargetColumnId=col,ExpectedBoardVersion=await ChecklistTests.V(a,g.Board.Id),ExpectedVersion=card.Card.Version})).EnsureSuccessStatusCode();}
        await Move(done.Id);await Move(g.Col.Id);await Move(done.Id);
        var stats=(await a.GetFromJsonAsync<BoardStatisticsDto>($"/api/boards/{g.Board.Id}/statistics"))!;
        Assert.Equal(1,stats.Completed);Assert.Equal(1,stats.Days.Sum(d=>d.Count));Assert.Equal(7,stats.Days.Count);Assert.Equal(0,stats.InProgress);Assert.NotNull(stats.MeanDays);Assert.False(stats.HasPreviousPeriod);
        var rows=(await a.GetFromJsonAsync<PagedResult<ActivityDto>>($"/api/boards/{g.Board.Id}/history?field=column"))!;Assert.Equal(3,rows.TotalCount);
        var stranger=await new ChecklistTests(fixture).Seed();Assert.Equal(HttpStatusCode.NotFound,(await a.PutAsJsonAsync($"/api/boards/{g.Board.Id}/completion-column",new CompletionColumnRequest{ColumnId=stranger.Col.Id,ExpectedBoardVersion=await ChecklistTests.V(a,g.Board.Id)})).StatusCode);
    }
    [SqlFact]
    public async Task PreviousStateWorkloadAndTimezoneBucketsAreCalculatedFromHistory()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);var g=await new ChecklistTests(fixture).Seed();using var a=ChecklistTests.Client(app,g.User);
        var card=await ChecklistTests.Create(a,g.Board.Id,g.Col.Id);
        await using(var db=fixture.CreateContext())
        {
            var board=await db.Boards.SingleAsync(b=>b.Id==g.Board.Id);board.InsightsSinceUtc=new(2026,9,1,0,0,0,DateTimeKind.Utc);board.CompletionColumnId=g.Col.Id;
            var c=await db.Cards.SingleAsync(c=>c.Id==card.Card.Id);c.CreatedAtUtc=new(2026,10,1);c.AssigneeId=g.User.Id;
            var creation=await db.BoardActivities.SingleAsync(e=>e.CardId==c.Id);creation.CreatedAtUtc=new(2026,10,14,22,30,0,DateTimeKind.Utc);creation.CardCreatedAtUtc=c.CreatedAtUtc;
            await db.SaveChangesAsync();
        }
        var stats=(await a.GetFromJsonAsync<BoardStatisticsDto>($"/api/boards/{g.Board.Id}/statistics?today=2026-10-21&utcOffsetMinutes=180"))!;
        Assert.True(stats.HasPreviousPeriod);Assert.Equal(1,stats.Completed);Assert.Equal(1,stats.Days[0].Count);Assert.Equal(0,stats.PreviousCompleted);Assert.Equal(0,stats.InProgress);Assert.Equal(1,stats.Workload.Single().Total);
        Assert.Equal(HttpStatusCode.BadRequest,(await a.GetAsync($"/api/boards/{g.Board.Id}/statistics?utcOffsetMinutes=9999")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await a.GetAsync($"/api/boards/{g.Board.Id}/statistics?today=0001-01-01")).StatusCode);
    }
    [SqlFact]
    public async Task RevocationDeniesBothFeedsAndDeletedCardKeepsSafeHistory()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);var g=await new NotificationTests(fixture).Pair();using var a=ChecklistTests.Client(app,g.Owner);using var b=ChecklistTests.Client(app,g.Peer);
        var card=await ChecklistTests.Create(a,g.Board.Id,g.Column.Id);
        (await a.DeleteAsync($"/api/cards/{card.Card.Id}?expectedVersion={Uri.EscapeDataString(card.Card.Version)}&expectedBoardVersion={Uri.EscapeDataString(await ChecklistTests.V(a,g.Board.Id))}")).EnsureSuccessStatusCode();
        var history=(await a.GetFromJsonAsync<PagedResult<ActivityDto>>($"/api/boards/{g.Board.Id}/history"))!;Assert.Contains(history.Items,e=>e.Field=="deleted");Assert.All(history.Items,e=>Assert.False(e.CanOpen));
        await using(var db=fixture.CreateContext()){await db.BoardMembers.Where(m=>m.BoardId==g.Board.Id&&m.UserId==g.Peer.Id).ExecuteDeleteAsync();}
        foreach(var suffix in new[]{"history","statistics"})Assert.Equal(HttpStatusCode.NotFound,(await b.GetAsync($"/api/boards/{g.Board.Id}/{suffix}")).StatusCode);
    }
    [SqlFact]
    public async Task CompleteHistoryRemainsReadableAndMigrationCannotDiscardIt()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);var g=await new ChecklistTests(fixture).Seed();using var a=ChecklistTests.Client(app,g.User);var card=await ChecklistTests.Create(a,g.Board.Id,g.Col.Id);
        await using(var db=fixture.CreateContext()){await db.BoardActivities.Where(e=>e.CardId==card.Card.Id).ExecuteUpdateAsync(s=>s.SetProperty(e=>e.CreatedAtUtc,DateTime.UtcNow.AddDays(-100)));}
        Assert.Empty((await a.GetFromJsonAsync<PagedResult<ActivityDto>>($"/api/boards/{g.Board.Id}/history"))!.Items);
        Assert.Single((await a.GetFromJsonAsync<PagedResult<ActivityDto>>($"/api/boards/{g.Board.Id}/history?days=0"))!.Items);
        var older=await ChecklistTests.Create(a,g.Board.Id,g.Col.Id);
        await using(var db=fixture.CreateContext()){await db.BoardActivities.Where(e=>e.CardId==older.Card.Id).ExecuteUpdateAsync(s=>s.SetProperty(e=>e.CreatedAtUtc,DateTime.UtcNow.AddDays(-200)));}
        var chronology=(await a.GetFromJsonAsync<PagedResult<ActivityDto>>($"/api/boards/{g.Board.Id}/history?days=0"))!;
        Assert.Equal(new[]{card.Card.Id,older.Card.Id},chronology.Items.Select(e=>e.CardId));
        Assert.Equal(card.Card.Id,(await a.GetFromJsonAsync<BoardStatisticsDto>($"/api/boards/{g.Board.Id}/statistics"))!.Recent[0].CardId);
        var f=new SqlFixture();await f.InitializeAsync();try
        {
            await using var db=f.CreateContext();var b=new Board{Name="Зберегти історію"};db.Boards.Add(b);db.BoardActivities.Add(new(){BoardId=b.Id,CardId=Guid.NewGuid(),Field="created",CardTitle="Стара картка",CreatedAtUtc=DateTime.UtcNow,CardCreatedAtUtc=DateTime.UtcNow});await db.SaveChangesAsync();
            var ex=await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(()=>Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(db).MigrateAsync("20261021153418_BoardBackupMetadata"));Assert.Equal(50011,ex.Number);Assert.Single(await db.BoardActivities.ToListAsync());
        }finally{await f.DisposeAsync();}
    }

}
