using System.ComponentModel.DataAnnotations;

namespace KanbanContracts;

public enum DeadlineFilter { Overdue, Today, Upcoming }
public sealed class CardQuery : PageQuery, IValidatableObject
{
    [StringLength(FieldLimits.CardTitle)] public string? Search { get; set; }
    [EnumDataType(typeof(CardPriority))] public CardPriority? Priority { get; set; }
    public Guid? TagId { get; set; }
    public Guid? AssigneeId { get; set; }
    [EnumDataType(typeof(DeadlineFilter))] public DeadlineFilter? Deadline { get; set; }
    public DateOnly? Today { get; set; }
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Deadline is not null && Today is null)
            yield return new ValidationResult("Передайте локальну дату для фільтра дедлайну.", [nameof(Today)]);
    }
}
public static class ImageLimits
{
    public const int MaxBytes = 5 * 1024 * 1024;
    public const int RequestBytes = MaxBytes + 64 * 1024;
    public const int MaxSide = 10_000;
    public const long MaxPixels = 20_000_000;
    public const int ThumbnailSide = 480;
}
public sealed record ImageMutationDto(string Version, string BoardVersion);
