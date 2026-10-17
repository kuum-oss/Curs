using KanbanApi.Domain;

namespace KanbanApi.Application;

public static class DemoAccounts
{
    public static readonly string[] Emails = ["DEMO.ANASTASIIA@KANBAN.LOCAL", "DEMO.OLENA@KANBAN.LOCAL", "DEMO.DMYTRO@KANBAN.LOCAL", "DEMO.IRYNA@KANBAN.LOCAL"];
    public static bool IsProtectedEmail(string email) => Emails.Contains(email.Trim(), StringComparer.OrdinalIgnoreCase);
    public static bool IsProtected(User user) => user.IsProtectedDemo || IsProtectedEmail(user.NormalizedEmail) || IsProtectedEmail(user.Email);
    public static void RequireDeletable(User user)
    {
        if (IsProtected(user)) throw new ApiException(403, "demo_account_protected", "Цей демонстраційний аккаунт захищений від видалення.");
    }
}
