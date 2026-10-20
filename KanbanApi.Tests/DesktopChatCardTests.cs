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
public sealed class DesktopChatCardTests(SqlFixture fixture)
{
    private static ClientServices Services(WebApplicationFactory<Program> app,KanbanApi.Domain.User user,HttpMessageHandler? transport=null)
    {var s=new ClientServices(new Uri("http://localhost/"),transport:transport??app.Server.CreateHandler());var dto=new UserDto(user.Id,user.Email,user.Name);s.Session.SignIn(app.Services.GetRequiredService<JwtService>().Issue(dto).AccessToken,dto);return s;}
    [SqlFact]
    public async Task SharePickerSearchSendsLinkPreservesDraftAndResolvesProtectedTarget()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);var g=await new NotificationTests(fixture).Pair();using var a=ChecklistTests.Client(app,g.Owner);var card=await ChecklistTests.Create(a,g.Board.Id,g.Column.Id);using var s=Services(app,g.Owner);Guid? opened=null;using var vm=new ChatViewModel(s,(_,id)=>opened=id);await vm.Show(new(g.Peer.Id,g.Peer.Email,g.Peer.Name));vm.Draft="Моя незавершена відповідь";
        await vm.ShowShare();Assert.True(vm.ShareVisible);Assert.Single(vm.ShareCards);vm.ShareSearch="неіснуюче";Assert.True(vm.NoShareCards);vm.ShareSearch="Карт";vm.SelectedShareCard=vm.VisibleShareCards.Single();await vm.SendShared();Assert.False(vm.ShareVisible);Assert.Empty(vm.Error);Assert.Equal("Моя незавершена відповідь",vm.Draft);var message=Assert.Single(vm.Messages);Assert.True(message.HasSharedCard);Assert.Equal(card.Card.Title,message.SharedTitle);await vm.OpenShared(message.Message.Id);Assert.Equal(card.Card.Id,opened);
    }
    private sealed class LostReply(HttpMessageHandler inner):DelegatingHandler(inner)
    {
        private bool lost;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken t)
        {
            var response=await base.SendAsync(r,t);
            if(r.Method==HttpMethod.Post&&r.RequestUri!.AbsolutePath.EndsWith("/messages")&&!lost){lost=true;response.Dispose();throw new HttpRequestException("Lost after commit");}return response;
        }
    }
    [SqlFact]
    public async Task LostShareReplyKeepsSelectionAndRetryCreatesOneMessageAndNotification()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);var g=await new NotificationTests(fixture).Pair();using var a=ChecklistTests.Client(app,g.Owner);using var b=ChecklistTests.Client(app,g.Peer);await ChecklistTests.Create(a,g.Board.Id,g.Column.Id);using var s=Services(app,g.Owner,new LostReply(app.Server.CreateHandler()));using var vm=new ChatViewModel(s);await vm.Show(new(g.Peer.Id,g.Peer.Email,g.Peer.Name));await vm.ShowShare();vm.SelectedShareCard=vm.ShareCards.Single();await vm.SendShared();Assert.True(vm.ShareVisible);Assert.NotEmpty(vm.Error);Assert.NotNull(vm.SelectedShareCard);await vm.SendShared();Assert.Empty(vm.Error);Assert.Single(vm.Messages);Assert.Single((await NotificationTests.List(b)).Items);
    }
    private sealed class Delayed(HttpMessageHandler inner):DelegatingHandler(inner)
    {
        public TaskCompletionSource Reached{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<HttpResponseMessage> Reply{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken t)
        {if(r.Method==HttpMethod.Post&&r.RequestUri!.AbsolutePath.EndsWith("/messages")){Reached.TrySetResult();return Reply.Task;}return base.SendAsync(r,t);}
    }
    [SqlFact]
    public async Task LogoutDuringShareClearsPickerAndLateResponseCannotRestorePrivateCard()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);var g=await new NotificationTests(fixture).Pair();using var a=ChecklistTests.Client(app,g.Owner);var card=await ChecklistTests.Create(a,g.Board.Id,g.Column.Id);var transport=new Delayed(app.Server.CreateHandler());using var s=Services(app,g.Owner,transport);using var vm=new ChatViewModel(s);await vm.Show(new(g.Peer.Id,g.Peer.Email,g.Peer.Name));await vm.ShowShare();vm.SelectedShareCard=vm.ShareCards.Single();vm.Draft="Чернетка";var sharing=vm.SendShared();await transport.Reached.Task;s.Session.Clear();transport.Reply.SetResult(new(HttpStatusCode.OK){Content=JsonContent.Create(new ChatMessageDto(1,g.Owner.Id,"Картка",DateTime.UtcNow,new(card.Card.Id,g.Board.Id,"Приватне","Приховано",true)))});await sharing;Assert.False(vm.ShareVisible);Assert.Empty(vm.ShareCards);Assert.Empty(vm.ShareBoards);Assert.Empty(vm.Messages);Assert.Empty(vm.Draft);
    }
}
