using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Curs.Services;
using Curs.ViewModels;
using KanbanContracts;
using Xunit;

namespace KanbanApi.Tests;
public sealed class DesktopChatTests
{
    private sealed class Transport(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken t)=>send(r,t);}
    private static HttpResponseMessage Ok(object data)=>new(HttpStatusCode.OK){Content=JsonContent.Create(data)};
    private static readonly UserDto Self=new(Guid.NewGuid(),"self@example.test","Ви"),Peer=new(Guid.NewGuid(),"peer@example.test","Учасник");
    private static ChatDto Chat()=>new(Guid.NewGuid(),Peer,true,null,null,0);
    private static ClientServices Services(HttpMessageHandler transport){var s=new ClientServices(new Uri("http://localhost/"),transport:transport);s.Session.SignIn("token",Self);return s;}
    [Fact]
    public async Task RetryKeepsDraftAndUsesSameIdAndSuccessClearsOnlySentDraft()
    {
        var chat=Chat();var ids=new List<Guid>();var fail=true;
        using var services=Services(new Transport(async(r,_)=>
        {
            var path=r.RequestUri!.AbsolutePath;
            if(r.Method==HttpMethod.Post&&path.EndsWith("/messages"))
            {
                var body=(await r.Content!.ReadFromJsonAsync<SendChatMessageRequest>())!;ids.Add(body.ClientMessageId);
                if(fail){fail=false;throw new HttpRequestException();}return Ok(new ChatMessageDto(1,Self.Id,body.Text,DateTime.UtcNow));
            }
            if(path=="/api/chats")return Ok(new[]{chat});
            return Ok(new ChatMessagesDto([],false));
        }));using var vm=new ChatViewModel(services);await vm.Show();await vm.Open(chat);vm.Draft="Залишити текст";await vm.Send();Assert.Equal("Залишити текст",vm.Draft);Assert.Empty(vm.Messages);await vm.Send();Assert.Equal(ids[0],ids[1]);Assert.Empty(vm.Draft);Assert.Single(vm.Messages);
    }
    [Fact]
    public async Task PollDoesNotLoseIncomingMessagesBeforeOwnReplyOrOverwriteDraft()
    {
        var chat=Chat();var time=DateTime.UtcNow;var sent=false;
        using var services=Services(new Transport(async(r,_)=>
        {
            if(r.Method==HttpMethod.Post&&r.RequestUri!.AbsolutePath.EndsWith("/messages")){sent=true;return Ok(new ChatMessageDto(3,Self.Id,"Відповідь",time));}
            if(r.RequestUri!.AbsolutePath=="/api/chats")return Ok(new[]{chat});
            if(r.RequestUri.AbsolutePath.EndsWith("/read"))return new HttpResponseMessage(HttpStatusCode.NoContent);
            if(sent){Assert.Equal("?afterId=1",r.RequestUri.Query);return Ok(new ChatMessagesDto([new(2,Peer.Id,"Вхідне",time),new(3,Self.Id,"Відповідь",time)],false));}
            await Task.CompletedTask;return Ok(new ChatMessagesDto([new(1,Peer.Id,"Перше",time)],false));
        }));using var vm=new ChatViewModel(services);await vm.Show();await vm.Open(chat);vm.Draft="Відповідь";await vm.Send();vm.Draft="Чернетка";await vm.Poll();Assert.Equal(new long[]{1,2,3},vm.Messages.Select(m=>m.Message.Id));Assert.Equal("Чернетка",vm.Draft);
    }
    [Fact]
    public async Task LateReplyAfterLogoutCannotPopulateMessagesOrAnotherUsersSession()
    {
        var chat=Chat();var reached=new TaskCompletionSource();var pending=new TaskCompletionSource<HttpResponseMessage>();
        using var services=Services(new Transport((r,_)=>
        {
            if(r.RequestUri!.AbsolutePath=="/api/chats")return Task.FromResult(Ok(new[]{chat}));
            reached.TrySetResult();return pending.Task;
        }));using var vm=new ChatViewModel(services);await vm.Show();var opening=vm.Open(chat);await reached.Task;services.Auth.Logout();services.Session.SignIn("other",Peer);pending.SetResult(Ok(new ChatMessagesDto([new(1,Self.Id,"Приватний текст",DateTime.UtcNow)],false)));await opening;
        Assert.Empty(vm.Threads);Assert.Empty(vm.Messages);Assert.Empty(vm.Draft);Assert.False(vm.HasConversation);Assert.Equal("other",services.Session.AccessToken);
    }
    [Fact]
    public async Task DraftsSurviveSwitchingAndHidingButLogoutClearsEverything()
    {
        var one=Chat();var two=Chat() with{Peer=Peer with{Id=Guid.NewGuid()}};
        using var services=Services(new Transport((r,_)=>Task.FromResult(r.RequestUri!.AbsolutePath=="/api/chats"?Ok(new[]{one,two}):Ok(new ChatMessagesDto([],false)))));
        using var vm=new ChatViewModel(services);await vm.Show();await vm.Open(one);vm.Draft="Перша";await vm.Open(two);vm.Draft="Друга";await vm.Open(one);Assert.Equal("Перша",vm.Draft);vm.Active=false;await vm.Show();Assert.Equal("Перша",vm.Draft);services.Auth.Logout();Assert.Empty(vm.Draft);Assert.Empty(vm.Threads);Assert.Empty(vm.Messages);
    }
}
