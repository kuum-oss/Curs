using KanbanApi.Application;
using KanbanApi.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);
if (builder.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("Default")))
{
    var local = DatabaseConfiguration.ReadLocalConnection();
    if (local is not null) builder.Configuration["ConnectionStrings:Default"] = local;
}
builder.Services.AddSingleton(sp => JwtSettings.Load(sp.GetRequiredService<IConfiguration>(), builder.Environment.IsDevelopment()));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<JwtService>();
builder.Services.AddSingleton<PasswordService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<BoardAccess>();
builder.Services.AddScoped<BoardService>();
builder.Services.AddScoped<ContentService>();
builder.Services.AddScoped<OrderingService>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<JwtSettings>((options, settings) => JwtService.Configure(options, settings));
builder.Services.AddAuthorization();
builder.Services.AddDbContext<KanbanDbContext>(options =>
{
    var connection = builder.Configuration.GetConnectionString("Default")
        ?? throw new InvalidOperationException("Configure ConnectionStrings__Default before starting the API.");
    options.UseSqlServer(connection, sql => sql.UseCompatibilityLevel(160)
        .EnableRetryOnFailure(3, TimeSpan.FromSeconds(2), null).CommandTimeout(10));
});
builder.Services.AddControllers().ConfigureApiBehaviorOptions(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var problem = new ValidationProblemDetails(context.ModelState)
        {
            Status = 400, Title = "Дані запиту некоректні.",
        };
        problem.Extensions["code"] = "validation_failed";
        problem.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
        return new BadRequestObjectResult(problem) { ContentTypes = { "application/problem+json" } };
    };
});
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
{
    context.ProblemDetails.Extensions.TryAdd("traceId", context.HttpContext.TraceIdentifier);
    context.ProblemDetails.Extensions.TryAdd("code", context.ProblemDetails.Status switch
    {
        404 => "not_found", 401 => "unauthorized", 403 => "forbidden", _ => "request_failed",
    });
});
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);

var app = builder.Build();
// Validate secrets after the final configuration (including test host overrides) has been composed.
_ = app.Services.GetRequiredService<JwtSettings>();
if (app.Configuration.GetValue("Database:ApplyMigrationsOnStartup", true))
{
    await using var scope = app.Services.CreateAsyncScope();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    await scope.ServiceProvider.GetRequiredService<KanbanDbContext>().Database.MigrateAsync(timeout.Token);
}
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready"),
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new { status = report.Status == HealthStatus.Healthy ? "ready" : "not_ready" });
    },
});
app.Run();

public partial class Program;
