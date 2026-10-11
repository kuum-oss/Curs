using System.Net;
using System.Net.Http.Json;
using Curs.Services;
using KanbanContracts;
using Xunit;

namespace KanbanApi.Tests;

public sealed class ClientFoundationTests
{
    private static UserDto User() => new(Guid.NewGuid(), "user@example.com", "Олена");
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    [Fact]
    public async Task AuthorizationIsAddedPerRequestAndDisappearsOnLogout()
    {
        var session = new SessionService();
        var tokens = new List<string?>();
        using var client = new HttpClient(new SessionHandler(session) { InnerHandler = new Handler((request, _) =>
        {
            tokens.Add(request.Headers.Authorization?.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }) }) { BaseAddress = new Uri("http://localhost:5000/") };
        var api = new ApiClient(client);
        session.SignIn("token-one", User());
        await api.SendAsync<object>(HttpMethod.Get, "api/probe");
        session.Clear();
        await api.SendAsync<object>(HttpMethod.Get, "api/probe");
        Assert.Equal(new string?[] { "token-one", null }, tokens);
        Assert.Null(session.User);
    }

    [Fact]
    public async Task UnauthorizedClearsSessionAndReturnsToLogin()
    {
        var session = new SessionService();
        using var navigation = new NavigationService(session);
        session.SignIn("token", User());
        navigation.OpenBoard(Guid.NewGuid());
        using var client = new HttpClient(new SessionHandler(session) { InnerHandler = new Handler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = JsonContent.Create(new ApiProblem { Code = "unauthorized" }) })) })
            { BaseAddress = new Uri("http://localhost:5000/") };
        await Assert.ThrowsAsync<ApiClientException>(() => new ApiClient(client).SendAsync<object>(HttpMethod.Get, "api/probe"));
        Assert.Null(session.AccessToken);
        Assert.Equal(AppScreen.Login, navigation.CurrentScreen);
        Assert.Null(navigation.BoardId);
    }

    [Fact]
    public async Task AResponseFromAnOlderSessionCannotClearNewLogin()
    {
        var session = new SessionService();
        session.SignIn("old", User());
        using var client = new HttpClient(new SessionHandler(session) { InnerHandler = new Handler((_, _) =>
        {
            session.SignIn("new", User());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }) }) { BaseAddress = new Uri("http://localhost/") };
        await client.GetAsync("api/probe");
        Assert.Equal("new", session.AccessToken);
    }

    [Fact]
    public async Task ValidationProblemFieldsSurviveTheClientBoundary()
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = JsonContent.Create(new ApiProblem { Code = "validation_failed", Errors = new() { ["Name"] = ["Required"] } }),
        }))) { BaseAddress = new Uri("http://localhost/") };
        var exception = await Assert.ThrowsAsync<ApiClientException>(() => new ApiClient(client).SendAsync<object>(HttpMethod.Post, "api/probe", new { Name = "" }));
        Assert.Equal("validation_failed", exception.Problem.Code);
        Assert.Contains("Name", exception.Problem.Errors!.Keys);
    }

    [Fact]
    public async Task CancellationIsPropagatedAndAbsoluteUrlsAreRejected()
    {
        using var client = new HttpClient(new Handler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        })) { BaseAddress = new Uri("http://localhost/") };
        var api = new ApiClient(client);
        await Assert.ThrowsAsync<ArgumentException>(() => api.SendAsync<object>(HttpMethod.Get, "https://another-host.example/probe"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.SendAsync<object>(HttpMethod.Get, "api/probe", cancellationToken: cancellation.Token));
    }

    [Fact]
    public void NavigationRequiresSessionAndValidBoardId()
    {
        var session = new SessionService();
        using var navigation = new NavigationService(session);
        Assert.Throws<InvalidOperationException>(() => navigation.OpenBoards());
        session.SignIn("token", User());
        Assert.Throws<ArgumentException>(() => navigation.OpenBoard(Guid.Empty));
        navigation.OpenBoards();
        Assert.Equal(AppScreen.Boards, navigation.CurrentScreen);
    }
}
