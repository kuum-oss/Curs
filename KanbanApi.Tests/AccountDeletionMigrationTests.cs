using KanbanApi.Domain;
using KanbanApi.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace KanbanApi.Tests;

[Collection("sql")]
public sealed class AccountDeletionMigrationTests(SqlFixture fixture)
{
    [SqlFact]
    public async Task UpgradeKeepsExistingHistoryAndDowngradeCannotInventDeletedAuthors()
    {
        var connection=new SqlConnectionStringBuilder(fixture.ConnectionString){InitialCatalog="KanbanDeleteUpgrade_"+Guid.NewGuid().ToString("N")}.ConnectionString;
        await using var db=new KanbanDbContext(new DbContextOptionsBuilder<KanbanDbContext>().UseSqlServer(connection).Options);
        const string previous="20261015172810_SketchParityProfiles";
        try
        {
            await db.GetService<IMigrator>().MigrateAsync(previous);
            var user=new User{Email="migration@example.test",NormalizedEmail="MIGRATION@EXAMPLE.TEST",Name="Історія",PasswordHash="preserved"};var board=new Board{Name="Дошка",OwnerId=user.Id};var col=new BoardColumn{BoardId=board.Id,Name="План"};var card=new Card{ColumnId=col.Id,CreatedById=user.Id,Title="Збережена картка"};var comment=new Comment{CardId=card.Id,AuthorId=user.Id,Text="Збережений коментар"};
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Users(Id,Email,NormalizedEmail,Name,PasswordHash,SessionVersion,CreatedAtUtc) VALUES({user.Id},{user.Email},{user.NormalizedEmail},{user.Name},{user.PasswordHash},{0},{user.CreatedAtUtc})");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Boards(Id,Name,OwnerId,IsArchived,CreatedAtUtc,UpdatedAtUtc) VALUES({board.Id},{board.Name},{user.Id},{false},{board.CreatedAtUtc},{board.UpdatedAtUtc})");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Columns(Id,BoardId,Name,Position) VALUES({col.Id},{board.Id},{col.Name},{0})");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Cards(Id,ColumnId,Title,CreatedById,IsArchived,Position,Priority,CreatedAtUtc,UpdatedAtUtc) VALUES({card.Id},{col.Id},{card.Title},{user.Id},{false},{0},{1},{card.CreatedAtUtc},{card.UpdatedAtUtc})");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Comments(Id,CardId,AuthorId,Text,CreatedAtUtc) VALUES({comment.Id},{card.Id},{user.Id},{comment.Text},{comment.CreatedAtUtc})");
            await db.Database.MigrateAsync();db.ChangeTracker.Clear();Assert.Equal(user.Id,(await db.Cards.FindAsync(card.Id))!.CreatedById);Assert.Equal(user.Id,(await db.Comments.FindAsync(comment.Id))!.AuthorId);Assert.Equal("Збережений коментар",(await db.Comments.FindAsync(comment.Id))!.Text);
            (await db.Cards.FindAsync(card.Id))!.CreatedById=null;(await db.Comments.FindAsync(comment.Id))!.AuthorId=null;await db.SaveChangesAsync();
            var error=await Assert.ThrowsAsync<SqlException>(()=>db.GetService<IMigrator>().MigrateAsync(previous));Assert.Equal(50001,error.Number);
            Assert.Contains(await db.Database.GetAppliedMigrationsAsync(),m=>m.EndsWith("AccountDeletionAuthors"));db.ChangeTracker.Clear();Assert.Null(await db.Comments.Where(c=>c.Id==comment.Id).Select(c=>c.AuthorId).SingleAsync());Assert.Equal("Збережена картка",await db.Cards.Where(c=>c.Id==card.Id).Select(c=>c.Title).SingleAsync());
        }
        finally{await db.Database.EnsureDeletedAsync();}
    }
}
