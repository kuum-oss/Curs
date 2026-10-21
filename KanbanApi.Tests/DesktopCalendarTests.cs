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
public sealed class DesktopCalendarTests(SqlFixture fixture)
{
    [SqlFact]
    public async Task MonthWeekFilterMoveAndOpenUseCorrectDatesAndTarget()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);var g=await new NotificationTests(fixture).Pair();using var a=ChecklistTests.Client(app,g.Owner);var card=await ChecklistTests.Create(a,g.Board.Id,g.Column.Id);using var services=new ClientServices(new Uri("http://localhost"),transport:app.Server.CreateHandler());var u=new UserDto(g.Owner.Id,g.Owner.Email,g.Owner.Name);services.Session.SignIn(app.Services.GetRequiredService<JwtService>().Issue(u).AccessToken,u);Guid? opened=null;using var vm=new CalendarViewModel(services,(_,id)=>opened=id);await vm.Show();await vm.SetAnchor(new(2026,10,21));Assert.Equal(new DateOnly(2026,9,28),vm.Start);Assert.Equal(42,vm.Days.Count);var row=Assert.Single(vm.Undated);await vm.Move(row.Row,new(2026,10,23));Assert.Empty(vm.Error);Assert.Empty(vm.Undated);var day=vm.Days.Single(d=>d.Date==new DateOnly(2026,10,23));var moved=Assert.Single(day.Tasks);Assert.Equal(card.Card.Id,moved.Row.Id);vm.Open(moved.Row);Assert.Equal(card.Card.Id,opened);await vm.SetMode(true);Assert.Equal(new DateOnly(2026,10,19),vm.Start);Assert.Equal(7,vm.Days.Count);await vm.Navigate(1);Assert.Equal(new DateOnly(2026,10,26),vm.Start);Assert.All(vm.Days,d=>Assert.Empty(d.Tasks));services.Session.Clear();Assert.Empty(vm.Days);Assert.Empty(vm.Undated);Assert.Empty(vm.Assignees);
    }
    private sealed class Delayed(HttpMessageHandler inner):DelegatingHandler(inner)
    {
        public bool Delay{get;set;}
        public TaskCompletionSource Reached{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<HttpResponseMessage> Reply{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken t)
        {if(Delay&&request.RequestUri!.AbsolutePath=="/api/me/calendar"){Reached.TrySetResult();return Reply.Task;}return base.SendAsync(request,t);}
    }
    [SqlFact]
    public async Task LateCalendarResponseCannotRestoreCardsAfterLogout()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);var g=await new NotificationTests(fixture).Pair();var transport=new Delayed(app.Server.CreateHandler());using var services=new ClientServices(new Uri("http://localhost"),transport:transport);var u=new UserDto(g.Owner.Id,g.Owner.Email,g.Owner.Name);services.Session.SignIn(app.Services.GetRequiredService<JwtService>().Issue(u).AccessToken,u);using var vm=new CalendarViewModel(services,(_,_)=>{});await vm.Show();transport.Delay=true;var loading=vm.Refresh();await transport.Reached.Task;services.Session.Clear();transport.Reply.SetResult(new(HttpStatusCode.OK){Content=JsonContent.Create(new PagedResult<CalendarCardDto>([new(Guid.NewGuid(),g.Board.Id,"Приватне","Дошка","План",null,CardPriority.High,null,"v","v")],1,0,500))});await loading;Assert.Empty(vm.Days);Assert.Empty(vm.Undated);Assert.False(vm.Active);
    }
    [Theory]
    [InlineData(2027,1,1,2026,12,28)]
    [InlineData(2028,2,29,2028,2,28)]
    [InlineData(2026,11,1,2026,10,26)]
    public void WeeksStartOnMondayAcrossYearsAndLeapDates(int y,int m,int d,int ey,int em,int ed)=>Assert.Equal(new DateOnly(ey,em,ed),CalendarViewModel.Monday(new(y,m,d)));
}
