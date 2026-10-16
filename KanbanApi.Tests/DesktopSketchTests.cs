using System.Net;
using System.Net.Http.Json;
using Curs.Services;
using Curs.ViewModels;
using KanbanContracts;
using Xunit;
namespace KanbanApi.Tests;

public sealed class DesktopSketchSafetyTests
{
    private sealed class Transport(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>send(request,token);}
    [Fact]
    public async Task LogoutWhilePasswordResponseIsPendingCannotSignTheUserBackIn()
    {
        var user=new UserDto(Guid.NewGuid(),"demo@example.test","Демо",Convert.ToBase64String(new byte[8]));
        var pending=new TaskCompletionSource<HttpResponseMessage>();
        using var services=new ClientServices(new Uri("http://localhost/"),transport:new Transport((r,_)=>r.Method==HttpMethod.Get?Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=JsonContent.Create(user)}):pending.Task));
        services.Session.SignIn("old-token",user);using var profile=new ProfileViewModel(services);await profile.Open();profile.CurrentPassword="old-password";profile.NewPassword="new-password";profile.Confirmation="new-password";
        var save=profile.Password();Assert.True(profile.IsBusy);services.Auth.Logout();
        pending.SetResult(new HttpResponseMessage(HttpStatusCode.OK){Content=JsonContent.Create(new AuthResponse("new-token",DateTime.UtcNow.AddHours(24),user))});await save;
        Assert.Null(services.Session.User);Assert.Null(services.Session.AccessToken);Assert.Equal("",profile.CurrentPassword);Assert.Equal("",profile.NewPassword);
    }
    [Fact]
    public async Task ProfileConflictKeepsDraftAndUsesFreshVersionOnlyOnExplicitRetry()
    {
        var first=Convert.ToBase64String(new byte[8]);var next=Convert.ToBase64String(new byte[]{0,0,0,0,0,0,0,1});var user=new UserDto(Guid.NewGuid(),"demo@example.test","Сервер",first);var reads=0;var writes=0;
        using var services=new ClientServices(new Uri("http://localhost/"),transport:new Transport((r,_)=>
        {
            if(r.Method==HttpMethod.Get){reads++;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=JsonContent.Create(user with{Version=reads==1?first:next})});}
            writes++;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict){Content=JsonContent.Create(new ApiProblem{Code="concurrency_conflict",Title="Конфлікт"})});
        }));
        services.Session.SignIn("token",user);using var profile=new ProfileViewModel(services);await profile.Open();profile.Name="Моя чернетка";await profile.Save();Assert.Equal("Моя чернетка",profile.Name);Assert.Equal(1,writes);Assert.Equal(next,services.Session.User!.Version);Assert.Contains("Чернетку збережено",profile.Error);
    }
}
[Collection("sql")]
public sealed class DesktopSketchIntegrationTests(SqlFixture fixture)
{
    [SqlFact]
    public async Task RevocationClearsPrivateDialogsEvenWhenAnUnsavedDraftIsOpen()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);
        using var owner=new ClientServices(new Uri("http://localhost/"),transport:app.Server.CreateHandler());using var admin=new BoardWorkspaceViewModel(owner);
        using var member=new ClientServices(new Uri("http://localhost/"),transport:app.Server.CreateHandler());using var workspace=new BoardWorkspaceViewModel(member);
        await owner.Auth.RegisterAsync(Guid.NewGuid()+"@example.test","Власник","profile-password123!");await admin.Loading;admin.NewName="Приватна";await admin.CreateAsync();var id=admin.SelectedBoard!.Id;
        admin.Content.NewColumnName="План";await admin.Content.CreateColumnAsync();await admin.RefreshAsync();
        await member.Auth.RegisterAsync(Guid.NewGuid()+"@example.test","Учасник","profile-password123!");await workspace.Loading;admin.MemberEmail=member.Session.User!.Email;await admin.AddMemberAsync();
        await workspace.OpenVisualAsync(id);workspace.Content.NewCard(workspace.Content.Columns[0]);workspace.Content.Title="Приватна чернетка";Assert.True(workspace.HasDraft);
        admin.RequestRemove(admin.Members.Single(x=>x.Member.User.Id==member.Session.User.Id));await admin.ConfirmAsync();await workspace.PollPresentation();
        Assert.Null(workspace.SelectedBoard);Assert.Empty(workspace.Members);Assert.Empty(workspace.Boards);Assert.False(workspace.Content.AnyDialogVisible);Assert.Equal("",workspace.Content.Title);Assert.Contains("доступ відкликано",workspace.Error);
    }
    [SqlFact]
    public async Task DesktopCanSaveProfileAndBatchRolesWithoutLosingTheOpenBoard()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);
        using var owner=new ClientServices(new Uri("http://localhost/"),transport:app.Server.CreateHandler());using var workspace=new BoardWorkspaceViewModel(owner);
        await owner.Auth.RegisterAsync(Guid.NewGuid()+"@example.test","Власник","profile-password123!");await workspace.Loading;workspace.NewName="Ескіз";await workspace.CreateAsync();var id=workspace.SelectedBoard!.Id;
        using var member=new ClientServices(new Uri("http://localhost/"),transport:app.Server.CreateHandler());await member.Auth.RegisterAsync(Guid.NewGuid()+"@example.test","Учасник","profile-password123!");Assert.True(workspace.SaveMembersCommand.CanExecute(null));workspace.MemberEmail=member.Session.User!.Email;await workspace.AddMemberAsync();
        var row=workspace.Members.Single(x=>x.Member.User.Id==member.Session.User.Id);row.SelectedRole=row.Roles.Single(x=>x.Value==BoardRole.Admin);await workspace.SaveMembers();Assert.False(workspace.HasError);Assert.Equal(BoardRole.Admin,workspace.Members.Single(x=>x.Member.User.Id==member.Session.User.Id).Member.Role);
        await workspace.Profile.Open();workspace.Profile.Name="Оновлений власник";await workspace.Profile.Save();Assert.Equal("",workspace.Profile.Error);Assert.Equal(id,workspace.SelectedBoard.Id);Assert.Equal("Оновлений власник",workspace.UserName);Assert.Equal("",workspace.Profile.CurrentPassword);
        await workspace.LoadAsync();var tile=workspace.Boards.Single(x=>x.Board.Id==id);await workspace.FavoriteTile(tile.Board);Assert.True(workspace.Boards.Single(x=>x.Board.Id==id).Board.IsFavorite);
        await workspace.ArchiveTile(workspace.Boards.Single(x=>x.Board.Id==id).Board);Assert.True(workspace.Boards.Single(x=>x.Board.Id==id).Board.IsArchived);
        await workspace.ArchiveTile(workspace.Boards.Single(x=>x.Board.Id==id).Board);Assert.False(workspace.Boards.Single(x=>x.Board.Id==id).Board.IsArchived);
    }
}
