using System.Net;
using System.Net.Http.Json;
using Curs.Services;
using Curs.ViewModels;
using KanbanApi.Infrastructure;
using KanbanContracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KanbanApi.Tests;

[Collection("sql")]
public sealed class CardCompletionTests(SqlFixture fixture)
{
    private static async Task<HttpResponseMessage> Complete(HttpClient client, Guid board, Guid card, bool create = false)
    {
        var current = (await client.GetFromJsonAsync<CardDetailsDto>($"/api/cards/{card}"))!;
        return await client.PostAsJsonAsync($"/api/cards/{card}/complete", new CompleteCardRequest
        { ExpectedBoardVersion = await ChecklistTests.V(client, board), ExpectedVersion = current.Card.Version, CreateCompletionColumn = create });
    }

    [SqlFact]
    public async Task CompletionWithoutColumnPersistsCountsOnceAndReopensByMoving()
    {
        using var app = ApiFoundationTests.Factory(fixture.ConnectionString);
        var g = await new ChecklistTests(fixture).Seed(); using var a = ChecklistTests.Client(app, g.User);
        var card = await ChecklistTests.Create(a, g.Board.Id, g.Col.Id);
        (await Complete(a, g.Board.Id, card.Card.Id)).EnsureSuccessStatusCode();
        var done = (await a.GetFromJsonAsync<CardDetailsDto>($"/api/cards/{card.Card.Id}"))!;
        Assert.True(done.Card.IsCompleted); Assert.Equal(g.Col.Id, done.Card.ColumnId);
        (await Complete(a, g.Board.Id, card.Card.Id)).EnsureSuccessStatusCode();
        var stats = (await a.GetFromJsonAsync<BoardStatisticsDto>($"/api/boards/{g.Board.Id}/statistics"))!;
        Assert.Null(stats.CompletionColumnId); Assert.Equal(1, stats.Completed); Assert.Equal(1, stats.CompletedTotal);
        Assert.Equal(1, stats.Days.Sum(d => d.Count)); Assert.Equal(0, stats.InProgress); Assert.NotNull(stats.MeanDays);
        var history = (await a.GetFromJsonAsync<PagedResult<ActivityDto>>($"/api/boards/{g.Board.Id}/history?field=completion"))!;
        Assert.Single(history.Items);
        var col = await a.PostAsJsonAsync($"/api/boards/{g.Board.Id}/columns", new CreateColumnRequest { Name = "Знову в роботі", ExpectedBoardVersion = await ChecklistTests.V(a, g.Board.Id) });
        var target = (await col.Content.ReadFromJsonAsync<ColumnDto>())!;
        done = (await a.GetFromJsonAsync<CardDetailsDto>($"/api/cards/{card.Card.Id}"))!;
        (await a.PostAsJsonAsync($"/api/cards/{card.Card.Id}/move", new MoveCardRequest { TargetColumnId = target.Id, ExpectedBoardVersion = await ChecklistTests.V(a, g.Board.Id), ExpectedVersion = done.Card.Version })).EnsureSuccessStatusCode();
        Assert.False((await a.GetFromJsonAsync<CardDetailsDto>($"/api/cards/{card.Card.Id}"))!.Card.IsCompleted);
        stats = (await a.GetFromJsonAsync<BoardStatisticsDto>($"/api/boards/{g.Board.Id}/statistics"))!;
        Assert.Equal(0, stats.CompletedTotal); Assert.Equal(1, stats.InProgress); Assert.Equal(1, stats.Completed);
    }

    [SqlFact]
    public async Task CreateOrReuseDoneColumnMovesAndDoesNotDuplicateStatistics()
    {
        using var app = ApiFoundationTests.Factory(fixture.ConnectionString);
        var g = await new ChecklistTests(fixture).Seed(); using var a = ChecklistTests.Client(app, g.User);
        var first = await ChecklistTests.Create(a, g.Board.Id, g.Col.Id);
        (await Complete(a, g.Board.Id, first.Card.Id, true)).EnsureSuccessStatusCode();
        var stats = (await a.GetFromJsonAsync<BoardStatisticsDto>($"/api/boards/{g.Board.Id}/statistics"))!;
        Assert.NotNull(stats.CompletionColumnId); Assert.Equal(1, stats.Completed); Assert.Equal(1, stats.CompletedTotal);
        var second = await ChecklistTests.Create(a, g.Board.Id, g.Col.Id);
        (await Complete(a, g.Board.Id, second.Card.Id, true)).EnsureSuccessStatusCode();
        Assert.Equal(stats.CompletionColumnId, (await a.GetFromJsonAsync<CardDetailsDto>($"/api/cards/{second.Card.Id}"))!.Card.ColumnId);
        Assert.Single((await a.GetFromJsonAsync<List<ColumnDto>>($"/api/boards/{g.Board.Id}/columns"))!, c => c.Name == "Готово");
        var stale = new CompleteCardRequest { ExpectedBoardVersion = stats.BoardVersion, ExpectedVersion = first.Card.Version };
        Assert.Equal(HttpStatusCode.Conflict, (await a.PostAsJsonAsync($"/api/cards/{first.Card.Id}/complete", stale)).StatusCode);
    }

    [SqlFact]
    public async Task MemberMayMarkWithoutColumnButCannotConfigureAndForeignUserCannotWrite()
    {
        using var app = ApiFoundationTests.Factory(fixture.ConnectionString);
        var g = await new NotificationTests(fixture).Pair(); using var a = ChecklistTests.Client(app, g.Owner); using var peer = ChecklistTests.Client(app, g.Peer);
        var card = await ChecklistTests.Create(a, g.Board.Id, g.Column.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await Complete(peer, g.Board.Id, card.Card.Id, true)).StatusCode);
        (await Complete(peer, g.Board.Id, card.Card.Id)).EnsureSuccessStatusCode();
        var outsider = await new ChecklistTests(fixture).Seed(); using var other = ChecklistTests.Client(app, outsider.User);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"/api/cards/{card.Card.Id}/complete", new CompleteCardRequest { ExpectedBoardVersion = await ChecklistTests.V(a, g.Board.Id), ExpectedVersion = card.Card.Version })).StatusCode);
        var backup = (await a.GetFromJsonAsync<BoardBackup>($"/api/boards/{g.Board.Id}/backup"))!;
        Assert.NotNull(Assert.Single(backup.Data.Cards).CompletedAtUtc);
        var restore = await a.PostAsJsonAsync("/api/backups/restore", new RestoreBoardRequest(Guid.NewGuid(), backup)); restore.EnsureSuccessStatusCode();
        var result = (await restore.Content.ReadFromJsonAsync<RestoreBoardResult>())!;
        var copied = (await a.GetFromJsonAsync<PagedResult<CardSummaryDto>>($"/api/boards/{result.BoardId}/cards"))!;
        Assert.True(Assert.Single(copied.Items).IsCompleted);
    }

    [SqlFact]
    public async Task DesktopPromptCancelDraftAndCompletionUpdateStatistics()
    {
        using var app = ApiFoundationTests.Factory(fixture.ConnectionString);
        var g = await new ChecklistTests(fixture).Seed(); using var a = ChecklistTests.Client(app, g.User);
        var card = await ChecklistTests.Create(a, g.Board.Id, g.Col.Id);
        using var services = new ClientServices(new Uri("http://localhost"), transport: app.Server.CreateHandler());
        var u = new UserDto(g.User.Id, g.User.Email, g.User.Name);
        services.Session.SignIn(app.Services.GetRequiredService<JwtService>().Issue(u).AccessToken, u);
        using var content = new BoardContentViewModel(services); using var insights = new BoardInsightsViewModel(services, _ => { });
        await insights.Open(g.Board.Id, "Дошка", true, [u]); content.CompletionChanged = () => insights.Refresh();
        await content.OpenAsync(g.Board.Id); await content.RequestCompletionAsync(card.Card.Id);
        Assert.True(content.CompletionPromptVisible); content.CancelCompletionCommand.Execute(null);
        Assert.False(content.CompletionPromptVisible); Assert.Equal(0, insights.Snapshot!.CompletedTotal);
        await content.OpenCardAsync(card.Card.Id); content.Title = "Незбережена чернетка";
        await content.RequestCompletionAsync(card.Card.Id); Assert.Contains("збережіть", content.Error); Assert.Equal("Незбережена чернетка", content.Title);
        content.CloseEditor(); await content.RequestCompletionAsync(card.Card.Id); await content.CompletePendingAsync(false);
        Assert.False(content.CompletionPromptVisible); Assert.Empty(content.Error);
        Assert.True(Assert.Single(Assert.Single(content.Columns).Cards).IsCompleted);
        Assert.Equal(1, insights.Snapshot!.CompletedTotal); Assert.Equal("1", insights.Metrics[0].Value);
    }
}
