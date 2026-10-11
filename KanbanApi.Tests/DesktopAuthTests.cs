using System.Net;
using System.Net.Http.Json;
using Curs.Services;
using Curs.ViewModels;
using KanbanContracts;
using Xunit;

namespace KanbanApi.Tests;

public sealed class DesktopAuthTests
{
    private sealed class Transport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private static readonly UserDto User = new(Guid.NewGuid(), "olena@example.test", "Олена");

    [Fact]
    public async Task LoginAndLogoutUpdateScreenAndEraseAllSessionFields()
    {
        using var services = new ClientServices(new Uri("http://localhost/"), transport: new Transport((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/boards") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<BoardSummaryDto>()) });
            Assert.Equal("/api/auth/login", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new AuthResponse("opaque-token", DateTime.UtcNow.AddHours(24), User)) });
        }));
        using var vm = new MainWindowViewModel(services) { Email = User.Email, Password = "password123" };
        await vm.SubmitAsync();
        Assert.True(vm.IsSignedIn); Assert.False(vm.ShowAuth); Assert.Equal("", vm.Password);
        Assert.Equal(AppScreen.Boards, services.Navigation.CurrentScreen); Assert.Equal(User.Name, vm.UserName);
        vm.Logout();
        Assert.True(vm.ShowAuth); Assert.Null(services.Session.AccessToken); Assert.Null(services.Session.User);
        Assert.Equal(AppScreen.Login, services.Navigation.CurrentScreen);
        Assert.Equal("", vm.Email); Assert.Equal("", vm.Password); Assert.Equal("", vm.Name); Assert.Equal("", vm.Error);
    }

    [Fact]
    public async Task ValidationAndNetworkFailuresAreVisibleAndPasswordIsErased()
    {
        using var services = new ClientServices(new Uri("http://localhost/"), transport: new Transport((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new ApiProblem
                { Title = "Invalid", Errors = new() { ["Password"] = ["Пароль закороткий."] } }) })));
        using var vm = new MainWindowViewModel(services) { Email = User.Email, Password = "short" };
        await vm.SubmitAsync();
        Assert.Contains("Пароль закороткий", vm.Error); Assert.True(vm.ShowAuth); Assert.False(vm.IsBusy); Assert.Equal("", vm.Password);
        using var offline = new ClientServices(new Uri("http://localhost/"), transport: new Transport((_, _) => throw new HttpRequestException("offline")));
        using var offlineVm = new MainWindowViewModel(offline) { Password = "password123" };
        await offlineVm.SubmitAsync(); Assert.Contains("підключитися", offlineVm.Error); Assert.Equal("", offlineVm.Password);
    }

    [Fact]
    public async Task LogoutDuringPendingLoginCannotRestoreTheSession()
    {
        var pending = new TaskCompletionSource<HttpResponseMessage>();
        using var services = new ClientServices(new Uri("http://localhost/"), transport: new Transport((_, _) => pending.Task));
        using var vm = new MainWindowViewModel(services) { Email = User.Email, Password = "password123" };
        var login = vm.SubmitAsync(); Assert.True(vm.IsBusy);
        Assert.False(vm.SubmitCommand.CanExecute(null)); Assert.False(vm.SwitchCommand.CanExecute(null));
        vm.Logout();
        pending.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new AuthResponse("old-token", DateTime.UtcNow.AddHours(24), User)) });
        await login; Assert.Null(services.Session.AccessToken); Assert.True(vm.ShowAuth); Assert.Equal("", vm.Password);
    }

    [Fact]
    public async Task FailedAnonymousLoginKeepsEmailAndShowsError()
    {
        using var services = new ClientServices(new Uri("http://localhost/"), transport: new Transport((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = JsonContent.Create(new ApiProblem
                { Title = "Невірний email або пароль.", Code = "invalid_credentials" }) })));
        using var vm = new MainWindowViewModel(services) { Email = User.Email, Password = "wrong-password" };
        await vm.SubmitAsync();
        Assert.Equal(User.Email, vm.Email); Assert.Equal("", vm.Password);
        Assert.Contains("Невірний email", vm.Error); Assert.True(vm.ShowAuth);
    }

    [Fact]
    public async Task RegistrationRouteAndExpiredSessionReturnToLogin()
    {
        using var services = new ClientServices(new Uri("http://localhost/"), transport: new Transport((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("/api/auth/register", request.RequestUri!.AbsolutePath);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created) { Content = JsonContent.Create(new AuthResponse("token", DateTime.UtcNow.AddHours(24), User)) });
            }
            if (request.RequestUri!.AbsolutePath == "/api/boards") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<BoardSummaryDto>()) });
            Assert.Equal("token", request.Headers.Authorization?.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = JsonContent.Create(new ApiProblem { Title = "Сесію завершено.", Code = "unauthorized" }) });
        }));
        using var vm = new MainWindowViewModel(services);
        vm.SwitchCommand.Execute(null); Assert.True(vm.IsRegister);
        vm.Email = User.Email; vm.Name = User.Name; vm.Password = "password123";
        await vm.SubmitAsync(); Assert.True(vm.IsSignedIn);
        await vm.CheckSessionAsync(); Assert.True(vm.ShowAuth); Assert.Null(services.Session.AccessToken); Assert.Equal("", vm.Password);
    }
}
