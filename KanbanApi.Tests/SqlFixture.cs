using KanbanApi.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace KanbanApi.Tests;

public sealed class SqlFactAttribute : FactAttribute
{
    public SqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KANBAN_TEST_CONNECTION")))
            Skip = "Set KANBAN_TEST_CONNECTION to run the real MSSQL integration checks.";
    }
}

[CollectionDefinition("sql")]
public sealed class SqlCollection : ICollectionFixture<SqlFixture>;

public sealed class SqlFixture : IAsyncLifetime
{
    private readonly bool enabled = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KANBAN_TEST_CONNECTION"));
    public string ConnectionString { get; } = new SqlConnectionStringBuilder(
        Environment.GetEnvironmentVariable("KANBAN_TEST_CONNECTION") ?? "Server=localhost;Integrated Security=True")
    {
        InitialCatalog = "KanbanTest_" + Guid.NewGuid().ToString("N"),
    }.ConnectionString;

    public KanbanDbContext CreateContext() => new(new DbContextOptionsBuilder<KanbanDbContext>()
        .UseSqlServer(ConnectionString, sql => sql.UseCompatibilityLevel(160)).Options);

    public async Task InitializeAsync()
    {
        if (!enabled) return;
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (!enabled) return;
        await using var db = CreateContext();
        await db.Database.EnsureDeletedAsync();
    }
}
