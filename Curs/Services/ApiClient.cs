using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KanbanContracts;

namespace Curs.Services;

public sealed class SessionHandler(SessionService session) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = session.AccessToken;
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized) session.ClearIfCurrent(token);
        return response;
    }
}

public sealed class ApiClientException(int status, ApiProblem problem)
    : Exception(problem.Title ?? "Не вдалося виконати запит.")
{
    public int Status { get; } = status;
    public ApiProblem Problem { get; } = problem;
}

public sealed class ApiClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<T?> SendAsync<T>(HttpMethod method, string path, object? payload = null, CancellationToken cancellationToken = default, string? accessToken = null, TimeSpan? timeout = null)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout??TimeSpan.FromSeconds(10));cancellationToken=deadline.Token;
        // Relative paths keep credentials on the configured API host.
        if (!Uri.TryCreate(path, UriKind.Relative, out _) || path.StartsWith("//", StringComparison.Ordinal))
            throw new ArgumentException("Use a relative API path.", nameof(path));
        using var request = new HttpRequestMessage(method, path);
        if (accessToken is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (payload is not null) request.Content = JsonContent.Create(payload, options: Json);
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccess(response, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent) return default;
        return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken);
    }
    public async Task<byte[]> BytesAsync(string path, CancellationToken token, TimeSpan? timeout = null)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(timeout??TimeSpan.FromSeconds(10));token=deadline.Token;
        ValidatePath(path);
        using var response = await http.GetAsync(path, token); await EnsureSuccess(response, token);
        return await response.Content.ReadAsByteArrayAsync(token);
    }
    public async Task<T?> MultipartAsync<T>(string path, HttpContent content, CancellationToken token)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(10));token=deadline.Token;
        ValidatePath(path);
        using var response = await http.PutAsync(path, content, token); await EnsureSuccess(response, token);
        return await response.Content.ReadFromJsonAsync<T>(Json, token);
    }
    private static void ValidatePath(string path)
    {
        if (!Uri.TryCreate(path, UriKind.Relative, out _) || path.StartsWith("//", StringComparison.Ordinal))
            throw new ArgumentException("Use a relative API path.", nameof(path));
    }
    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            ApiProblem? problem = null;
            try { problem = await response.Content.ReadFromJsonAsync<ApiProblem>(Json, cancellationToken); }
            catch (JsonException) { }
            throw new ApiClientException((int)response.StatusCode, problem ?? new ApiProblem
                { Status = (int)response.StatusCode, Title = "Не вдалося виконати запит.", Code = "http_error" });
        }
    }

}
