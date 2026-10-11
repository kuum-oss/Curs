using System.ComponentModel.DataAnnotations;
using System.Text;

namespace KanbanContracts;

public sealed class RegisterRequest : IValidatableObject
{
    [Required, EmailAddress, StringLength(FieldLimits.Email)]
    public string Email { get; set; } = string.Empty;
    [Required, StringLength(FieldLimits.UserName)]
    public string Name { get; set; } = string.Empty;
    [Required, PasswordLimits]
    public string Password { get; set; } = string.Empty;
    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if (string.IsNullOrWhiteSpace(Name))
            yield return new ValidationResult("Вкажіть ім'я.", [nameof(Name)]);
    }
}

public sealed class LoginRequest
{
    [Required, EmailAddress, StringLength(FieldLimits.Email)]
    public string Email { get; set; } = string.Empty;
    [Required, PasswordLimits]
    public string Password { get; set; } = string.Empty;
}

public sealed class PasswordLimitsAttribute : ValidationAttribute
{
    public PasswordLimitsAttribute() => ErrorMessage = "Пароль: від 8 символів, до 72 байтів UTF-8, без нульового символу.";
    public override bool IsValid(object? value) => value is string password && password.Length >= 8
        && Encoding.UTF8.GetByteCount(password) <= 72 && !password.Contains('\0');
}

public sealed record AuthResponse(string AccessToken, DateTime ExpiresAtUtc, UserDto User);
