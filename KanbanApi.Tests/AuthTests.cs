using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using KanbanApi.Infrastructure;
using KanbanContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace KanbanApi.Tests;

[Collection("sql")]
public sealed class AuthTests(SqlFixture fixture)
{
    private const string Key = "test-only-key-for-kanban-api-at-least-32-bytes-long-enough-for-hs384";
    private static RegisterRequest Request() => new() { Email = Guid.NewGuid().ToString("N") + "@example.test", Name = "  Олена  ", Password = "мійПароль123!" };

    [SqlFact]
    public async Task RegistrationStoresSaltedBcryptAndIssuesExactly24HourToken()
    {
        using var app = ApiFoundationTests.Factory(fixture.ConnectionString);
        using var client = app.CreateClient();
        var request = Request();
        var response = await client.PostAsJsonAsync("/api/auth/register", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        Assert.Equal("Олена", auth.User.Name);
        await using var db = fixture.CreateContext();
        var stored = await db.Users.SingleAsync(x => x.Id == auth.User.Id);
        Assert.NotEqual(request.Password, stored.PasswordHash);
        Assert.True(BCrypt.Net.BCrypt.Verify(request.Password, stored.PasswordHash));
        Assert.Equal("12", BCrypt.Net.BCrypt.InterrogateHash(stored.PasswordHash).WorkFactor);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(auth.AccessToken);
        Assert.Equal(auth.User.Id.ToString(), jwt.Subject);
        Assert.Equal(TimeSpan.FromHours(24), jwt.ValidTo - jwt.ValidFrom);
        Assert.Equal(auth.ExpiresAtUtc, jwt.ValidTo);
        Assert.Contains(jwt.Claims, x => x.Type == "iat");
        Assert.Contains(jwt.Claims, x => x.Type == "jti");
        Assert.DoesNotContain(jwt.Claims, x => x.Type == "role");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        var me = await client.GetFromJsonAsync<UserDto>("/api/auth/me");
        Assert.Equal(auth.User, me);
        // Same password has distinct salts in separate accounts.
        var other = Request(); other.Password = request.Password;
        var second = (await (await client.PostAsJsonAsync("/api/auth/register", other)).Content.ReadFromJsonAsync<AuthResponse>())!;
        Assert.NotEqual(stored.PasswordHash, (await db.Users.SingleAsync(x => x.Id == second.User.Id)).PasswordHash);
    }

    [SqlFact]
    public async Task LoginNormalizesEmailAndUnknownAccountMatchesWrongPasswordResponse()
    {
        using var app = ApiFoundationTests.Factory(fixture.ConnectionString);
        using var client = app.CreateClient(); var request = Request();
        var registered = (await (await client.PostAsJsonAsync("/api/auth/register", request)).Content.ReadFromJsonAsync<AuthResponse>())!;
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = request.Email.ToUpperInvariant(), Password = request.Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(registered.User, (await response.Content.ReadFromJsonAsync<AuthResponse>())!.User);
        var wrong = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = request.Email, Password = "wrong-password" });
        var missing = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = Request().Email, Password = "wrong-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal("Bearer", wrong.Headers.WwwAuthenticate.Single().Scheme);
        Assert.Equal(wrong.StatusCode, missing.StatusCode);
        var first = (await wrong.Content.ReadFromJsonAsync<ApiProblem>())!;
        var second = (await missing.Content.ReadFromJsonAsync<ApiProblem>())!;
        Assert.Equal("invalid_credentials", first.Code); Assert.Equal(first.Code, second.Code); Assert.Equal(first.Title, second.Title);
        var duplicate = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest { Email = request.Email.ToUpperInvariant(), Name = "Інший", Password = request.Password });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("email_exists", (await duplicate.Content.ReadFromJsonAsync<ApiProblem>())!.Code);
    }

    [SqlFact]
    public async Task ParallelRegistrationLeavesOnlyOneAccount()
    {
        using var app = ApiFoundationTests.Factory(fixture.ConnectionString);
        using var client = app.CreateClient(); var request = Request();
        var results = await Task.WhenAll(client.PostAsJsonAsync("/api/auth/register", request), client.PostAsJsonAsync("/api/auth/register", request));
        Assert.Single(results, x => x.StatusCode == HttpStatusCode.Created);
        Assert.Single(results, x => x.StatusCode == HttpStatusCode.Conflict);
        await using var db = fixture.CreateContext();
        Assert.Equal(1, await db.Users.CountAsync(x => x.NormalizedEmail == request.Email.ToUpperInvariant()));
        foreach (var result in results) result.Dispose();
    }

    [SqlFact]
    public async Task InvalidFieldsAndUtf8PasswordBoundaryAreEnforced()
    {
        using var app = ApiFoundationTests.Factory(fixture.ConnectionString); using var client = app.CreateClient();
        foreach (var password in new[] { "short", new string('a', 73), new string('я', 37), "abcdefgh\0" })
        {
            var request = Request(); request.Password = password;
            var response = await client.PostAsJsonAsync("/api/auth/register", request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("Password", (await response.Content.ReadFromJsonAsync<ApiProblem>())!.Errors!.Keys);
        }
        var invalid = Request(); invalid.Email = "bad"; invalid.Name = " ";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/register", invalid)).StatusCode);
        var valid = Request(); valid.Password = new string('я', 36); // Exactly 72 UTF-8 bytes.
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/auth/register", valid)).StatusCode);
    }

    [SqlFact]
    public async Task MissingInvalidExpiredOrMisaddressedTokensAreRejected()
    {
        using var app = ApiFoundationTests.Factory(fixture.ConnectionString); using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        var now = DateTime.UtcNow;
        string Token(string issuer = "KanbanBoard.Api", string audience = "KanbanBoard.Desktop", string key = Key,
            DateTime? expires = null, DateTime? starts = null, string? subject = null, string algorithm = SecurityAlgorithms.HmacSha256) =>
            new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(issuer, audience,
                [new Claim("sub", subject ?? Guid.NewGuid().ToString())], starts ?? now.AddMinutes(-5), expires ?? now.AddHours(1),
                new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), algorithm)));
        foreach (var token in new[] { "malformed", Token(key: new string('x', 64)), Token(issuer: "wrong"), Token(audience: "wrong"),
            Token(expires: now.AddSeconds(-1)), Token(starts: now.AddMinutes(5)), Token(subject: "bad"), Token(subject: Guid.Empty.ToString()),
            new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("KanbanBoard.Api", "KanbanBoard.Desktop", [new Claim("sub", Guid.NewGuid().ToString())], now.AddMinutes(-5), now.AddHours(1))), Token(algorithm: SecurityAlgorithms.HmacSha384) })
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var response = await client.GetAsync("/api/auth/me");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
            Assert.Equal("unauthorized", (await response.Content.ReadFromJsonAsync<ApiProblem>())!.Code);
        }
    }

    [SqlFact]
    public async Task TokenCannotReadADeletedAccount()
    {
        using var app = ApiFoundationTests.Factory(fixture.ConnectionString); using var client = app.CreateClient();
        var auth = (await (await client.PostAsJsonAsync("/api/auth/register", Request())).Content.ReadFromJsonAsync<AuthResponse>())!;
        await using var db = fixture.CreateContext();
        await db.Users.Where(x => x.Id == auth.User.Id).ExecuteDeleteAsync();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }
}

public sealed class JwtConfigurationTests
{
    [Fact]
    public void MissingShortOrPlaceholderSecretFailsConfiguration()
    {
        foreach (var key in new[] { "", "short", "replace-with-a-random-key-of-at-least-32-bytes" })
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Key"] = key }).Build();
            Assert.Throws<InvalidOperationException>(() => JwtSettings.Load(config, false));
        }
    }
}
