using KanbanApi.Domain;
using KanbanContracts;
using Microsoft.EntityFrameworkCore;

namespace KanbanApi.Infrastructure;

public sealed class KanbanDbContext(DbContextOptions<KanbanDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Board> Boards => Set<Board>();
    public DbSet<BoardMember> BoardMembers => Set<BoardMember>();
    public DbSet<BoardColumn> Columns => Set<BoardColumn>();
    public DbSet<Card> Cards => Set<Card>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<CardTag> CardTags => Set<CardTag>();
    public DbSet<CardImage> CardImages => Set<CardImage>();
    public DbSet<Comment> Comments => Set<Comment>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        var user = model.Entity<User>();
        user.ToTable("Users", t => t.HasCheckConstraint("CK_Users_DeletionWindow", "([DeletionRequestedAtUtc] IS NULL AND [DeletionDueAtUtc] IS NULL) OR ([DeletionRequestedAtUtc] IS NOT NULL AND [DeletionDueAtUtc] IS NOT NULL AND [DeletionDueAtUtc] = DATEADD(day, 7, [DeletionRequestedAtUtc]))"));
        user.HasIndex(x => x.DeletionDueAtUtc);
        user.HasKey(x => x.Id);
        user.Property(x => x.RowVersion).IsRowVersion();
        user.Property(x => x.AvatarData).HasColumnType("varbinary(max)");
        user.Property(x => x.Email).HasMaxLength(FieldLimits.Email).IsRequired();
        user.Property(x => x.NormalizedEmail).HasMaxLength(FieldLimits.Email)
            .UseCollation("Latin1_General_100_CI_AS_SC").IsRequired();
        user.Property(x => x.Name).HasMaxLength(FieldLimits.UserName).IsRequired();
        user.Property(x => x.PasswordHash).HasMaxLength(100).IsUnicode(false).IsRequired();
        user.HasIndex(x => x.NormalizedEmail).IsUnique();

        var board = model.Entity<Board>();
        board.ToTable("Boards");
        board.HasKey(x => x.Id);
        board.HasOne<User>().WithMany().HasForeignKey(x => x.OwnerId);
        board.Property(x => x.CoverOriginalData).HasColumnType("varbinary(max)");
        board.Property(x => x.CoverThumbnailData).HasColumnType("varbinary(max)");
        board.Property(x => x.CoverContentType).HasMaxLength(50).IsUnicode(false);
        board.Property(x => x.Name).HasMaxLength(FieldLimits.BoardName).IsRequired();
        board.Property(x => x.Description).HasMaxLength(FieldLimits.BoardDescription);
        board.Property(x => x.RowVersion).IsRowVersion();

        var member = model.Entity<BoardMember>();
        member.ToTable("BoardMembers", t => t.HasCheckConstraint("CK_BoardMembers_Role", "[Role] IN (0, 1)"));
        member.HasKey(x => new { x.BoardId, x.UserId });
        member.HasOne<Board>().WithMany().HasForeignKey(x => x.BoardId);
        member.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
        member.HasIndex(x => new { x.UserId, x.BoardId });


        var column = model.Entity<BoardColumn>();
        column.ToTable("Columns", t => t.HasCheckConstraint("CK_Columns_Position", "[Position] >= 0"));
        column.HasKey(x => x.Id);
        column.Property(x => x.Name).HasMaxLength(FieldLimits.ColumnName).IsRequired();
        column.Property(x => x.RowVersion).IsRowVersion();
        column.HasOne<Board>().WithMany().HasForeignKey(x => x.BoardId);
        column.HasIndex(x => new { x.BoardId, x.Position });

        var card = model.Entity<Card>();
        card.ToTable("Cards", t =>
        {
            t.HasCheckConstraint("CK_Cards_Position", "[Position] >= 0");
            t.HasCheckConstraint("CK_Cards_Priority", "[Priority] IN (0, 1, 2, 3)");
            t.HasCheckConstraint("CK_Cards_Description", "[Description] IS NULL OR DATALENGTH([Description]) <= 20000");
        });
        card.HasKey(x => x.Id);
        card.Property(x => x.Title).HasMaxLength(FieldLimits.CardTitle).IsRequired();
        card.Property(x => x.Description).HasColumnType("nvarchar(max)");
        card.Property(x => x.DueDate).HasColumnType("date");
        card.Property(x => x.RowVersion).IsRowVersion();
        card.HasOne<BoardColumn>().WithMany().HasForeignKey(x => x.ColumnId);
        card.HasOne<User>().WithMany().HasForeignKey(x => x.AssigneeId);
        card.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedById);
        card.HasIndex(x => new { x.ColumnId, x.Position });
        card.HasIndex(x => x.DueDate);
        card.HasIndex(x => x.Priority);

        var tag = model.Entity<Tag>();
        tag.ToTable("Tags");
        tag.HasKey(x => x.Id);
        tag.Property(x => x.Name).HasMaxLength(FieldLimits.TagName).IsRequired();
        tag.Property(x => x.NormalizedName).HasMaxLength(FieldLimits.TagName)
            .UseCollation("Latin1_General_100_CI_AS_SC").IsRequired();
        tag.Property(x => x.Color).HasMaxLength(7).IsUnicode(false).IsRequired();
        tag.HasOne<Board>().WithMany().HasForeignKey(x => x.BoardId);
        tag.HasIndex(x => new { x.BoardId, x.NormalizedName }).IsUnique();

        var cardTag = model.Entity<CardTag>();
        cardTag.ToTable("CardTags");
        cardTag.HasKey(x => new { x.CardId, x.TagId });
        cardTag.HasOne<Card>().WithMany().HasForeignKey(x => x.CardId);
        cardTag.HasOne<Tag>().WithMany().HasForeignKey(x => x.TagId);

        var image = model.Entity<CardImage>();
        image.ToTable("CardImages", t => t.HasCheckConstraint("CK_CardImages_Size",
            $"[SizeBytes] > 0 AND [SizeBytes] <= {FieldLimits.ImageBytes} AND [SizeBytes] = DATALENGTH([OriginalData])"));
        image.HasKey(x => x.Id);
        image.Property(x => x.ContentType).HasMaxLength(50).IsUnicode(false).IsRequired();
        image.Property(x => x.ThumbnailContentType).HasMaxLength(50).IsUnicode(false).IsRequired();
        image.Property(x => x.OriginalName).HasMaxLength(255).IsRequired();
        image.Property(x => x.OriginalData).HasColumnType("varbinary(max)").IsRequired();
        image.Property(x => x.ThumbnailData).HasColumnType("varbinary(max)").IsRequired();
        image.HasOne<Card>().WithMany().HasForeignKey(x => x.CardId);
        image.HasIndex(x => x.CardId).IsUnique();

        var comment = model.Entity<Comment>();
        comment.ToTable("Comments");
        comment.HasKey(x => x.Id);
        comment.Property(x => x.RowVersion).IsRowVersion();
        comment.Property(x => x.Text).HasMaxLength(FieldLimits.Comment).IsRequired();
        comment.HasOne<Card>().WithMany().HasForeignKey(x => x.CardId);
        comment.HasOne<User>().WithMany().HasForeignKey(x => x.AuthorId);
        comment.HasIndex(x => new { x.CardId, x.CreatedAtUtc, x.Id });

        // Avoid SQL Server multiple cascade paths; deletion is an explicit service transaction.
        foreach (var foreignKey in model.Model.GetEntityTypes().SelectMany(x => x.GetForeignKeys()))
            foreignKey.DeleteBehavior = DeleteBehavior.NoAction;
    }
}
