using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace KanbanApi.Infrastructure;

public static class DatabaseConfiguration
{
    // Only called for local development and design-time tools. Compose supplies the full connection string.
    public static string? ReadLocalConnection()
    {
        var configured = Environment.GetEnvironmentVariable("ConnectionStrings__Default");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var password = ReadLocalValue("MSSQL_SA_PASSWORD");
        if (string.IsNullOrWhiteSpace(password)) return null;
        return new SqlConnectionStringBuilder
        {
            DataSource = "localhost,3038", InitialCatalog = "KanbanDB", UserID = "sa",
            Password = password, TrustServerCertificate = true, ConnectTimeout = 5,
        }.ConnectionString;
    }

    public static string? ReadLocalValue(string key)
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Curs.sln")))
            {
                var file = Path.Combine(directory.FullName, ".env");
                if (!File.Exists(file)) return null;
                var prefix = key + "=";
                var line = File.ReadLines(file).FirstOrDefault(x => x.StartsWith(prefix, StringComparison.Ordinal));
                return line?[prefix.Length..].Trim().Trim('"', '\'');
            }
            directory = directory.Parent;
        }
        return null;
    }
}

public sealed class KanbanDbContextFactory : IDesignTimeDbContextFactory<KanbanDbContext>
{
    public KanbanDbContext CreateDbContext(string[] args)
    {
        var connection = DatabaseConfiguration.ReadLocalConnection()
            ?? throw new InvalidOperationException("Set ConnectionStrings__Default or prepare the local .env file.");
        return new KanbanDbContext(new DbContextOptionsBuilder<KanbanDbContext>()
            .UseSqlServer(connection, sql => sql.UseCompatibilityLevel(160)).Options);
    }
}
