using KanbanApi.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace KanbanApi.Tests;

[Collection("sql")]
public sealed class SketchMigrationTests(SqlFixture fixture)
{
    [SqlFact]
    public async Task UpgradePreservesExistingBoardAndAssignsItsOriginalAdministratorAsOwner()
    {
        var connection=new SqlConnectionStringBuilder(fixture.ConnectionString){InitialCatalog="KanbanUpgrade_"+Guid.NewGuid().ToString("N")}.ConnectionString;
        await using var db=new KanbanDbContext(new DbContextOptionsBuilder<KanbanDbContext>().UseSqlServer(connection).Options);
        try
        {
            await db.GetService<IMigrator>().MigrateAsync("20261011201833_InitialCreate");
            var user=Guid.NewGuid();var board=Guid.NewGuid();var now=DateTime.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Users(Id,Email,NormalizedEmail,Name,PasswordHash,CreatedAtUtc) VALUES({user},{"old@example.test"},{"OLD@EXAMPLE.TEST"},{"Старий профіль"},{"preserved-hash"},{now})");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Boards(Id,Name,Description,CreatedAtUtc,UpdatedAtUtc) VALUES({board},{"Існуюча дошка"},{"Опис до оновлення"},{now},{now})");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO BoardMembers(BoardId,UserId,Role,CanCreateCards,CanDeleteCards,JoinedAtUtc) VALUES({board},{user},{0},{true},{true},{now})");
            await db.Database.MigrateAsync();
            var saved=await db.Boards.SingleAsync(x=>x.Id==board);Assert.Equal(user,saved.OwnerId);Assert.Equal("Опис до оновлення",saved.Description);Assert.False(saved.IsArchived);Assert.Null(saved.CoverOriginalData);
            var profile=await db.Users.SingleAsync(x=>x.Id==user);Assert.Equal("preserved-hash",profile.PasswordHash);Assert.Equal(8,profile.RowVersion.Length);Assert.Equal(0,profile.SessionVersion);Assert.Null(profile.AvatarData);
            Assert.Single(await db.BoardMembers.Where(x=>x.BoardId==board).ToListAsync());
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }
}
