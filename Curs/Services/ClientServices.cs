using System;
using System.Net.Http;

namespace Curs.Services;

// Desktop composition root. No database or EF dependency is introduced into the client.
public sealed class ClientServices : IDisposable
{
    private readonly HttpClient http;
    public SessionService Session { get; }
    public NavigationService Navigation { get; }
    public ApiClient Api { get; }
    public AuthService Auth { get; }
    public BoardService Boards { get; }
    public ContentService Content { get; }
    public ProfileService Profile { get; }
    public ImageService Images { get; }
    public RememberedSession Remembered { get; }
    public ChatService Chats { get; }
    public NotificationService Notifications { get; }
    public CalendarService Calendar { get; }
    public BackupService Backups { get; }

    public ClientServices(Uri baseAddress, Action<Action>? dispatch = null, HttpMessageHandler? transport = null, ISessionStore? sessionStore = null)
    {
        if (!baseAddress.IsAbsoluteUri || (baseAddress.Scheme != "http" && baseAddress.Scheme != "https"))
            throw new ArgumentException("Configure an absolute HTTP(S) API address.", nameof(baseAddress));
        Session = new SessionService(dispatch);
        Remembered = new RememberedSession(Session, sessionStore ?? new MemorySessionStore());
        Navigation = new NavigationService(Session);
        // Authenticated redirects are deliberately not followed to another host.
        http = new HttpClient(new SessionHandler(Session) { InnerHandler = transport ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            // Renew pooled connections after a local API restart. On macOS another
            // local service can temporarily answer on port 5000 while Docker stops.
            PooledConnectionLifetime = TimeSpan.FromSeconds(5),
            ConnectTimeout = TimeSpan.FromSeconds(3)
        } })
        { BaseAddress = baseAddress, Timeout = TimeSpan.FromMinutes(2) };
        Api = new ApiClient(http);
        Auth = new AuthService(Api, Session, Navigation);
        Boards = new BoardService(Api);
        Chats = new ChatService(Api);
        Notifications = new NotificationService(Api);
        Calendar = new CalendarService(Api);
        Backups = new BackupService(Api);
        Content = new ContentService(Api);
        Profile = new ProfileService(Api,Session);
        Images = new ImageService(Api, Session);
    }

    public void Dispose() { Remembered.Dispose(); Session.Clear(); Images.Dispose(); Navigation.Dispose(); http.Dispose(); }
}
