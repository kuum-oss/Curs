using System;
using KanbanContracts;

namespace Curs.Services;

public sealed class SessionService
{
    private readonly object gate = new();
    private readonly Action<Action> dispatch;
    private string? accessToken;
    private UserDto? user;

    public SessionService(Action<Action>? dispatch = null) => this.dispatch = dispatch ?? (action => action());
    public string? AccessToken { get { lock (gate) return accessToken; } }
    public UserDto? User { get { lock (gate) return user; } }
    public event Action? Changed;

    public void SignIn(string token, UserDto signedInUser)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentNullException.ThrowIfNull(signedInUser);
        lock (gate) { accessToken = token; user = signedInUser; }
        dispatch(() => Changed?.Invoke());
    }

    public void UpdateUser(UserDto updated)
    {
        lock(gate) { if(accessToken is null || user?.Id!=updated.Id)return; user=updated; }
        dispatch(()=>Changed?.Invoke());
    }
    public void Clear() => ClearIfCurrent(AccessToken);

    public bool ClearIfCurrent(string? observedToken)
    {
        lock (gate)
        {
            if (accessToken is null && user is null) return false;
            // A late 401 from an older request must not erase a newly established session.
            if (!string.Equals(accessToken, observedToken, StringComparison.Ordinal)) return false;
            accessToken = null;
            user = null;
        }
        dispatch(() => Changed?.Invoke());
        return true;
    }
}
