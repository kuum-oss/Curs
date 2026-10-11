using KanbanApi.Domain;
using KanbanApi.Infrastructure;
using KanbanContracts;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace KanbanApi.Tests;

[Collection("sql")]
public sealed class SqlSchemaTests(SqlFixture fixture)
{
    private static async Task<(User User, Board Board, BoardColumn Column, Card Card, Tag Tag)> Seed(KanbanDbContext db)
    {
        var user = new User { Email = Guid.NewGuid()+"@example.com", NormalizedEmail = Guid.NewGuid()+"@EXAMPLE.COM", Name = "Олена", PasswordHash = "test-only-hash" };
        var board = new Board { Name = "Курсова" };
        var column = new BoardColumn { BoardId = board.Id, Name = "У роботі" };
        var card = new Card { ColumnId = column.Id, Title = "Перевірка", CreatedById = user.Id, AssigneeId = user.Id, DueDate = new DateOnly(2026,10,11) };
        var tag = new Tag { BoardId = board.Id, Name = "API", NormalizedName = "API" };
        db.AddRange(user, board, column, card, tag,
            new BoardMember { BoardId = board.Id, UserId = user.Id, Role = BoardRole.Admin },
            new CardTag { CardId = card.Id, TagId = tag.Id },
            new CardImage { CardId = card.Id, ContentType = "image/png", ThumbnailContentType = "image/png", OriginalName = "probe.png", SizeBytes = 4, OriginalData = [1,2,3,4], ThumbnailData = [1] },
            new Comment { CardId = card.Id, AuthorId = user.Id, Text = "Коментар" });
        await db.SaveChangesAsync();
        return (user, board, column, card, tag);
    }

    [SqlFact]
    public async Task MigrationCreatesExactlyNineTablesAndIsIdempotent()
    {
        await using var db = fixture.CreateContext();
        var before = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        await db.Database.MigrateAsync();
        Assert.Single(before);
        Assert.Equal(before, (await db.Database.GetAppliedMigrationsAsync()).ToArray());
        var tables = await db.Database.SqlQueryRaw<string>("SELECT [name] AS [Value] FROM sys.tables WHERE [name] <> '__EFMigrationsHistory'").ToListAsync();
        Assert.Equal(new[] { "BoardMembers", "Boards", "CardImages", "CardTags", "Cards", "Columns", "Comments", "Tags", "Users" }.Order(StringComparer.Ordinal).ToArray(), tables.Order(StringComparer.Ordinal).ToArray());
    }

    [SqlFact]
    public async Task CompleteGraphRoundTripsUnicodeDatesBinaryAndVersion()
    {
        await using var db = fixture.CreateContext();
        var graph = await Seed(db);
        db.ChangeTracker.Clear();
        var card = await db.Cards.SingleAsync(x => x.Id == graph.Card.Id);
        Assert.Equal("Перевірка", card.Title);
        Assert.Equal(new DateOnly(2026,10,11), card.DueDate);
        Assert.Equal(8, card.RowVersion.Length);
        Assert.Equal(new byte[] {1,2,3,4}, (await db.CardImages.SingleAsync(x => x.CardId == card.Id)).OriginalData);
        Assert.Equal("Коментар", (await db.Comments.SingleAsync(x => x.CardId == card.Id)).Text);
    }

    [SqlFact]
    public async Task DuplicateEmailIsRejectedWithoutCaseSensitivity()
    {
        await using var db = fixture.CreateContext();
        var graph = await Seed(db);
        db.Users.Add(new User { Email = "other@example.com", NormalizedEmail = graph.User.NormalizedEmail.ToLowerInvariant(), Name = "Інший", PasswordHash = "test-only" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [SqlFact]
    public async Task ABoardCannotHaveTwoAdministrators()
    {
        await using var db = fixture.CreateContext();
        var graph = await Seed(db);
        var other = new User { Email = "other@example.com", NormalizedEmail = Guid.NewGuid()+"@EXAMPLE.COM", Name = "Інший", PasswordHash = "test-only" };
        db.AddRange(other, new BoardMember { BoardId = graph.Board.Id, UserId = other.Id, Role = BoardRole.Admin });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [SqlFact]
    public async Task CheckConstraintsRejectBadPriorityPositionAndImageSize()
    {
        await using var db = fixture.CreateContext();
        var graph = await Seed(db);
        graph.Card.Priority = (CardPriority)255;
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        var column = await db.Columns.SingleAsync(x => x.Id == graph.Column.Id);
        column.Position = -1;
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        var image = await db.CardImages.SingleAsync(x => x.CardId == graph.Card.Id);
        image.SizeBytes = FieldLimits.ImageBytes + 1;
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [SqlFact]
    public async Task ForeignKeysAndSingleCoverAreEnforced()
    {
        await using var db = fixture.CreateContext();
        var graph = await Seed(db);
        db.Cards.Add(new Card { ColumnId = Guid.NewGuid(), CreatedById = graph.User.Id, Title = "Wrong column" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        db.CardImages.Add(new CardImage { CardId = graph.Card.Id, ContentType = "image/png", ThumbnailContentType = "image/png", OriginalName = "second.png", SizeBytes = 1, OriginalData = [1], ThumbnailData = [1] });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [SqlFact]
    public async Task StaleRowVersionCannotOverwriteAnotherEditor()
    {
        await using var first = fixture.CreateContext();
        var graph = await Seed(first);
        await using var second = fixture.CreateContext();
        var stale = await second.Cards.SingleAsync(x => x.Id == graph.Card.Id);
        graph.Card.Title = "Перша зміна";
        await first.SaveChangesAsync();
        stale.Title = "Втрачена зміна";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    [SqlFact]
    public async Task ParentDeletionDoesNotSilentlyCascadeAndRollbackPreservesData()
    {
        await using var db = fixture.CreateContext();
        var graph = await Seed(db);
        var exception = await Assert.ThrowsAsync<SqlException>(() => db.Boards.Where(x => x.Id == graph.Board.Id).ExecuteDeleteAsync());
        Assert.Equal(547, exception.Number);
        db.ChangeTracker.Clear();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await db.Comments.Where(x => x.CardId == graph.Card.Id).ExecuteDeleteAsync();
            await transaction.RollbackAsync();
        }
        Assert.True(await db.Comments.AnyAsync(x => x.CardId == graph.Card.Id));
    }
}
