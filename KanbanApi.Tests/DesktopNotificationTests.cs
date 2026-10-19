using System.Net;
using System.Net.Http.Json;
using Curs.Services;
using Curs.ViewModels;
using KanbanApi.Infrastructure;
using KanbanContracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace KanbanApi.Tests;
[Collection("sql")]
public sealed class DesktopNotificationTests(SqlFixture fixture)
{
    private static ClientServices Services(WebApplicationFactory<Program> app,KanbanApi.Domain.User user,HttpMessageHandler? transport=null)
    {
        var s=new ClientServices(new Uri("http://localhost/"),transport:transport??app.Server.CreateHandler());var u=new UserDto(user.Id,user.Email,user.Name);s.Session.SignIn(app.Services.GetRequiredService<JwtService>().Issue(u).AccessToken,u);return s;
    }
    [SqlFact]
    public async Task BellFiltersReadOpenAndLogoutKeepCorrectRecipientAndTarget()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);var g=await new NotificationTests(fixture).Pair();using var a=ChecklistTests.Client(app,g.Owner);var card=await NotificationTests.Assigned(a,g.Board.Id,g.Column.Id,g.Peer.Id);using var s=Services(app,g.Peer);NotificationDto? target=null;using var vm=new NotificationViewModel(s,n=>target=n);
        await vm.Refresh();Assert.True(vm.HasUnread);Assert.Equal("1",vm.Badge);var id=vm.Items.Single().Item.Id;await vm.Open(id);Assert.Equal(card.Card.Id,target?.CardId);Assert.False(vm.HasUnread);Assert.False(vm.Visible);
        vm.UnreadCommand.Execute(null);await Wait(()=>!vm.IsBusy);Assert.True(vm.IsEmpty);s.Session.Clear();Assert.Empty(vm.Items);Assert.Equal("0",vm.Badge);
    }
    [SqlFact]
    public async Task NotificationNavigationProtectsCardDraftAndMentionPickerUsesEmail()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);var g=await new NotificationTests(fixture).Pair();using var a=ChecklistTests.Client(app,g.Owner);var card=await NotificationTests.Assigned(a,g.Board.Id,g.Column.Id,g.Peer.Id);using var s=Services(app,g.Peer);using var vm=new BoardWorkspaceViewModel(s);
        await vm.OpenVisualAsync(g.Board.Id);await vm.Content.OpenCardAsync(card.Card.Id);vm.Content.Title="Моя чернетка";
        vm.Content.MentionPerson=vm.Content.Assignees.Single(p=>p.Id==g.Owner.Id);vm.Content.InsertMention();Assert.Contains("@"+g.Owner.Email,vm.Content.CommentText);
        await vm.Notifications.Refresh();await vm.Notifications.Open(vm.Notifications.Items.Single().Item.Id);Assert.True(vm.NavigationConfirmation);Assert.Equal("Моя чернетка",vm.Content.Title);vm.StayCommand.Execute(null);Assert.False(vm.NavigationConfirmation);Assert.Equal("Моя чернетка",vm.Content.Title);
    }
    private sealed class Delayed(HttpMessageHandler inner):DelegatingHandler(inner)
    {
        public TaskCompletionSource Reached{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<HttpResponseMessage> Reply{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken t)
        {if(r.RequestUri!.AbsolutePath=="/api/notifications"){Reached.TrySetResult();return Reply.Task;}return base.SendAsync(r,t);}
    }
    [SqlFact]
    public async Task LateNotificationResponseCannotRestorePreviousUsersPrivateData()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);var g=await new NotificationTests(fixture).Pair();var transport=new Delayed(app.Server.CreateHandler());using var s=Services(app,g.Peer,transport);using var vm=new NotificationViewModel(s,_=>throw new Exception("Must not navigate"));var refreshing=vm.Refresh();await transport.Reached.Task;s.Session.Clear();s.Session.SignIn("new-user-session",new(Guid.NewGuid(),"other@example.test","Інший"));
        transport.Reply.SetResult(new(HttpStatusCode.OK){Content=JsonContent.Create(new NotificationsDto([new(1,NotificationKind.Mention,"Приватне","Приховано",DateTime.UtcNow,false,true,g.Board.Id,Guid.NewGuid(),null)],1,1,false))});await refreshing;Assert.Empty(vm.Items);Assert.False(vm.HasUnread);Assert.False(vm.IsBusy);
    }
    private static async Task Wait(Func<bool> condition){for(var i=0;i<200&&!condition();i++)await Task.Delay(10);Assert.True(condition());}
}
