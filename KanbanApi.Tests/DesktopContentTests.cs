using System.Net.Http.Json;
using Curs.Services;
using Curs.ViewModels;
using KanbanApi.Domain;
using KanbanApi.Infrastructure;
using KanbanContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KanbanApi.Tests;

[Collection("sql")]
public sealed class DesktopContentTests(SqlFixture fixture)
{
    private async Task<User> User()
    {
        var email=Guid.NewGuid().ToString("N")+"@example.test";var user=new User{Email=email,NormalizedEmail=email.ToUpperInvariant(),Name="Учасник",PasswordHash="unused"};
        await using var db=fixture.CreateContext();db.Users.Add(user);await db.SaveChangesAsync();return user;
    }
    [SqlFact]
    public async Task DesktopContentCreatesEditsReopensCommentsAndConfirmsDeletion()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);using var bootstrap=app.CreateClient();var user=await User();
        using var services=new ClientServices(new Uri("http://localhost/"),transport:app.Server.CreateHandler());services.Session.SignIn(app.Services.GetRequiredService<JwtService>().Issue(new(user.Id,user.Email,user.Name)).AccessToken,new(user.Id,user.Email,user.Name));
        using var workspace=new BoardWorkspaceViewModel(services);workspace.NewName="Дошка";await workspace.CreateAsync();var board=workspace.SelectedBoard!.Id;var vm=workspace.Content;
        await vm.OpenAsync(board);Assert.True(vm.IsVisible);vm.NewColumnName="Початок";await vm.CreateColumnAsync();Assert.Single(vm.Columns);Assert.Empty(vm.Error);
        vm.SelectColumn(vm.Columns[0]);vm.ColumnName="В роботі";await vm.RenameColumnAsync();Assert.Equal("В роботі",vm.Columns[0].Name);
        vm.NewCard(vm.Columns[0]);vm.Title="Курсова";vm.Description="Детальний опис";vm.DueDate="2027-03-04";vm.Priority=CardPriority.Critical;vm.Assignee=vm.Assignees.Single(a=>a.Id==user.Id);
        vm.TagName="Розробка";await vm.CreateTagAsync();Assert.True(vm.Tags[0].Selected);await vm.SaveCardAsync();Assert.Empty(vm.Error);Assert.True(vm.ExistingCard);var id=vm.Columns[0].Cards[0].Card.Id;
        vm.CommentText="Коментар з авторами";await vm.AddCommentAsync();Assert.Single(vm.Comments);Assert.Contains(user.Name,vm.Comments[0].Heading);
        vm.CloseEditor();await vm.OpenCardAsync(id);Assert.Equal("Детальний опис",vm.Description);Assert.Equal("2027-03-04",vm.DueDate);Assert.Equal(CardPriority.Critical,vm.Priority);Assert.Equal(user.Id,vm.Assignee!.Id);Assert.True(vm.Tags[0].Selected);Assert.Single(vm.Comments);
        vm.DueDate="31/31/2027";await vm.SaveCardAsync();Assert.Contains("РРРР-ММ-ДД",vm.Error);vm.DueDate="";vm.Title="Редаговано";await vm.SaveCardAsync();Assert.Empty(vm.Error);Assert.Null(vm.Columns[0].Cards[0].Card.DueDate);
        vm.DeleteCardCommand.Execute(null);Assert.True(vm.ShowConfirmation);vm.CancelConfirmation();Assert.Single(vm.Columns[0].Cards);vm.DeleteCardCommand.Execute(null);await vm.ConfirmAsync();Assert.Empty(vm.Columns[0].Cards);Assert.False(vm.EditorVisible);
        vm.SelectColumn(vm.Columns[0]);vm.DeleteColumnCommand.Execute(null);vm.CancelConfirmation();Assert.Single(vm.Columns);vm.DeleteColumnCommand.Execute(null);await vm.ConfirmAsync();Assert.Empty(vm.Columns);
        services.Session.Clear();Assert.False(vm.IsVisible);Assert.Empty(vm.Tags);Assert.Empty(vm.Comments);Assert.Empty(vm.Title);
    }
    [SqlFact]
    public async Task ConflictKeepsDraftAndRevocationClearsContent()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);using var bootstrap=app.CreateClient();var user=await User();var owner=await User();
        using var admin=new ClientServices(new Uri("http://localhost/"),transport:app.Server.CreateHandler());admin.Session.SignIn(app.Services.GetRequiredService<JwtService>().Issue(new(owner.Id,owner.Email,owner.Name)).AccessToken,new(owner.Id,owner.Email,owner.Name));
        var board=await admin.Boards.CreateAsync(new(){Name="Спільна"},default);await admin.Boards.AddMemberAsync(board.Id,new(){Email=user.Email,ExpectedBoardVersion=board.Version},default);
        using var member=new ClientServices(new Uri("http://localhost/"),transport:app.Server.CreateHandler());member.Session.SignIn(app.Services.GetRequiredService<JwtService>().Issue(new(user.Id,user.Email,user.Name)).AccessToken,new(user.Id,user.Email,user.Name));
        using var vm=new BoardContentViewModel(member);await vm.OpenAsync(board.Id);vm.NewColumnName="Колонка";await vm.CreateColumnAsync();vm.NewCard(vm.Columns[0]);vm.Title="Початкова";await vm.SaveCardAsync();var id=vm.Columns[0].Cards[0].Card.Id;var original=await admin.Content.Card(id,default);
        vm.Title="Моя чернетка";await admin.Content.UpdateCard(id,new(){Title="Чужа зміна",ExpectedBoardVersion=(await admin.Boards.GetAsync(board.Id,default)).Version,ExpectedVersion=original.Card.Version},default);
        await vm.SaveCardAsync();Assert.True(vm.HasError);Assert.Equal("Моя чернетка",vm.Title);Assert.Equal("Чужа зміна",(await admin.Content.Card(id,default)).Card.Title);
        await vm.SaveCardAsync();Assert.Empty(vm.Error);Assert.Equal("Моя чернетка",(await admin.Content.Card(id,default)).Card.Title);
        await admin.Boards.PermissionsAsync(board.Id,user.Id,new(){CanCreateCards=false,CanDeleteCards=false,ExpectedBoardVersion=(await admin.Boards.GetAsync(board.Id,default)).Version},default);await vm.RefreshAsync();Assert.False(vm.CanCreate);Assert.False(vm.CanDeleteCard);Assert.False(vm.CanDeleteColumn);Assert.False(vm.Columns[0].AddCommand.CanExecute(null));
        await admin.Boards.RemoveMemberAsync(board.Id,user.Id,(await admin.Boards.GetAsync(board.Id,default)).Version,default);await vm.RefreshAsync();Assert.True(vm.HasError);Assert.False(vm.IsVisible);Assert.False(vm.EditorVisible);Assert.Empty(vm.Columns);Assert.Empty(vm.Comments);Assert.Empty(vm.Title);Assert.NotNull(member.Session.AccessToken);
    }
    [SqlFact]
    public async Task DesktopLoadsEveryCardAndCommentPage()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);using var bootstrap=app.CreateClient();var user=await User();
        using var services=new ClientServices(new Uri("http://localhost/"),transport:app.Server.CreateHandler());services.Session.SignIn(app.Services.GetRequiredService<JwtService>().Issue(new(user.Id,user.Email,user.Name)).AccessToken,new(user.Id,user.Email,user.Name));
        var board=await services.Boards.CreateAsync(new(){Name="Пагінація"},default);var col=await services.Content.CreateColumn(board.Id,new(){Name="Колонка",ExpectedBoardVersion=board.Version},default);
        await using(var db=fixture.CreateContext())
        {
            var cards=Enumerable.Range(0,101).Select(i=>new Card{ColumnId=col!.Id,CreatedById=user.Id,Title="Картка "+i,Position=i}).ToList();db.Cards.AddRange(cards);
            db.Comments.AddRange(Enumerable.Range(0,101).Select(i=>new Comment{CardId=cards[0].Id,AuthorId=user.Id,Text="Коментар "+i}));await db.SaveChangesAsync();
        }
        using var vm=new BoardContentViewModel(services);await vm.OpenAsync(board.Id);Assert.Empty(vm.Error);Assert.Equal(101,vm.Columns[0].Cards.Count);
        await vm.OpenCardAsync(vm.Columns[0].Cards[0].Card.Id);Assert.Empty(vm.Error);Assert.Equal(101,vm.Comments.Count);Assert.Equal(101,vm.Comments.Select(c=>c.Comment.Id).Distinct().Count());
    }
}

public sealed class DesktopContentSessionTests
{
    private sealed class Transport(Func<HttpRequestMessage,Task<HttpResponseMessage>> send):HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>send(request); }
    [Fact]
    public async Task LogoutDuringContentReadCannotRestoreBoardCache()
    {
        var id=Guid.NewGuid();var version=Convert.ToBase64String(new byte[8]);var reached=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var finish=new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var services=new ClientServices(new Uri("http://localhost/"),transport:new Transport(request=>
        {
            var path=request.RequestUri!.AbsolutePath;object value=path.EndsWith("columns")?new List<ColumnDto>():path.EndsWith("tags")?new List<TagDto>():path.EndsWith("members")?new List<MemberDto>():new BoardDetailsDto(id,"Приватна дошка",null,BoardRole.Admin,true,true,version);
            if(path.EndsWith("cards")){reached.TrySetResult();return finish.Task;}
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=JsonContent.Create(value)});
        }));
        services.Session.SignIn("test-token",new(Guid.NewGuid(),"test@example.test","Тест"));using var vm=new BoardContentViewModel(services);var loading=vm.OpenAsync(id);await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        services.Session.Clear();finish.SetResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=JsonContent.Create(new PagedResult<CardSummaryDto>([],0,0,100,version))});await loading;
        Assert.False(vm.IsVisible);Assert.False(vm.EditorVisible);Assert.Empty(vm.Columns);Assert.Empty(vm.Assignees);Assert.Empty(vm.Tags);Assert.Empty(vm.Error);
    }
}
