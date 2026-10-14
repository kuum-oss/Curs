using System.Net;
using System.Net.Http.Json;
using Curs.Services;
using Curs.ViewModels;
using KanbanContracts;
using Xunit;

namespace KanbanApi.Tests;

public sealed class DesktopFilterImageTests
{
    private sealed class Transport(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>send(request,token);}
    private static HttpResponseMessage Response(object value)=>new(HttpStatusCode.OK){Content=JsonContent.Create(value)};
    private sealed class Harness:IDisposable
    {
        public Guid Board=Guid.NewGuid(),Column=Guid.NewGuid(),Tag=Guid.NewGuid(),User=Guid.NewGuid();public ClientServices Services;public BoardContentViewModel View;
        public List<string> Queries=[];public bool HoldOld;public bool OldCancelled;public DateOnly Today=new(2026,10,14);
        public TaskCompletionSource OldReached=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Harness()
        {
            var v=Convert.ToBase64String(new byte[8]);Services=new(new Uri("http://localhost/"),transport:new Transport(async (r,t)=>
            {
                var path=r.RequestUri!.AbsolutePath;object body;
                if(path.EndsWith("/cards"))
                {
                    Queries.Add(r.RequestUri.Query);
                    if(HoldOld&&r.RequestUri.Query.Contains("search=old"))
                    {OldReached.TrySetResult();try{await Task.Delay(Timeout.Infinite,t);}catch(OperationCanceledException){OldCancelled=true;throw;}}
                    var text=r.RequestUri.Query.Contains("search=new")?"new":"all";
                    body=new PagedResult<CardSummaryDto>([new(Guid.NewGuid(),Column,text,null,CardPriority.High,null,7,[],false,v)],1,0,100,v);
                }
                else if(path.EndsWith("/columns"))body=new[]{new ColumnDto(Column,Board,"Колонка",0,v)};
                else if(path.EndsWith("/tags"))body=new[]{new TagDto(Tag,"Тег","#64748B")};
                else if(path.EndsWith("/members"))body=new[]{new MemberDto(new(User,"u@example.test","Учасник"),BoardRole.Member,true,true)};
                else body=new BoardDetailsDto(Board,"Дошка",null,BoardRole.Admin,true,true,v);
                return Response(body);
            }));Services.Session.SignIn("token",new(User,"u@example.test","Учасник"));View=new(Services,()=>Today);
        }
        public void Dispose(){View.Dispose();Services.Dispose();}
    }
    [Fact]
    public async Task FiltersCombineDebounceResetAndRestrictOnlyCardOrdering()
    {
        using var h=new Harness();await h.View.OpenAsync(h.Board);h.Queries.Clear();
        h.View.Search="завдання %";h.View.FilterPriority=h.View.PriorityFilters.Single(p=>p.Value==CardPriority.High);h.View.FilterTag=h.View.TagFilters.Single(t=>t.Id==h.Tag);h.View.FilterAssignee=h.View.AssigneeFilters.Single(a=>a.Id==h.User);h.View.FilterDeadline=h.View.DeadlineFilters.Single(d=>d.Value==DeadlineFilter.Today);
        Assert.False(h.View.CanDragCards);await h.View.Filtering;var q=Assert.Single(h.Queries);Assert.Contains("search="+Uri.EscapeDataString("завдання %"),q);Assert.Contains("priority=High",q);Assert.Contains("tagId="+h.Tag,q);Assert.Contains("assigneeId="+h.User,q);Assert.Contains("deadline=Today&today=2026-10-14",q);Assert.True(h.View.CanDragColumns);Assert.False(h.View.CanDragCards);Assert.Equal(7,h.View.Columns[0].Cards[0].Card.Position);
        h.View.ResetFilters();await h.View.Filtering;Assert.DoesNotContain("search=",h.Queries.Last());Assert.DoesNotContain("priority=",h.Queries.Last());Assert.True(h.View.CanDragCards);
    }
    [Fact]
    public async Task NewSearchCancelsOldRequestAndOldReplyCannotPublish()
    {
        using var h=new Harness();await h.View.OpenAsync(h.Board);h.HoldOld=true;h.View.Search="old";await h.OldReached.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.True(h.View.CanFilter);h.View.Search="new";await h.View.Filtering;Assert.True(h.OldCancelled);Assert.Equal("new",h.View.Columns[0].Cards[0].Title);Assert.False(h.View.HasError);
        h.View.Search="pending";h.Services.Session.Clear();await h.View.Filtering;Assert.Empty(h.View.Columns);Assert.False(h.View.IsVisible);
    }
    [Fact]
    public async Task DeadlineRefreshesAfterLocalMidnightEvenIfBoardVersionIsUnchanged()
    {
        using var h=new Harness();await h.View.OpenAsync(h.Board);h.View.FilterDeadline=h.View.DeadlineFilters.Single(d=>d.Value==DeadlineFilter.Today);await h.View.Filtering;var reads=h.Queries.Count;h.Today=h.Today.AddDays(1);await h.View.PollAsync();Assert.Equal(reads+1,h.Queries.Count);Assert.Contains("today=2026-10-15",h.Queries.Last());
    }
    [Fact]
    public async Task ThumbnailCacheIsVersionedAndIsolatedBetweenSessions()
    {
        var count=0;using var services=new ClientServices(new Uri("http://localhost/"),transport:new Transport((r,t)=>{Interlocked.Increment(ref count);Assert.NotNull(r.Headers.Authorization);return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent([1,2,3])});}));services.Session.SignIn("one",new(Guid.NewGuid(),"a@example.test","A"));var card=Guid.NewGuid();await services.Images.Read(card,"v1",true,default);await services.Images.Read(card,"v1",true,default);Assert.Equal(1,count);await services.Images.Read(card,"v2",true,default);Assert.Equal(2,count);Assert.Equal(2,services.Images.CachedCount);
        services.Session.Clear();Assert.Equal(0,services.Images.CachedCount);await Assert.ThrowsAsync<OperationCanceledException>(()=>services.Images.Read(card,"v2",true,default));services.Session.SignIn("two",new(Guid.NewGuid(),"b@example.test","B"));await services.Images.Read(card,"v2",true,default);Assert.Equal(3,count);
    }
    [Fact]
    public async Task LateImageReplyAfterLogoutCannotFillCacheAndDownloadsAreBounded()
    {
        var pending=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var reached=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var active=0;var peak=0;
        using var services=new ClientServices(new Uri("http://localhost/"),transport:new Transport(async (r,t)=>{var n=Interlocked.Increment(ref active);peak=Math.Max(peak,n);if(n==4)reached.TrySetResult();await pending.Task;Interlocked.Decrement(ref active);return new(HttpStatusCode.OK){Content=new ByteArrayContent([1])};}));services.Session.SignIn("one",new(Guid.NewGuid(),"a@example.test","A"));var reads=Enumerable.Range(0,9).Select(_=>services.Images.Read(Guid.NewGuid(),"v",true,default)).ToArray();await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal(4,peak);services.Session.Clear();pending.SetResult();foreach(var task in reads)await Assert.ThrowsAsync<OperationCanceledException>(()=>task);Assert.Equal(0,services.Images.CachedCount);Assert.Equal(4,peak);
    }
}
