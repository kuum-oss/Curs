using KanbanApi.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace KanbanApi.Tests;

internal sealed class AccountDeletionTestClock : TimeProvider
{
    private DateTimeOffset now=DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow()=>now;
    public void Advance(TimeSpan value)=>now+=value;
    public WebApplicationFactory<Program> Factory(string connection)=>ApiFoundationTests.Factory(connection).WithWebHostBuilder(b=>b.ConfigureServices(services=>
    {
        services.AddSingleton<TimeProvider>(this);
        // Only the recovery window advances; bearer token timestamps stay real.
        services.AddSingleton<JwtService>(sp=>new(sp.GetRequiredService<JwtSettings>(),TimeProvider.System));
    }));
}
