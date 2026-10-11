using System.Net;
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
public sealed class DesktopBoardIntegrationTests(SqlFixture fixture)
{
    [SqlFact]
    public async Task DesktopWorkflowCreatesEditsSharesRestrictsRevokesAndDeletesRealBoard()
    {
        await using var db = fixture.CreateContext();
        User User() { var email = Guid.NewGuid().ToString("N") + "@example.test"; return new User { Email = email, NormalizedEmail = email.ToUpperInvariant(), Name = "Учасник", PasswordHash = "unused" }; }
        var owner = User(); var person = User(); db.Users.AddRange(owner,person); await db.SaveChangesAsync();
        using var app = ApiFoundationTests.Factory(fixture.ConnectionString); using var bootstrap = app.CreateClient();
        ClientServices Services(User user)
        {
            var services = new ClientServices(new Uri("http://localhost/"), transport: app.Server.CreateHandler());
            services.Session.SignIn(app.Services.GetRequiredService<JwtService>().Issue(new UserDto(user.Id,user.Email,user.Name)).AccessToken, new UserDto(user.Id,user.Email,user.Name));
            return services;
        }
        using var admin = Services(owner); using var workspace = new BoardWorkspaceViewModel(admin);
        admin.Navigation.OpenBoards(); await workspace.Loading; Assert.Empty(workspace.Boards);
        workspace.NewName = "Нова дошка"; workspace.NewDescription = "Опис"; await workspace.CreateAsync();
        Assert.True(workspace.IsAdmin); Assert.Single(workspace.Members); var boardId = workspace.SelectedBoard!.Id;
        workspace.EditName = "Оновлена дошка"; await workspace.SaveBoardAsync(); Assert.Equal("Оновлена дошка",workspace.BoardTitle);
        workspace.MemberEmail = person.Email; await workspace.AddMemberAsync(); Assert.Equal(2,workspace.Members.Count);
        var row = workspace.Members.Single(x => x.Member.User.Id == person.Id); row.CanCreateCards = false; row.CanDeleteCards = true;
        await workspace.SavePermissionsAsync(row); Assert.False(workspace.Members.Single(x => x.Member.User.Id == person.Id).CanCreateCards);
        using var participant = Services(person); using var memberView = new BoardWorkspaceViewModel(participant);
        participant.Navigation.OpenBoards(); await memberView.Loading; Assert.Single(memberView.Boards);
        await memberView.OpenAsync(boardId); Assert.False(memberView.IsAdmin); Assert.False(memberView.SaveBoardCommand.CanExecute(null));
        Assert.False(memberView.DeleteCommand.CanExecute(null)); Assert.False(memberView.AddMemberCommand.CanExecute(null));
        Assert.All(memberView.Members,x => Assert.False(x.CanManage)); Assert.False(memberView.SelectedBoard!.CanCreateCards);
        workspace.RequestRemove(workspace.Members.Single(x => x.Member.User.Id == person.Id)); Assert.True(workspace.ShowConfirmation);
        workspace.CancelConfirmation(); Assert.False(workspace.ShowConfirmation); Assert.Equal(2,workspace.Members.Count);
        workspace.RequestRemove(workspace.Members.Single(x => x.Member.User.Id == person.Id)); await workspace.ConfirmAsync(); Assert.Single(workspace.Members);
        await memberView.RefreshAsync(); Assert.True(memberView.ShowList); Assert.Empty(memberView.Boards); Assert.Empty(memberView.Members);
        Assert.NotNull(participant.Session.AccessToken); Assert.NotNull(participant.Session.User);
        workspace.RequestDelete(); workspace.CancelConfirmation(); Assert.NotNull(workspace.SelectedBoard);
        workspace.RequestDelete(); await workspace.ConfirmAsync(); Assert.True(workspace.ShowList); Assert.Empty(workspace.Boards);
        Assert.False(await db.Boards.AnyAsync(x => x.Id == boardId));
    }
}

public sealed class DesktopBoardTests
{
    private sealed class Transport(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) => send(request,token); }
    private static readonly UserDto User = new(Guid.NewGuid(), "test@example.test", "Тест");
    private static readonly Guid BoardId = Guid.NewGuid();
    private static readonly string Version = Convert.ToBase64String(new byte[8]);
    private static HttpResponseMessage Response(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = JsonContent.Create(value) };
    private static ClientServices Services(HttpMessageHandler transport)
    {
        var services = new ClientServices(new Uri("http://localhost/"),transport:transport); services.Session.SignIn("test-token",User); return services;
    }
    private static BoardDetailsDto Board(string version) => new(BoardId,"Серверна назва","Опис",BoardRole.Admin,true,true,version);

    [Fact]
    public async Task LogoutDuringLoadingCannotRestorePrivateBoardCache()
    {
        var pending = new TaskCompletionSource<HttpResponseMessage>();
        using var services = Services(new Transport((_,_) => pending.Task)); using var vm = new BoardWorkspaceViewModel(services);
        services.Navigation.OpenBoards(); var load = vm.Loading; Assert.True(vm.IsBusy);
        services.Auth.Logout(); Assert.Empty(vm.Boards); Assert.False(vm.IsBusy);
        pending.SetResult(Response(new[] { new BoardSummaryDto(BoardId,"Приватна","Опис",BoardRole.Admin) })); await load;
        Assert.Empty(vm.Boards); Assert.Empty(vm.Members); Assert.Null(vm.SelectedBoard); Assert.Equal("",vm.EditName);
    }

    [Fact]
    public async Task ConflictRefreshesVersionPreservesDraftAndDoesNotRetryMutation()
    {
        var gets = 0; var puts = 0; var next = Convert.ToBase64String(new byte[] {0,0,0,0,0,0,0,1});
        using var services = Services(new Transport((request,_) =>
        {
            if (request.Method == HttpMethod.Put) { puts++; return Task.FromResult(Response(new ApiProblem { Code = "concurrency_conflict",Title = "Конфлікт" },HttpStatusCode.Conflict)); }
            if (request.RequestUri!.AbsolutePath.EndsWith("/members")) return Task.FromResult(Response(Array.Empty<MemberDto>()));
            gets++; return Task.FromResult(Response(Board(gets == 1 ? Version : next)));
        }));
        using var vm = new BoardWorkspaceViewModel(services); await vm.OpenAsync(BoardId); vm.EditName = "Моя незбережена назва";
        await vm.SaveBoardAsync(); Assert.Equal(1,puts); Assert.Equal(next,vm.SelectedBoard!.Version);
        Assert.Equal("Моя незбережена назва",vm.EditName); Assert.Contains("Конфлікт",vm.Error);
    }

    [Fact]
    public async Task FailedConflictRefreshIsReportedWithoutEscapingTheCommand()
    {
        var gets = 0;
        using var services = Services(new Transport((request,_) =>
        {
            if (request.Method == HttpMethod.Put) return Task.FromResult(Response(new ApiProblem { Code = "concurrency_conflict",Title = "Конфлікт" },HttpStatusCode.Conflict));
            if (request.RequestUri!.AbsolutePath.EndsWith("/members")) return Task.FromResult(Response(Array.Empty<MemberDto>()));
            if (++gets > 1) throw new HttpRequestException("offline");
            return Task.FromResult(Response(Board(Version)));
        }));
        using var vm = new BoardWorkspaceViewModel(services); await vm.OpenAsync(BoardId); await vm.SaveBoardAsync();
        Assert.False(vm.IsBusy); Assert.Contains("підключення",vm.Error); Assert.NotNull(vm.SelectedBoard);
    }

    [Fact]
    public async Task DestructiveActionRequiresConfirmationAndCancelSendsNothing()
    {
        var deletes = 0;
        using var services = Services(new Transport((request,_) =>
        {
            if (request.Method == HttpMethod.Delete) { deletes++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)); }
            if (request.RequestUri!.AbsolutePath == "/api/boards") return Task.FromResult(Response(Array.Empty<BoardSummaryDto>()));
            if (request.RequestUri.AbsolutePath.EndsWith("/members")) return Task.FromResult(Response(Array.Empty<MemberDto>()));
            return Task.FromResult(Response(Board(Version)));
        }));
        using var vm = new BoardWorkspaceViewModel(services); await vm.OpenAsync(BoardId);
        vm.RequestDelete(); Assert.True(vm.ShowConfirmation); Assert.Equal(0,deletes);
        vm.CancelConfirmation(); await vm.ConfirmAsync(); Assert.Equal(0,deletes);
        vm.RequestDelete(); await vm.ConfirmAsync(); Assert.Equal(1,deletes); Assert.True(vm.ShowList);
    }
}
