using System.ComponentModel.DataAnnotations;

namespace KanbanContracts;

public class BoardMutationRequest
{
    [Required(ErrorMessage = "Оновіть дошку перед зміною."), RowVersion] public string ExpectedBoardVersion { get; set; } = "";
}
public class ResourceVersionQuery : BoardMutationRequest
{
    [Required(ErrorMessage = "Оновіть дані перед зміною."), RowVersion] public string ExpectedVersion { get; set; } = "";
}
public class CreateColumnRequest : BoardMutationRequest
{
    [Required(ErrorMessage = "Введіть назву колонки."), StringLength(FieldLimits.ColumnName, ErrorMessage = "Назва колонки: не більше 100 символів.")] public string Name { get; set; } = "";
}
public sealed class UpdateColumnRequest : CreateColumnRequest
{
    [Required(ErrorMessage = "Оновіть дані перед зміною."), RowVersion] public string ExpectedVersion { get; set; } = "";
}
public class CardFieldsRequest : BoardMutationRequest
{
    [Required(ErrorMessage = "Введіть назву картки."), StringLength(FieldLimits.CardTitle, ErrorMessage = "Назва картки: не більше 200 символів.")] public string Title { get; set; } = "";
    [StringLength(FieldLimits.CardDescription, ErrorMessage = "Опис картки: не більше 10 000 символів.")] public string? Description { get; set; }
    public DateOnly? DueDate { get; set; }
    [EnumDataType(typeof(CardPriority), ErrorMessage = "Оберіть допустимий пріоритет.")] public CardPriority Priority { get; set; } = CardPriority.Medium;
    public Guid? AssigneeId { get; set; }
    [Required(ErrorMessage = "Передайте список тегів."), MaxLength(100, ErrorMessage = "Не більше 100 тегів на картку.")] public List<Guid> TagIds { get; set; } = [];
}
public sealed class CreateCardRequest : CardFieldsRequest
{
    public Guid ColumnId { get; set; }
}
public sealed class UpdateCardRequest : CardFieldsRequest
{
    [Required(ErrorMessage = "Оновіть дані перед зміною."), RowVersion] public string ExpectedVersion { get; set; } = "";
}
public sealed class CreateTagRequest : BoardMutationRequest
{
    [Required(ErrorMessage = "Введіть назву тегу."), StringLength(FieldLimits.TagName, ErrorMessage = "Назва тегу: не більше 50 символів.")] public string Name { get; set; } = "";
    [Required(ErrorMessage = "Введіть колір тегу."), RegularExpression("^#[0-9A-Fa-f]{6}$", ErrorMessage = "Колір тегу має формат #RRGGBB.")] public string Color { get; set; } = "#64748B";
}
public sealed class CreateCommentRequest : ResourceVersionQuery
{
    [Required(ErrorMessage = "Введіть текст коментаря."), StringLength(FieldLimits.Comment, ErrorMessage = "Коментар: не більше 4000 символів.")] public string Text { get; set; } = "";
}
public class PageQuery
{
    [Range(0, int.MaxValue, ErrorMessage = "Зміщення не може бути від’ємним.")] public int Offset { get; set; }
    [Range(1, 500, ErrorMessage = "Розмір сторінки: від 1 до 500.")] public int Limit { get; set; } = 100;
}
