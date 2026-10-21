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
public sealed class DesktopInsightsTests(SqlFixture fixture)
{
    [SqlFact]
    public async Task ModesCompletionHistoryPagingAndLogoutUseLiveData()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);var g=await new NotificationTests(fixture).Pair();using var a=ChecklistTests.Client(app,g.Owner);var card=await ChecklistTests.Create(a,g.Board.Id,g.Column.Id);
        using var services=new ClientServices(new Uri("http://localhost"),transport:app.Server.CreateHandler());var u=new UserDto(g.Owner.Id,g.Owner.Email,g.Owner.Name);services.Session.SignIn(app.Services.GetRequiredService<JwtService>().Issue(u).AccessToken,u);ActivityDto? opened=null;
        using var vm=new BoardInsightsViewModel(services,e=>opened=e);await vm.Open(g.Board.Id,"Дошка",true,[u]);Assert.Equal(4,vm.Metrics.Count);Assert.Equal(7,vm.Days.Count);Assert.True(vm.IsBoard);Assert.True(vm.NeedsCompletion);
        vm.HistoryCommand.Execute(null);Assert.True(vm.IsHistory);await vm.History.Load();var row=Assert.Single(vm.History.Events);row.OpenCommand.Execute(null);Assert.Equal(card.Card.Id,opened!.CardId);
        vm.FullOverviewCommand.Execute(null);Assert.True(vm.IsOverview);Assert.False(vm.Drawer);await vm.Refresh();vm.Completion=vm.Columns.Single(c=>c.Id==g.Column.Id);await vm.SaveCompletion();Assert.False(vm.NeedsCompletion);Assert.Equal(g.Column.Id,vm.Snapshot!.CompletionColumnId);
        vm.ToggleDrawerCommand.Execute(null);Assert.True(vm.Drawer);await vm.Refresh();Assert.Equal(4,vm.Metrics.Count);Assert.Equal(vm.Snapshot.Completed,vm.Days.Sum(d=>d.Count));
        services.Session.Clear();Assert.Empty(vm.Metrics);Assert.Empty(vm.Workload);Assert.Empty(vm.Recent);Assert.Empty(vm.History.Events);Assert.Null(vm.Snapshot);Assert.False(vm.Drawer);
    }
    private sealed class Delayed(HttpMessageHandler inner):DelegatingHandler(inner)
    {
        public TaskCompletionSource Arrived{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<HttpResponseMessage> Reply{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {if(request.RequestUri!.AbsolutePath.EndsWith("/history",StringComparison.Ordinal)){Arrived.TrySetResult();return Reply.Task;}return base.SendAsync(request,token);}
    }
    [SqlFact]
    public async Task LatePrivateHistoryCannotReappearAfterLogout()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);var g=await new NotificationTests(fixture).Pair();var transport=new Delayed(app.Server.CreateHandler());using var services=new ClientServices(new Uri("http://localhost"),transport:transport);var u=new UserDto(g.Owner.Id,g.Owner.Email,g.Owner.Name);services.Session.SignIn(app.Services.GetRequiredService<JwtService>().Issue(u).AccessToken,u);
        using var history=new HistoryViewModel(services,_=>{});var loading=history.Open(g.Board.Id,[u]);await transport.Arrived.Task;services.Session.Clear();transport.Reply.SetResult(new(HttpStatusCode.OK){Content=JsonContent.Create(new PagedResult<ActivityDto>([new(1,g.Board.Id,Guid.NewGuid(),"Приватне",u,"priority","Low","High",DateTime.UtcNow,true)],1,0,50))});await loading;Assert.Empty(history.Events);Assert.Empty(history.People);
    }
}
