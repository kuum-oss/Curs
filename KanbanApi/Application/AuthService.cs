using KanbanApi.Domain;
using KanbanApi.Infrastructure;
using KanbanContracts;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace KanbanApi.Application;

public sealed class PasswordService
{
    public const int WorkFactor = 12;
    // A missing account still performs a real BCrypt verification.
    private readonly string dummyHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString(), WorkFactor);
    public string Hash(string password) => BCrypt.Net.BCrypt.HashPassword(password, WorkFactor);
    public bool Verify(string password, string? hash) => BCrypt.Net.BCrypt.Verify(password, hash ?? dummyHash) && hash is not null;
}

public sealed class AuthService(KanbanDbContext db, PasswordService passwords, JwtService jwt)
{
    public async Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken)
    {
        var email = request.Email.Trim();
        var normalized = email.ToUpperInvariant();
        if (await db.Users.AnyAsync(x => x.NormalizedEmail == normalized, cancellationToken))
            throw DuplicateEmail();
        var user = new User { Email = email, NormalizedEmail = normalized, Name = request.Name.Trim(), PasswordHash = passwords.Hash(request.Password) };
        db.Users.Add(user);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException error) when (error.InnerException is SqlException { Number: 2601 or 2627 })
        {
            throw DuplicateEmail();
        }
        return jwt.Issue(UserPresentation.Dto(user), user.SessionVersion);
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        var email = request.Email.Trim().ToUpperInvariant();
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.NormalizedEmail == email, cancellationToken);
        if (!passwords.Verify(request.Password, user?.PasswordHash))
            throw new ApiException(401, "invalid_credentials", "Невірний email або пароль.");
        return jwt.Issue(UserPresentation.Dto(user!), user!.SessionVersion);
    }

    public async Task<UserDto> MeAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Users.AsNoTracking().Where(x => x.Id == id).Select(x => new UserDto(x.Id, x.Email, x.Name, Convert.ToBase64String(x.RowVersion), x.AvatarData != null))
            .SingleOrDefaultAsync(cancellationToken)
        ?? throw new ApiException(401, "unauthorized", "Сесію завершено. Увійдіть знову.");

    private static ApiException DuplicateEmail() => new(409, "email_exists", "Користувач із цим email уже існує.");
}
