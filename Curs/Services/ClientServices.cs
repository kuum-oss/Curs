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

    public ClientServices(Uri baseAddress, Action<Action>? dispatch = null)
    {
        if (!baseAddress.IsAbsoluteUri || (baseAddress.Scheme != "http" && baseAddress.Scheme != "https"))
            throw new ArgumentException("Configure an absolute HTTP(S) API address.", nameof(baseAddress));
        Session = new SessionService(dispatch);
        Navigation = new NavigationService(Session);
        // Authenticated redirects are deliberately not followed to another host.
        http = new HttpClient(new SessionHandler(Session) { InnerHandler = new HttpClientHandler { AllowAutoRedirect = false } })
        { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(10) };
        Api = new ApiClient(http);
    }

    public void Dispose() { Navigation.Dispose(); http.Dispose(); }
}
