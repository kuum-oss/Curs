using KanbanApi.Domain;
using KanbanContracts;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;
namespace KanbanApi.Tests;
public sealed class NotificationMigrationTests
{
    [SqlFact]
    public async Task DowngradeGuardPreservesHistoryAndEmptyUpgradePreservesExistingUsers()
    {
        var f=new SqlFixture();await f.InitializeAsync();try
        {
            await using var db=f.CreateContext();var u=new User{Email="migration@example.test",NormalizedEmail="MIGRATION@EXAMPLE.TEST",Name="Зберегти",PasswordHash="unused"};db.AddRange(u,new Notification{UserId=u.Id,Kind=NotificationKind.Assignment,EventKey="test",CreatedAtUtc=DateTime.UtcNow});await db.SaveChangesAsync();
            var ex=await Assert.ThrowsAsync<SqlException>(()=>db.GetService<IMigrator>().MigrateAsync("20261019182649_CardChecklists"));Assert.Equal(50007,ex.Number);Assert.True(await db.Notifications.AnyAsync());await db.Notifications.ExecuteDeleteAsync();await db.GetService<IMigrator>().MigrateAsync("20261019182649_CardChecklists");await db.Database.MigrateAsync();Assert.Equal("Зберегти",(await db.Users.AsNoTracking().SingleAsync()).Name);
        }finally{await f.DisposeAsync();}
    }
}
