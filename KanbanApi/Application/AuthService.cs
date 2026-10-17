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

public sealed class AuthService(KanbanDbContext db, PasswordService passwords, JwtService jwt, TimeProvider clock)
{
    public async Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken)
    {
        var email = request.Email.Trim();
        var normalized = email.ToUpperInvariant();
        if (await db.Users.AnyAsync(x => x.NormalizedEmail == normalized, cancellationToken))
            throw DuplicateEmail();
        var user = new User { Email = email, NormalizedEmail = normalized, Name = request.Name.Trim(), PasswordHash = passwords.Hash(request.Password), IsProtectedDemo = DemoAccounts.IsProtectedEmail(normalized) };
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
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var current = await db.Users.FromSqlInterpolated($"SELECT * FROM Users WITH (UPDLOCK, HOLDLOCK) WHERE Id = {user!.Id}").SingleOrDefaultAsync(cancellationToken);
            if (current is null || current.NormalizedEmail != email || current.PasswordHash != user!.PasswordHash && !passwords.Verify(request.Password, current.PasswordHash))
                throw new ApiException(401, "invalid_credentials", "Невірний email або пароль.");
            var restored = current.DeletionDueAtUtc is not null;
            if (restored && current.DeletionDueAtUtc <= clock.GetUtcNow().UtcDateTime && !DemoAccounts.IsProtected(current))
                throw new ApiException(401, "account_deletion_expired", "Строк відновлення аккаунта минув.");
            if (restored)
            {
                current.DeletionRequestedAtUtc = null; current.DeletionDueAtUtc = null; current.SessionVersion++;
                await db.SaveChangesAsync(cancellationToken);
            }
            var response = jwt.Issue(UserPresentation.Dto(current), current.SessionVersion) with { AccountRestored = restored };
            await transaction.CommitAsync(cancellationToken);
            return response;
        });
    }

    public async Task<UserDto> MeAsync(Guid id, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.DeletionDueAtUtc == null, cancellationToken)
            ?? throw new ApiException(401, "unauthorized", "Сесію завершено. Увійдіть знову.");
        return UserPresentation.Dto(user);
    }

    private static ApiException DuplicateEmail() => new(409, "email_exists", "Користувач із цим email уже існує.");
}
