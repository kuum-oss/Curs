using System.Net;
using System.Net.Http.Json;
using KanbanApi.Application;
using KanbanContracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KanbanApi.Tests;

public sealed class ApiFoundationTests
{
    internal static WebApplicationFactory<Program> Factory(string connection) => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = connection,
                ["Database:ApplyMigrationsOnStartup"] = "false",
                ["Jwt:Key"] = "test-only-key-for-kanban-api-at-least-32-bytes-long-enough-for-hs384",
            }));
            builder.ConfigureServices(services => services.AddControllers().AddApplicationPart(typeof(ProbeController).Assembly));
        });

    [Fact]
    public async Task LivenessDoesNotDependOnDatabaseAndMissingRoutesAreProblemDetails()
    {
        using var app = Factory("Server=127.0.0.1,1;Database=missing;User Id=probe;Password=not-a-secret;Connect Timeout=1");
        using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        var response = await client.GetAsync("/api/missing");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", (await response.Content.ReadFromJsonAsync<ApiProblem>())!.Code);
    }

    [Fact]
    public async Task PublicErrorAndInternalErrorHaveStableCodes()
    {
        using var app = Factory("Server=127.0.0.1,1;Database=missing;Integrated Security=True");
        using var client = app.CreateClient();
        var response = await client.GetAsync("/__test/unknown-error");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("sensitive-database-message", text);
        Assert.Equal("internal_error", (await response.Content.ReadFromJsonAsync<ApiProblem>())!.Code);
        response = await client.GetAsync("/__test/known-error");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("probe_conflict", (await response.Content.ReadFromJsonAsync<ApiProblem>())!.Code);
        response = await client.PostAsJsonAsync("/__test/validate", new { Name = "" });
        var validation = await response.Content.ReadFromJsonAsync<ApiProblem>();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", validation!.Code);
        Assert.Contains("Name", validation.Errors!.Keys);
    }
}

[Collection("sql")]
public sealed class ApiReadinessTests(SqlFixture fixture)
{
    [SqlFact]
    public async Task ReadyOnlyWhenRealDatabaseIsMigrated()
    {
        using var app = ApiFoundationTests.Factory(fixture.ConnectionString);
        using var client = app.CreateClient();
        var response = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("ready", await response.Content.ReadAsStringAsync());
    }

    [SqlFact]
    public async Task MissingDatabaseIsNotReadyAndConnectionIsNotExposed()
    {
        var connection = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(fixture.ConnectionString)
        { InitialCatalog = "KanbanMissing_" + Guid.NewGuid().ToString("N"), ConnectTimeout = 1 }.ConnectionString;
        using var app = ApiFoundationTests.Factory(connection);
        using var client = app.CreateClient();
        var response = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain("Password", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }
}

// This controller exists only in the test assembly; it is not included in production discovery.
[ApiController]
[Route("__test")]
public sealed class ProbeController : ControllerBase
{
    [HttpGet("unknown-error")]
    public IActionResult Unknown() => throw new InvalidOperationException("sensitive-database-message");
    [HttpGet("known-error")]
    public IActionResult Known() => throw new ApiException(409, "probe_conflict", "Конфлікт перевірки.");
    [HttpPost("validate")]
    public IActionResult Validate([FromBody] ProbeRequest request) => Ok();
}
public sealed class ProbeRequest
{
    [System.ComponentModel.DataAnnotations.Required]
    public string Name { get; set; } = string.Empty;
}
