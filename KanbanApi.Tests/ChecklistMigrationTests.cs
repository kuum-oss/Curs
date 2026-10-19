using KanbanApi.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;
namespace KanbanApi.Tests;
[Collection("sql")]
public sealed class ChecklistMigrationTests
{
    [SqlFact]
    public async Task DowngradeGuardsExistingItemsAndEmptyUpgradeKeepsOriginalCards()
    {
        var f=new SqlFixture();await f.InitializeAsync();
        try
        {
            await using var db=f.CreateContext();var b=new Board{Name="Початкова дошка"};var col=new BoardColumn{BoardId=b.Id,Name="План"};var card=new Card{ColumnId=col.Id,Title="Зберегти"};db.AddRange(b,col,card,new ChecklistItem{Id=Guid.NewGuid(),CardId=card.Id,Title="Зберегти пункт"});await db.SaveChangesAsync();var version=Convert.ToBase64String(card.RowVersion);
            var e=await Assert.ThrowsAsync<SqlException>(()=>db.GetService<IMigrator>().MigrateAsync("20261017115129_PrivateChats"));Assert.Equal(50006,e.Number);Assert.True(await db.ChecklistItems.AnyAsync());await db.ChecklistItems.ExecuteDeleteAsync();await db.GetService<IMigrator>().MigrateAsync("20261017115129_PrivateChats");await db.Database.MigrateAsync();db.ChangeTracker.Clear();var saved=await db.Cards.SingleAsync(c=>c.Id==card.Id);Assert.Equal("Зберегти",saved.Title);Assert.Equal(version,Convert.ToBase64String(saved.RowVersion));
        }
        finally{await f.DisposeAsync();}
    }
}
