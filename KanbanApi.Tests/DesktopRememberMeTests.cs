using System.Net;
using System.Net.Http.Json;
using Curs.Services;
using Curs.ViewModels;
using KanbanContracts;
using Xunit;

namespace KanbanApi.Tests;

public sealed class DesktopRememberMeTests
{
    private sealed class Transport(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken t)=>send(r,t); }
    private static readonly UserDto User = new(Guid.NewGuid(), "remember@example.test", "Олена");
    private static HttpResponseMessage Ok(object data) => new(HttpStatusCode.OK){Content=JsonContent.Create(data)};
    private static HttpResponseMessage Denied() => new(HttpStatusCode.Unauthorized){Content=JsonContent.Create(new ApiProblem{Title="Сеанс завершено."})};
    private static Transport Server(string token="saved-token", UserDto? user=null) => new((r,_)=>Task.FromResult(
        r.RequestUri!.AbsolutePath=="/api/boards" ? Ok(Array.Empty<BoardSummaryDto>()) :
        r.RequestUri.AbsolutePath=="/api/auth/me" ? r.Headers.Authorization?.Parameter==token ? Ok(user??User) : Denied() :
        Ok(new AuthResponse(token,DateTime.UtcNow.AddHours(24),user??User))));
    private static ClientServices Client(ISessionStore store, HttpMessageHandler? transport=null) => new(new Uri("http://localhost/"),transport:transport??Server(),sessionStore:store);

    [Fact]
    public async Task OptInSurvivesAppCloseAndRestoresOnlyAfterServerValidation()
    {
        var store=new MemorySessionStore();
        using(var services=Client(store))
        using(var vm=new MainWindowViewModel(services){RememberMe=true,Email=User.Email,Password="never-store-this-password"})
        {
            await vm.SubmitAsync(); Assert.True(vm.IsSignedIn);
            Assert.Equal(new SavedSession("saved-token",User.Email),store.Read()); Assert.Empty(vm.Password);
        }
        Assert.NotNull(store.Read());
        var validated=false;
        using var next=Client(store,new Transport((r,_)=>
        {
            if(r.RequestUri!.AbsolutePath=="/api/auth/me"){Assert.Equal("saved-token",r.Headers.Authorization?.Parameter);validated=true;return Task.FromResult(Ok(User));}
            Assert.True(validated);return Task.FromResult(Ok(Array.Empty<BoardSummaryDto>()));
        }));
        using var window=new MainWindowViewModel(next);Assert.False(window.IsSignedIn);
        await window.InitializeAsync();Assert.True(validated);Assert.True(window.IsSignedIn);Assert.True(window.RememberMe);
    }

    [Fact]
    public async Task DefaultOffNeverPersistsAndLogoutAllowsAnotherAccount()
    {
        var store=new MemorySessionStore();using var services=Client(store);using var vm=new MainWindowViewModel(services){Email=User.Email,Password="password123"};
        await vm.SubmitAsync();Assert.Null(store.Read());vm.Logout();
        vm.RememberMe=true;vm.Email=User.Email;vm.Password="password123";await vm.SubmitAsync();Assert.NotNull(store.Read());
        // Use the same logout as the workspace's Вийти button.
        services.Auth.Logout();Assert.True(vm.ShowAuth);Assert.Null(store.Read());Assert.True(vm.RememberMe);
        vm.RememberMe=false;vm.Email="other@example.test";vm.Password="password123";await vm.SubmitAsync();Assert.Null(store.Read());
    }

    [Fact]
    public async Task RevokedOrExpiredTokenIsForgottenWithoutEnteringWorkspace()
    {
        var store=new MemorySessionStore();store.Write(new("expired",User.Email));
        using var services=Client(store,new Transport((_,_)=>Task.FromResult(Denied())));using var vm=new MainWindowViewModel(services);
        await vm.InitializeAsync();Assert.True(vm.ShowAuth);Assert.Null(store.Read());Assert.Null(services.Session.AccessToken);
        Assert.Contains("завершився",vm.Status);Assert.False(vm.CanRetrySavedSession);Assert.Equal(User.Email,vm.Email);
    }

    [Fact]
    public async Task OfflineKeepsCredentialForRetryAndTurningOffForgetsIt()
    {
        var store=new MemorySessionStore();store.Write(new("saved-token",User.Email));var offline=true;
        using var services=Client(store,new Transport((r,_)=>
        {
            if(offline)throw new HttpRequestException();
            return Task.FromResult(r.RequestUri!.AbsolutePath=="/api/auth/me"?Ok(User):Ok(Array.Empty<BoardSummaryDto>()));
        }));using var vm=new MainWindowViewModel(services);
        await vm.InitializeAsync();Assert.True(vm.ShowAuth);Assert.NotNull(store.Read());Assert.True(vm.CanRetrySavedSession);
        offline=false;await vm.InitializeAsync();Assert.True(vm.IsSignedIn);services.Auth.Logout();
        store.Write(new("saved-token",User.Email));vm.RememberMe=false;Assert.Null(store.Read());
    }

    [Fact]
    public async Task LogoutDuringPendingAutoLoginCannotReestablishSavedSession()
    {
        var store=new MemorySessionStore();store.Write(new("old",User.Email));var reached=new TaskCompletionSource();var pending=new TaskCompletionSource<HttpResponseMessage>();
        using var services=Client(store,new Transport((_,_)=>{reached.SetResult();return pending.Task;}));using var vm=new MainWindowViewModel(services);
        var restore=vm.InitializeAsync();await reached.Task;vm.Logout();pending.SetResult(Ok(User));await restore;
        Assert.True(vm.ShowAuth);Assert.Null(store.Read());Assert.Null(services.Session.User);Assert.Empty(vm.Password);
    }

    [Fact]
    public void RotatedTokenAndEmailReplaceTheOnlySavedUserAnd401ClearsIt()
    {
        var store=new MemorySessionStore();using var services=Client(store);services.Remembered.SetEnabled(true);
        services.Session.SignIn("first",User);services.Session.SignIn("rotated",User with{Email="changed@example.test"});
        Assert.Equal(new SavedSession("rotated","changed@example.test"),store.Read());
        Assert.False(services.Session.ClearIfCurrent("first"));Assert.NotNull(store.Read());
        services.Session.ClearIfCurrent("rotated");Assert.Null(store.Read());
    }

    private sealed class FailingStore:ISessionStore
    {
        public SavedSession? Read()=>throw new IOException();
        public void Write(SavedSession value)=>throw new IOException();
        public void Clear()=>throw new IOException();
    }
    [Fact]
    public async Task UnavailableStorageStillAllowsManualLoginAndReportsFailure()
    {
        using var services=Client(new FailingStore());using var vm=new MainWindowViewModel(services);
        await vm.InitializeAsync();Assert.Contains("вручну",vm.Error);Assert.True(vm.ShowAuth);
        vm.RememberMe=true;vm.Email=User.Email;vm.Password="password123";await vm.SubmitAsync();Assert.True(vm.IsSignedIn);Assert.NotNull(services.Remembered.Error);
    }
}
