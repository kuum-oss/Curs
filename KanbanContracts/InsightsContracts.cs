using System.ComponentModel.DataAnnotations;
namespace KanbanContracts;
public sealed class HistoryQuery : PageQuery
{
    public Guid? CardId { get; set; }
    public Guid? ActorId { get; set; }
    [StringLength(100)] public string? Search { get; set; }
    [RegularExpression("^(created|deleted|title|description|deadline|priority|assignee|column|archive)$")]
    public string? Field { get; set; }
    [Range(1,90)] public int Days { get; set; } = 7;
}
public sealed class StatisticsQuery : IValidatableObject
{
    public DateOnly Today { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow);
    [Range(-840,840)] public int UtcOffsetMinutes { get; set; }
    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if(Today.Year < 1900 || Today.Year > 9998) yield return new ValidationResult("Некоректна дата статистики.", [nameof(Today)]);
    }
}
public sealed class CompletionColumnRequest : BoardMutationRequest { public Guid? ColumnId { get; set; } }
public sealed record ActivityDto(long Id, Guid BoardId, Guid CardId, string CardTitle, UserDto Actor,
    string Field, string? Before, string? After, DateTime CreatedAtUtc, bool CanOpen);
public sealed record DailyCompletionDto(DateOnly Date, int Count);
public sealed record WorkloadDto(UserDto User, int Active, int Total);
public sealed record BoardStatisticsDto(Guid BoardId, string BoardVersion, Guid? CompletionColumnId,
    IReadOnlyList<ColumnDto> Columns, DateTime TrackingSinceUtc, bool HasPreviousPeriod,
    int Completed, int? PreviousCompleted, int Overdue, int? PreviousOverdue, int InProgress,
    int? PreviousInProgress, double? MeanDays, double? PreviousMeanDays, int OverdueOverSevenDays,
    IReadOnlyList<DailyCompletionDto> Days, IReadOnlyList<WorkloadDto> Workload, IReadOnlyList<ActivityDto> Recent);
