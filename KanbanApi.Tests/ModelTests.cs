using KanbanApi.Domain;
using KanbanApi.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace KanbanApi.Tests;

public sealed class ModelTests
{
    [Fact]
    public void RelationalModelHasTwelveTablesAndNoCascadePaths()
    {
        using var db = new KanbanDbContext(new DbContextOptionsBuilder<KanbanDbContext>()
            .UseSqlServer("Server=localhost;Database=model-only;Integrated Security=True").Options);
        var types = db.Model.GetEntityTypes().ToArray();
        Assert.Equal(12, types.Length);
        Assert.All(types.SelectMany(x => x.GetForeignKeys()), fk => Assert.Equal(DeleteBehavior.NoAction, fk.DeleteBehavior));
        foreach (var type in new[] { typeof(Board), typeof(BoardColumn), typeof(Card) })
        {
            var version = db.Model.FindEntityType(type)!.FindProperty("RowVersion")!;
            Assert.True(version.IsConcurrencyToken);
            Assert.Equal("rowversion", version.GetColumnType());
        }
        Assert.True(db.Model.FindEntityType(typeof(CardImage))!.GetIndexes().Single(x => x.Properties[0].Name == "CardId").IsUnique);
    }
}
