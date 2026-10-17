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
            var user=new User{Email="migration@example.test",NormalizedEmail="MIGRATION@EXAMPLE.TEST",Name="Історія",PasswordHash="preserved"};var board=new Board{Name="Дошка",OwnerId=user.Id};var col=new BoardColumn{BoardId=board.Id,Name="План"};var card=new Card{ColumnId=col.Id,CreatedById=user.Id,Title="Збережена картка"};var comment=new Comment{CardId=card.Id,AuthorId=user.Id,Text="Збережений коментар"};db.AddRange(user,board,col,card,comment);await db.SaveChangesAsync();
            await db.Database.MigrateAsync();db.ChangeTracker.Clear();Assert.Equal(user.Id,(await db.Cards.FindAsync(card.Id))!.CreatedById);Assert.Equal(user.Id,(await db.Comments.FindAsync(comment.Id))!.AuthorId);Assert.Equal("Збережений коментар",(await db.Comments.FindAsync(comment.Id))!.Text);
            (await db.Cards.FindAsync(card.Id))!.CreatedById=null;(await db.Comments.FindAsync(comment.Id))!.AuthorId=null;await db.SaveChangesAsync();
            var error=await Assert.ThrowsAsync<SqlException>(()=>db.GetService<IMigrator>().MigrateAsync(previous));Assert.Equal(50001,error.Number);
            Assert.Contains("AccountDeletionAuthors",(await db.Database.GetAppliedMigrationsAsync()).Last());db.ChangeTracker.Clear();Assert.Null((await db.Comments.FindAsync(comment.Id))!.AuthorId);Assert.Equal("Збережена картка",(await db.Cards.FindAsync(card.Id))!.Title);
        }
        finally{await db.Database.EnsureDeletedAsync();}
    }
}
