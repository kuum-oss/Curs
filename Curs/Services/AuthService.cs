using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using KanbanContracts;

namespace Curs.Services;

public sealed class AuthService(ApiClient api, SessionService session, NavigationService navigation)
{
    public Task LoginAsync(string email, string password, CancellationToken cancellationToken = default) =>
        AuthenticateAsync("api/auth/login", new LoginRequest { Email = email.Trim(), Password = password }, cancellationToken);
    public Task RegisterAsync(string email, string name, string password, CancellationToken cancellationToken = default) =>
        AuthenticateAsync("api/auth/register", new RegisterRequest { Email = email.Trim(), Name = name.Trim(), Password = password }, cancellationToken);
    private async Task AuthenticateAsync(string path, object request, CancellationToken cancellationToken)
    {
        var response = await api.SendAsync<AuthResponse>(HttpMethod.Post, path, request, cancellationToken)
            ?? throw new InvalidOperationException("Сервер не повернув дані сесії.");
        cancellationToken.ThrowIfCancellationRequested();
        session.SignIn(response.AccessToken, response.User, response.AccountRestored ? "Аккаунт відновлено. Заплановане видалення скасовано." : null);
        navigation.OpenBoards();
    }
    public async Task<UserDto> MeAsync(CancellationToken cancellationToken = default) =>
        await api.SendAsync<UserDto>(HttpMethod.Get, "api/auth/me", cancellationToken: cancellationToken)
        ?? throw new InvalidOperationException("Сервер не повернув дані користувача.");
    public async Task RestoreAsync(string accessToken, CancellationToken cancellationToken)
    {
        // Validate before exposing the workspace; a saved profile is never trusted.
        var user = await api.SendAsync<UserDto>(HttpMethod.Get, "api/auth/me", cancellationToken: cancellationToken, accessToken: accessToken)
            ?? throw new InvalidOperationException("Сервер не повернув дані користувача.");
        cancellationToken.ThrowIfCancellationRequested();
        session.SignIn(accessToken, user);
        navigation.OpenBoards();
    }
    public void Logout() => session.Clear();
}
