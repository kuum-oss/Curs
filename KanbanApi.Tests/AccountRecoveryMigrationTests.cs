using KanbanApi.Domain;
using KanbanApi.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace KanbanApi.Tests;

[Collection("sql")]
public sealed class AccountRecoveryMigrationTests(SqlFixture fixture)
{
    [SqlFact]
    public async Task PendingWindowCannotBeDroppedAndDatabaseRequiresExactlySevenDays()
    {
        var connection=new SqlConnectionStringBuilder(fixture.ConnectionString){InitialCatalog="KanbanRecoveryMigration_"+Guid.NewGuid().ToString("N")}.ConnectionString;
        await using var db=new KanbanDbContext(new DbContextOptionsBuilder<KanbanDbContext>().UseSqlServer(connection).Options);
        try
        {
            await db.Database.MigrateAsync();var now=DateTime.UtcNow;var user=new User{Email="window@example.test",NormalizedEmail="WINDOW@EXAMPLE.TEST",Name="Вікно",PasswordHash="unused",DeletionRequestedAtUtc=now,DeletionDueAtUtc=now.AddDays(7)};db.Add(user);await db.SaveChangesAsync();
            var error=await Assert.ThrowsAsync<SqlException>(()=>db.GetService<IMigrator>().MigrateAsync("20261017104945_AccountDeletionAuthors"));Assert.Equal(50002,error.Number);db.ChangeTracker.Clear();Assert.Equal(now.AddDays(7),(await db.Users.FindAsync(user.Id))!.DeletionDueAtUtc);
            (await db.Users.FindAsync(user.Id))!.DeletionDueAtUtc=now.AddDays(6);await Assert.ThrowsAsync<DbUpdateException>(()=>db.SaveChangesAsync());
        }
        finally{await db.Database.EnsureDeletedAsync();}
    }
}
