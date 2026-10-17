using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using KanbanContracts;

namespace Curs.Services;

public sealed record SavedSession(string AccessToken, string Email);
public interface ISessionStore
{
    SavedSession? Read();
    void Write(SavedSession value);
    void Clear();
}

// Default for tests and hosts without a configured persistent credential store.
public sealed class MemorySessionStore : ISessionStore
{
    private SavedSession? value;
    public SavedSession? Read() => value;
    public void Write(SavedSession session) => value = session;
    public void Clear() => value = null;
}

public sealed class RememberedSession : IDisposable
{
    private readonly SessionService session;
    private readonly ISessionStore store;
    public bool Enabled { get; private set; }
    public string? Error { get; private set; }
    public RememberedSession(SessionService session, ISessionStore store)
    {
        this.session = session; this.store = store;
        session.Changed += Changed;
    }
    public SavedSession? Read()
    {
        try { Error = null; var saved = store.Read(); Enabled = saved is not null; return saved; }
        catch (Exception) { Error = "Не вдалося прочитати збережений вхід. Увійдіть вручну."; return null; }
    }
    public void SetEnabled(bool enabled)
    {
        Enabled = enabled;
        if (!enabled) Forget();
        else if (session.User is not null) Changed();
    }
    public void Forget()
    {
        try { store.Clear(); Error = null; }
        catch (Exception) { Error = "Не вдалося прибрати збережений вхід. Перевірте доступ до В’язки ключів."; }
    }
    private void Changed()
    {
        if (session.AccessToken is not {} token || session.User is not {} user) { Forget(); return; }
        if (!Enabled) return;
        try { store.Write(new(token, user.Email)); Error = null; }
        catch (Exception) { Error = "Вхід виконано, але зберегти його на цьому комп’ютері не вдалося."; }
    }
    // Closing the app must keep the saved credential. Explicit logout still clears it.
    public void Dispose() => session.Changed -= Changed;
}
