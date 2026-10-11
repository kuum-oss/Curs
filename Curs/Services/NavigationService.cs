using System;

namespace Curs.Services;

public enum AppScreen { Login, Boards, Board }

public sealed class NavigationService : IDisposable
{
    private readonly SessionService session;
    public NavigationService(SessionService session)
    {
        this.session = session;
        session.Changed += OnSessionChanged;
    }
    public AppScreen CurrentScreen { get; private set; } = AppScreen.Login;
    public Guid? BoardId { get; private set; }
    public event Action? Changed;

    public void OpenBoards()
    {
        if (session.AccessToken is null) throw new InvalidOperationException("Sign in before opening boards.");
        CurrentScreen = AppScreen.Boards; BoardId = null; Changed?.Invoke();
    }

    public void OpenBoard(Guid boardId)
    {
        if (session.AccessToken is null) throw new InvalidOperationException("Sign in before opening a board.");
        if (boardId == Guid.Empty) throw new ArgumentException("Board ID is required.", nameof(boardId));
        CurrentScreen = AppScreen.Board; BoardId = boardId; Changed?.Invoke();
    }

    private void OnSessionChanged()
    {
        if (session.AccessToken is not null) return;
        CurrentScreen = AppScreen.Login; BoardId = null; Changed?.Invoke();
    }

    public void Dispose() => session.Changed -= OnSessionChanged;
}
