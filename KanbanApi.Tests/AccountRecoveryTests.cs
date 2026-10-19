using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using KanbanApi.Application;
using KanbanApi.Domain;
using KanbanContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KanbanApi.Tests;

[Collection("sql")]
public sealed class AccountRecoveryTests(SqlFixture fixture)
{
    private const string Password="recovery-password123!";
    private static async Task<AuthResponse> Register(HttpClient c)
    {
        var r=await c.PostAsJsonAsync("/api/auth/register",new RegisterRequest{Email=Guid.NewGuid()+"@example.test",Name="Відновлення",Password=Password});r.EnsureSuccessStatusCode();var auth=(await r.Content.ReadFromJsonAsync<AuthResponse>())!;c.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",auth.AccessToken);return auth;
    }
    private async Task<(Board Board,Card Card,Comment Comment)> Graph(Guid user)
    {
        await using var db=fixture.CreateContext();var board=new Board{Name="Дошка до відновлення",OwnerId=user};var col=new BoardColumn{BoardId=board.Id,Name="План"};var card=new Card{ColumnId=col.Id,CreatedById=user,AssigneeId=user,Title="Усі дані"};var comment=new Comment{CardId=card.Id,AuthorId=user,Text="Історія"};db.AddRange(board,col,card,comment,new ChecklistItem{Id=Guid.NewGuid(),CardId=card.Id,Title="Збережений пункт",IsCompleted=true},new BoardMember{BoardId=board.Id,UserId=user,Role=BoardRole.Admin},new CardImage{CardId=card.Id,OriginalData=[1,2,3],ThumbnailData=[4],SizeBytes=3,ContentType="image/png",ThumbnailContentType="image/png",OriginalName="original.png"});await db.SaveChangesAsync();return(board,card,comment);
    }
    private static async Task<AccountDeletionScheduledDto> Schedule(HttpClient c,string password=Password)
    {
        var p=(await c.GetFromJsonAsync<AccountDeletionPreviewDto>("/api/profile/deletion-preview"))!;
        var r=await c.SendAsync(new(HttpMethod.Delete,"/api/profile"){Content=JsonContent.Create(new DeleteAccountRequest{ConfirmationEmail=p.Email,CurrentPassword=password,ExpectedVersion=p.ExpectedVersion,ConfirmationToken=p.ConfirmationToken})});Assert.Equal(HttpStatusCode.Accepted,r.StatusCode);return(await r.Content.ReadFromJsonAsync<AccountDeletionScheduledDto>())!;
    }
    [SqlFact]
    public async Task SevenDayWindowKeepsAllDataAndLoginRestoresOriginalIdentityAndBoards()
    {
        var clock=new AccountDeletionTestClock();using var app=clock.Factory(fixture.ConnectionString);using var c=app.CreateClient();var a=await Register(c);var g=await Graph(a.User.Id);var scheduled=await Schedule(c);
        Assert.Equal(clock.GetUtcNow().UtcDateTime.AddDays(7),scheduled.DeletionDueAtUtc);Assert.Equal(HttpStatusCode.Unauthorized,(await c.GetAsync("/api/auth/me")).StatusCode);
        await using(var db=fixture.CreateContext()){var u=(await db.Users.FindAsync(a.User.Id))!;Assert.Equal(a.User.Email,u.Email);Assert.NotNull(u.DeletionDueAtUtc);Assert.Equal(g.Board.RowVersion,(await db.Boards.FindAsync(g.Board.Id))!.RowVersion);Assert.Equal(a.User.Id,(await db.Cards.FindAsync(g.Card.Id))!.CreatedById);Assert.Equal(a.User.Id,(await db.Comments.FindAsync(g.Comment.Id))!.AuthorId);}
        clock.Advance(TimeSpan.FromDays(6)+TimeSpan.FromHours(23));using(var scope=app.Services.CreateScope())Assert.Equal(0,await scope.ServiceProvider.GetRequiredService<AccountDeletionService>().PurgeExpired(default));
        var wrong=await c.PostAsJsonAsync("/api/auth/login",new LoginRequest{Email=a.User.Email,Password="wrong-password"});Assert.Equal(HttpStatusCode.Unauthorized,wrong.StatusCode);
        var restored=(await (await c.PostAsJsonAsync("/api/auth/login",new LoginRequest{Email=a.User.Email,Password=Password})).Content.ReadFromJsonAsync<AuthResponse>())!;Assert.True(restored.AccountRestored);Assert.Equal(a.User.Id,restored.User.Id);c.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",restored.AccessToken);Assert.Equal(HttpStatusCode.OK,(await c.GetAsync($"/api/boards/{g.Board.Id}")).StatusCode);
        clock.Advance(TimeSpan.FromDays(2));using(var scope=app.Services.CreateScope())await scope.ServiceProvider.GetRequiredService<AccountDeletionService>().PurgeExpired(default);
        await using var check=fixture.CreateContext();var user=(await check.Users.FindAsync(a.User.Id))!;Assert.Null(user.DeletionDueAtUtc);Assert.Null(user.DeletionRequestedAtUtc);Assert.Equal(a.User.Id,(await check.Comments.FindAsync(g.Comment.Id))!.AuthorId);Assert.Equal(new byte[]{1,2,3},(await check.CardImages.SingleAsync(i=>i.CardId==g.Card.Id)).OriginalData);Assert.True((await check.ChecklistItems.SingleAsync(i=>i.CardId==g.Card.Id)).IsCompleted);
        using var old=app.CreateClient();old.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",a.AccessToken);Assert.Equal(HttpStatusCode.Unauthorized,(await old.GetAsync("/api/auth/me")).StatusCode);
    }
    [SqlFact]
    public async Task ExactDeadlineDeniesRecoveryAndCleanupRemovesOnlyExpiredAccount()
    {
        var clock=new AccountDeletionTestClock();using var app=clock.Factory(fixture.ConnectionString);using var c=app.CreateClient();using var active=app.CreateClient();var a=await Register(c);var b=await Register(active);var g=await Graph(a.User.Id);await Schedule(c);clock.Advance(TimeSpan.FromDays(7));
        var r=await c.PostAsJsonAsync("/api/auth/login",new LoginRequest{Email=a.User.Email,Password=Password});Assert.Equal(HttpStatusCode.Unauthorized,r.StatusCode);Assert.Equal("account_deletion_expired",(await r.Content.ReadFromJsonAsync<ApiProblem>())!.Code);
        using(var scope=app.Services.CreateScope())await scope.ServiceProvider.GetRequiredService<AccountDeletionService>().PurgeExpired(default);
        await using var db=fixture.CreateContext();Assert.Null(await db.Users.FindAsync(a.User.Id));Assert.Null(await db.Boards.FindAsync(g.Board.Id));Assert.False(await db.ChecklistItems.AnyAsync(i=>i.CardId==g.Card.Id));Assert.NotNull(await db.Users.FindAsync(b.User.Id));
    }
    [SqlFact]
    public async Task ProtectedDemoIdentitySurvivesEmailChangesAndIsNeverScheduledOrPurged()
    {
        foreach(var email in DemoAccounts.Emails){Assert.True(DemoAccounts.IsProtectedEmail(email.ToLowerInvariant()));Assert.True(DemoAccounts.IsProtected(new User{Email=email}));}
        var clock=new AccountDeletionTestClock();using var app=clock.Factory(fixture.ConnectionString);using var c=app.CreateClient();var a=await Register(c);var g=await Graph(a.User.Id);
        await using(var db=fixture.CreateContext()){var u=(await db.Users.FindAsync(a.User.Id))!;u.IsProtectedDemo=true;u.Email="changed-"+u.Email;u.NormalizedEmail=u.Email.ToUpperInvariant();await db.SaveChangesAsync();}
        var me=(await c.GetFromJsonAsync<UserDto>("/api/auth/me"))!;Assert.True(me.IsDeletionProtected);Assert.Equal(HttpStatusCode.Forbidden,(await c.GetAsync("/api/profile/deletion-preview")).StatusCode);
        var response=await c.SendAsync(new(HttpMethod.Delete,"/api/profile"){Content=JsonContent.Create(new DeleteAccountRequest{CurrentPassword=Password,ConfirmationEmail=me.Email,ExpectedVersion=me.Version!,ConfirmationToken=new string('0',64)})});Assert.Equal(HttpStatusCode.Forbidden,response.StatusCode);
        await using(var db=fixture.CreateContext()){var u=(await db.Users.FindAsync(a.User.Id))!;Assert.Null(u.DeletionDueAtUtc);u.DeletionRequestedAtUtc=clock.GetUtcNow().UtcDateTime.AddDays(-8);u.DeletionDueAtUtc=u.DeletionRequestedAtUtc.Value.AddDays(7);await db.SaveChangesAsync();}
        clock.Advance(TimeSpan.FromDays(30));using(var scope=app.Services.CreateScope())await scope.ServiceProvider.GetRequiredService<AccountDeletionService>().PurgeExpired(default);
        await using(var db=fixture.CreateContext()){Assert.NotNull(await db.Users.FindAsync(a.User.Id));Assert.NotNull(await db.Boards.FindAsync(g.Board.Id));}
        var login=await c.PostAsJsonAsync("/api/auth/login",new LoginRequest{Email=me.Email,Password=Password});Assert.Equal(HttpStatusCode.OK,login.StatusCode);Assert.True((await login.Content.ReadFromJsonAsync<AuthResponse>())!.AccountRestored);
    }
    [SqlFact]
    public async Task RecoveryWindowCannotBeBypassedWithRepeatedOldTokenOrNewRegistration()
    {
        var clock=new AccountDeletionTestClock();using var app=clock.Factory(fixture.ConnectionString);using var c=app.CreateClient();var a=await Register(c);var due=(await Schedule(c)).DeletionDueAtUtc;
        Assert.Equal(HttpStatusCode.Unauthorized,(await c.GetAsync("/api/profile/deletion-preview")).StatusCode);Assert.Equal(HttpStatusCode.Conflict,(await c.PostAsJsonAsync("/api/auth/register",new RegisterRequest{Email=a.User.Email,Name="Інший",Password=Password})).StatusCode);
        await using var db=fixture.CreateContext();Assert.Equal(due,(await db.Users.FindAsync(a.User.Id))!.DeletionDueAtUtc);
    }
    [SqlFact]
    public async Task ConcurrentLoginAndCleanupAtDeadlineCannotResurrectAnExpiredAccount()
    {
        var clock=new AccountDeletionTestClock();using var app=clock.Factory(fixture.ConnectionString);using var c=app.CreateClient();var a=await Register(c);var g=await Graph(a.User.Id);await Schedule(c);clock.Advance(TimeSpan.FromDays(7));
        var login=c.PostAsJsonAsync("/api/auth/login",new LoginRequest{Email=a.User.Email,Password=Password});
        using var scope=app.Services.CreateScope();var purge=scope.ServiceProvider.GetRequiredService<AccountDeletionService>().PurgeExpired(default);await Task.WhenAll(login,purge);
        Assert.Equal(HttpStatusCode.Unauthorized,login.Result.StatusCode);await using var db=fixture.CreateContext();Assert.Null(await db.Users.FindAsync(a.User.Id));Assert.Null(await db.Boards.FindAsync(g.Board.Id));
    }

    [SqlFact]
    public async Task MemberSchedulingDeletionInvalidatesThePreviouslyDisplayedSuccessor()
    {
        var clock=new AccountDeletionTestClock();using var app=clock.Factory(fixture.ConnectionString);using var owner=app.CreateClient();using var member=app.CreateClient();var a=await Register(owner);var b=await Register(member);var g=await Graph(a.User.Id);
        await using(var db=fixture.CreateContext()){db.Add(new BoardMember{BoardId=g.Board.Id,UserId=b.User.Id});await db.SaveChangesAsync();}
        var preview=(await owner.GetFromJsonAsync<AccountDeletionPreviewDto>("/api/profile/deletion-preview"))!;await Schedule(member);
        var response=await owner.SendAsync(new(HttpMethod.Delete,"/api/profile"){Content=JsonContent.Create(new DeleteAccountRequest{CurrentPassword=Password,ConfirmationEmail=preview.Email,ExpectedVersion=preview.ExpectedVersion,ConfirmationToken=preview.ConfirmationToken})});Assert.Equal(HttpStatusCode.Conflict,response.StatusCode);
        await using var check=fixture.CreateContext();Assert.Null((await check.Users.FindAsync(a.User.Id))!.DeletionDueAtUtc);Assert.NotNull((await check.Users.FindAsync(b.User.Id))!.DeletionDueAtUtc);
    }

}
