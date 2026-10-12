using System.ComponentModel.DataAnnotations;

namespace KanbanContracts;

public class BoardMutationRequest
{
    [Required, RowVersion] public string ExpectedBoardVersion { get; set; } = "";
}
public class ResourceVersionQuery : BoardMutationRequest
{
    [Required, RowVersion] public string ExpectedVersion { get; set; } = "";
}
public class CreateColumnRequest : BoardMutationRequest
{
    [Required, StringLength(FieldLimits.ColumnName)] public string Name { get; set; } = "";
}
public sealed class UpdateColumnRequest : CreateColumnRequest
{
    [Required, RowVersion] public string ExpectedVersion { get; set; } = "";
}
public class CardFieldsRequest : BoardMutationRequest
{
    [Required, StringLength(FieldLimits.CardTitle)] public string Title { get; set; } = "";
    [StringLength(FieldLimits.CardDescription)] public string? Description { get; set; }
    public DateOnly? DueDate { get; set; }
    [EnumDataType(typeof(CardPriority))] public CardPriority Priority { get; set; } = CardPriority.Medium;
    public Guid? AssigneeId { get; set; }
    [Required, MaxLength(100)] public List<Guid> TagIds { get; set; } = [];
}
public sealed class CreateCardRequest : CardFieldsRequest
{
    public Guid ColumnId { get; set; }
}
public sealed class UpdateCardRequest : CardFieldsRequest
{
    [Required, RowVersion] public string ExpectedVersion { get; set; } = "";
}
public sealed class CreateTagRequest : BoardMutationRequest
{
    [Required, StringLength(FieldLimits.TagName)] public string Name { get; set; } = "";
    [Required, RegularExpression("^#[0-9A-Fa-f]{6}$")] public string Color { get; set; } = "#64748B";
}
public sealed class CreateCommentRequest : ResourceVersionQuery
{
    [Required, StringLength(FieldLimits.Comment)] public string Text { get; set; } = "";
}
public sealed class PageQuery
{
    [Range(0, int.MaxValue)] public int Offset { get; set; }
    [Range(1, 500)] public int Limit { get; set; } = 100;
}
