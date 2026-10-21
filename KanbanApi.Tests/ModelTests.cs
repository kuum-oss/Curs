using KanbanApi.Domain;
using KanbanApi.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace KanbanApi.Tests;

public sealed class ModelTests
{
    [Fact]
    public void RelationalModelHasFourteenTablesAndOneDirectHistoryCascade()
    {
        using var db = new KanbanDbContext(new DbContextOptionsBuilder<KanbanDbContext>()
            .UseSqlServer("Server=localhost;Database=model-only;Integrated Security=True").Options);
        var types = db.Model.GetEntityTypes().ToArray();
        Assert.Equal(14, types.Length);
        Assert.All(types.SelectMany(x => x.GetForeignKeys()), fk => Assert.Equal(fk.DeclaringEntityType.ClrType == typeof(BoardActivity) ? DeleteBehavior.Cascade : DeleteBehavior.NoAction, fk.DeleteBehavior));
        foreach (var type in new[] { typeof(Board), typeof(BoardColumn), typeof(Card) })
        {
            var version = db.Model.FindEntityType(type)!.FindProperty("RowVersion")!;
            Assert.True(version.IsConcurrencyToken);
            Assert.Equal("rowversion", version.GetColumnType());
        }
        Assert.True(db.Model.FindEntityType(typeof(CardImage))!.GetIndexes().Single(x => x.Properties[0].Name == "CardId").IsUnique);
    }
}
