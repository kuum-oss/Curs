using System.Net;
using System.Net.Http.Json;
using Curs.Services;
using Curs.ViewModels;
using KanbanApi.Domain;
using KanbanApi.Infrastructure;
using KanbanContracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KanbanApi.Tests;

[Collection("sql")]
public sealed class DesktopOrderingTests(SqlFixture fixture)
{
    [SqlFact]
    public async Task TwoClientsPollMoveReorderProtectDraftAndResolveConflict()
    {
        var email=Guid.NewGuid().ToString("N")+"@example.test";var user=new User{Email=email,NormalizedEmail=email.ToUpperInvariant(),Name="Учасник",PasswordHash="unused"};
        await using(var db=fixture.CreateContext()){db.Users.Add(user);await db.SaveChangesAsync();}
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);using var bootstrap=app.CreateClient();
        ClientServices Services(){var s=new ClientServices(new Uri("http://localhost/"),transport:app.Server.CreateHandler());s.Session.SignIn(app.Services.GetRequiredService<JwtService>().Issue(new(user.Id,user.Email,user.Name)).AccessToken,new(user.Id,user.Email,user.Name));return s;}
        using var one=Services();using var two=Services();using var a=new BoardContentViewModel(one);using var b=new BoardContentViewModel(two);
        var board=await one.Boards.CreateAsync(new(){Name="Спільна"},default);await a.OpenAsync(board.Id);a.NewColumnName="А";await a.CreateColumnAsync();a.NewColumnName="Б";await a.CreateColumnAsync();var first=a.Columns[0].Column.Id;var second=a.Columns[1].Column.Id;
        a.NewCard(a.Columns[0]);a.Title="Картка";await a.SaveCardAsync();var id=a.Columns[0].Cards[0].Card.Id;a.CloseEditor();await b.OpenAsync(board.Id);
        await a.MoveCardAsync(id,second);Assert.Empty(a.Error);await b.PollAsync();Assert.Empty(b.Columns[0].Cards);Assert.Equal(id,b.Columns[1].Cards[0].Card.Id);
        await a.ReorderColumnAsync(second,first);await b.PollAsync();Assert.Equal(new[]{second,first},b.Columns.Select(x=>x.Column.Id));
        await b.OpenCardAsync(id);b.Title="Незбережена чернетка";b.Description="Мій опис";await a.OpenCardAsync(id);a.Title="Чужа зміна";await a.SaveCardAsync();a.CloseEditor();
        var oldCard=b.Columns.SelectMany(x=>x.Cards).Single(x=>x.Card.Id==id).Card.Title;await b.PollAsync();Assert.True(b.RemoteChangesPending);Assert.True(b.CanEdit);Assert.Equal("Незбережена чернетка",b.Title);Assert.Equal("Мій опис",b.Description);Assert.Equal(oldCard,b.Columns.SelectMany(x=>x.Cards).Single(x=>x.Card.Id==id).Card.Title);
        await b.SaveCardAsync();Assert.True(b.HasError);Assert.Equal("Незбережена чернетка",b.Title);Assert.Equal("Чужа зміна",(await one.Content.Card(id,default)).Card.Title);
        await b.SaveCardAsync();Assert.Empty(b.Error);b.CloseEditor();await a.PollAsync();Assert.Equal("Незбережена чернетка",a.Columns.SelectMany(x=>x.Cards).Single(x=>x.Card.Id==id).Title);
        Assert.True(b.BeginDrag(id,true));await a.MoveCardAsync(id,first);await b.PollAsync();Assert.Equal(id,b.Columns[0].Cards[0].Card.Id);b.EndDrag();await b.PollAsync();Assert.Equal(id,b.Columns[1].Cards[0].Card.Id);
    }
    [SqlFact]
    public async Task PollDetectsRevocationEvenWithAnOpenDraft()
    {
        User NewUser(){var email=Guid.NewGuid().ToString("N")+"@example.test";return new(){Email=email,NormalizedEmail=email.ToUpperInvariant(),Name="Тест",PasswordHash="unused"};}
        var owner=NewUser();var person=NewUser();await using(var db=fixture.CreateContext()){db.Users.AddRange(owner,person);await db.SaveChangesAsync();}
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);using var bootstrap=app.CreateClient();
        ClientServices Services(User user){var s=new ClientServices(new Uri("http://localhost/"),transport:app.Server.CreateHandler());s.Session.SignIn(app.Services.GetRequiredService<JwtService>().Issue(new(user.Id,user.Email,user.Name)).AccessToken,new(user.Id,user.Email,user.Name));return s;}
        using var admin=Services(owner);using var member=Services(person);var board=await admin.Boards.CreateAsync(new(){Name="Доступ"},default);
        await admin.Boards.AddMemberAsync(board.Id,new(){Email=person.Email,ExpectedBoardVersion=board.Version},default);var version=(await admin.Boards.GetAsync(board.Id,default)).Version;await admin.Content.CreateColumn(board.Id,new(){Name="Колонка",ExpectedBoardVersion=version},default);
        using var vm=new BoardContentViewModel(member);await vm.OpenAsync(board.Id);vm.NewCard(vm.Columns[0]);vm.Title="Чернетка";
        await admin.Boards.PermissionsAsync(board.Id,person.Id,new(){CanCreateCards=false,CanDeleteCards=false,ExpectedBoardVersion=(await admin.Boards.GetAsync(board.Id,default)).Version},default);
        await vm.PollAsync();Assert.False(vm.CanCreate);Assert.False(vm.Columns[0].CanCreate);Assert.False(vm.Columns[0].AddCommand.CanExecute(null));Assert.Equal("Чернетка",vm.Title);
        await admin.Boards.RemoveMemberAsync(board.Id,person.Id,(await admin.Boards.GetAsync(board.Id,default)).Version,default);await vm.PollAsync();Assert.False(vm.IsVisible);Assert.False(vm.EditorVisible);Assert.Empty(vm.Title);Assert.Empty(vm.Columns);Assert.NotNull(member.Session.AccessToken);
    }
}
public sealed class DesktopOrderingUnitTests
{
    private sealed class Transport(Func<HttpRequestMessage,Task<HttpResponseMessage>> send):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>send(request);}
    private static HttpResponseMessage Response(object body,HttpStatusCode status=HttpStatusCode.OK)=>new(status){Content=JsonContent.Create(body)};
    private sealed class Harness:IDisposable
    {
        public Guid Board=Guid.NewGuid(),One=Guid.NewGuid(),Two=Guid.NewGuid(),Card=Guid.NewGuid();public int Reads;public ClientServices Services;public BoardContentViewModel View;
        public bool HoldMetadata, Incomplete;
        public TaskCompletionSource<HttpResponseMessage> Finish=new(TaskCreationOptions.RunContinuationsAsynchronously);public TaskCompletionSource Reached=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Harness()
        {
            var version=Convert.ToBase64String(new byte[8]);
            Services=new(new Uri("http://localhost/"),transport:new Transport(request=>
            {
                if(request.Method==HttpMethod.Post || request.Method==HttpMethod.Put){Reached.TrySetResult();return Finish.Task;}
                Reads++;var path=request.RequestUri!.AbsolutePath;object body=path.EndsWith("columns")?new List<ColumnDto>{new(One,Board,"А",0,version),new(Two,Board,"Б",1,version)}:
                    path.EndsWith("cards")?new PagedResult<CardSummaryDto>([new(Card,One,"Картка",null,CardPriority.Medium,null,0,[],false,version)],1,0,100,version):
                    path.EndsWith("members")?new List<MemberDto>():path.EndsWith("tags")?new List<TagDto>():new BoardDetailsDto(Board,"Дошка",null,BoardRole.Admin,true,true,version);
                if (HoldMetadata && path == $"/api/boards/{Board}") { Reached.TrySetResult(); return Finish.Task; }
                if (Incomplete && path.EndsWith("cards")) body = new PagedResult<CardSummaryDto>([],1,0,100,version);
                return Task.FromResult(Response(body));
            }));Services.Session.SignIn("test-token",new(Guid.NewGuid(),"test@example.test","Тест"));View=new(Services);
        }
        public void Dispose(){View.Dispose();Services.Dispose();}
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OptimisticMoveAppearsBeforeReplyAndFailureRestoresExactOrder(bool offline)
    {
        using var h=new Harness();await h.View.OpenAsync(h.Board);var move=h.View.MoveCardAsync(h.Card,h.Two);await h.Reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(h.View.Columns[0].Cards);Assert.Equal(h.Card,h.View.Columns[1].Cards[0].Card.Id);Assert.True(h.View.LastReorderFeedbackMilliseconds<100);
        var reads=h.Reads;await h.View.PollAsync();await h.View.PollAsync();Assert.Equal(reads,h.Reads);
        if (offline) h.Finish.SetException(new HttpRequestException("offline"));
        else h.Finish.SetResult(Response(new ApiProblem{Code="internal_error",Title="Помилка"},HttpStatusCode.InternalServerError));
        await move;
        Assert.Equal(h.Card,h.View.Columns[0].Cards[0].Card.Id);Assert.Empty(h.View.Columns[1].Cards);Assert.True(h.View.HasError);
    }
    [Fact]
    public async Task OptimisticColumnFailureRestoresPermutationAndFilteredCardsCannotDrag()
    {
        using var h=new Harness();await h.View.OpenAsync(h.Board);h.View.CardsFiltered=true;Assert.False(h.View.CanDragCards);Assert.True(h.View.CanDragColumns);
        Assert.False(h.View.BeginDrag(h.Card,true));var task=h.View.ReorderColumnAsync(h.Two,h.One);await h.Reached.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal(new[]{h.Two,h.One},h.View.Columns.Select(c=>c.Column.Id));
        h.Finish.SetResult(Response(new ApiProblem{Code="internal_error",Title="Помилка"},HttpStatusCode.InternalServerError));await task;Assert.Equal(new[]{h.One,h.Two},h.View.Columns.Select(c=>c.Column.Id));
    }
    [Fact]
    public async Task DragSuspendsPollingAndLogoutDoesNotRestoreOptimisticCache()
    {
        using var h=new Harness();await h.View.OpenAsync(h.Board);Assert.True(h.View.BeginDrag(h.Card,true));var reads=h.Reads;await h.View.PollAsync();Assert.Equal(reads,h.Reads);h.View.EndDrag();
        var task=h.View.MoveCardAsync(h.Card,h.Two);await h.Reached.Task.WaitAsync(TimeSpan.FromSeconds(5));h.Services.Session.Clear();h.Finish.SetResult(Response(new MoveResultDto(h.Card,h.Two,"version","version")));await task;Assert.Empty(h.View.Columns);Assert.False(h.View.IsVisible);
    }
    [Fact]
    public async Task UnchangedPollReadsOnlyMetadataAndKeepsRowIdentity()
    {
        using var h=new Harness();await h.View.OpenAsync(h.Board);var col=h.View.Columns[0];var reads=h.Reads;await h.View.PollAsync();Assert.Same(col,h.View.Columns[0]);Assert.Equal(reads+1,h.Reads);Assert.False(h.View.IsPolling);
    }
    [Fact]
    public async Task SlowPollNeverOverlapsOrDisablesDraftFields()
    {
        using var h=new Harness();await h.View.OpenAsync(h.Board);h.HoldMetadata=true;var reads=h.Reads;var task=h.View.PollAsync();await h.Reached.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.True(h.View.IsPolling);Assert.True(h.View.CanEdit);await h.View.PollAsync();await h.View.PollAsync();Assert.Equal(reads+1,h.Reads);
        h.Finish.SetResult(Response(new BoardDetailsDto(h.Board,"Дошка",null,BoardRole.Admin,true,true,Convert.ToBase64String(new byte[8]))));await task;Assert.False(h.View.IsPolling);
    }
    [Fact]
    public async Task IncompleteSnapshotNeverEnablesDragging()
    {
        using var h=new Harness{Incomplete=true};await h.View.OpenAsync(h.Board);Assert.True(h.View.HasError);Assert.False(h.View.CanDragCards);Assert.False(h.View.CanDragColumns);Assert.False(h.View.IsComplete);Assert.Empty(h.View.Columns);
    }
}
