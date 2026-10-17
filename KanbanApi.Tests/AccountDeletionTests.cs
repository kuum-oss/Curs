using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using KanbanApi.Domain;
using KanbanApi.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc.Testing;
using KanbanContracts;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace KanbanApi.Tests;

[Collection("sql")]
public sealed class AccountDeletionTests(SqlFixture fixture)
{
    private readonly AccountDeletionTestClock clock=new();
    private async Task Expire(WebApplicationFactory<Program> app)
    {
        clock.Advance(TimeSpan.FromDays(7));using var scope=app.Services.CreateScope();await scope.ServiceProvider.GetRequiredService<AccountDeletionService>().PurgeExpired(default);
    }
    private const string Password="delete-account123!";
    private static async Task<AuthResponse> User(HttpClient client,string name="Власник")
    {
        var response=await client.PostAsJsonAsync("/api/auth/register",new RegisterRequest{Email=Guid.NewGuid()+"@example.test",Name=name,Password=Password});response.EnsureSuccessStatusCode();
        var auth=(await response.Content.ReadFromJsonAsync<AuthResponse>())!;client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",auth.AccessToken);return auth;
    }
    private async Task<(Board Board,Card Card,Comment Comment)> Graph(Guid owner,params (Guid Id,BoardRole Role,int Age)[] peers)
    {
        await using var db=fixture.CreateContext();var board=new Board{Name="Приватна назва "+Guid.NewGuid(),OwnerId=owner};var col=new BoardColumn{BoardId=board.Id,Name="План"};var card=new Card{ColumnId=col.Id,CreatedById=owner,AssigneeId=owner,Title="Картка",Description="Спільний опис"};var comment=new Comment{CardId=card.Id,AuthorId=owner,Text="Історія обговорення"};var tag=new Tag{BoardId=board.Id,Name="Тег",NormalizedName="ТЕГ"};
        db.AddRange(board,col,card,comment,tag,new BoardMember{BoardId=board.Id,UserId=owner,Role=BoardRole.Admin},new CardTag{CardId=card.Id,TagId=tag.Id},new CardImage{CardId=card.Id,OriginalData=[1,2,3],ThumbnailData=[4,5],SizeBytes=3,ContentType="image/png",ThumbnailContentType="image/png",OriginalName="cover.png"});
        foreach(var peer in peers)db.Add(new BoardMember{BoardId=board.Id,UserId=peer.Id,Role=peer.Role,JoinedAtUtc=DateTime.UtcNow.AddDays(-peer.Age),CanCreateCards=false,CanDeleteCards=false});
        await db.SaveChangesAsync();return(board,card,comment);
    }
    private static async Task<AccountDeletionPreviewDto> Preview(HttpClient c)
    {
        using var response=await c.GetAsync("/api/profile/deletion-preview");response.EnsureSuccessStatusCode();Assert.True(response.Headers.CacheControl?.NoStore);
        return (await response.Content.ReadFromJsonAsync<AccountDeletionPreviewDto>())!;
    }
    private static DeleteAccountRequest Request(AccountDeletionPreviewDto p)=>new(){CurrentPassword=Password,ConfirmationEmail=p.Email,ExpectedVersion=p.ExpectedVersion,ConfirmationToken=p.ConfirmationToken};
    private static Task<HttpResponseMessage> Delete(HttpClient c,DeleteAccountRequest request)=>c.SendAsync(new HttpRequestMessage(HttpMethod.Delete,"/api/profile"){Content=JsonContent.Create(request)});
    [SqlFact]
    public async Task RemovesUserAndEntireSoloBoardGraphRevokesJwtAndAllowsEmailReuse()
    {
        using var app=clock.Factory(fixture.ConnectionString);using var c=app.CreateClient();var auth=await User(c);var g=await Graph(auth.User.Id);
        await using(var db=fixture.CreateContext()){var u=(await db.Users.FindAsync(auth.User.Id))!;u.AvatarData=[8,9];await db.SaveChangesAsync();}
        var p=await Preview(c);Assert.Single(p.OwnedBoards);Assert.Null(p.OwnedBoards[0].NewOwnerName);Assert.Equal(auth.User.Email,p.Email);
        Assert.Equal(HttpStatusCode.Accepted,(await Delete(c,Request(p))).StatusCode);await Expire(app);
        await using(var db=fixture.CreateContext())
        {
            Assert.False(await db.Users.AnyAsync(u=>u.Id==auth.User.Id));Assert.False(await db.Boards.AnyAsync(b=>b.Id==g.Board.Id));Assert.False(await db.Cards.AnyAsync(x=>x.Id==g.Card.Id));Assert.False(await db.Comments.AnyAsync(x=>x.Id==g.Comment.Id));Assert.False(await db.CardImages.AnyAsync(i=>i.CardId==g.Card.Id));Assert.False(await db.BoardMembers.AnyAsync(m=>m.UserId==auth.User.Id));
        }
        Assert.Equal(HttpStatusCode.Unauthorized,(await c.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await c.PostAsJsonAsync("/api/auth/login",new LoginRequest{Email=auth.User.Email,Password=Password})).StatusCode);
        var again=await c.PostAsJsonAsync("/api/auth/register",new RegisterRequest{Email=auth.User.Email,Name="Новий аккаунт",Password=Password});again.EnsureSuccessStatusCode();Assert.NotEqual(auth.User.Id,(await again.Content.ReadFromJsonAsync<AuthResponse>())!.User.Id);
    }
    [SqlFact]
    public async Task SharedBoardTransfersToExistingAdminAndPreservesAnonymousCommentsCardsAndImages()
    {
        using var app=clock.Factory(fixture.ConnectionString);using var c=app.CreateClient();using var admin=app.CreateClient();using var member=app.CreateClient();var a=await User(c);var b=await User(admin,"Адміністратор");var m=await User(member,"Старший учасник");var g=await Graph(a.User.Id,(m.User.Id,BoardRole.Member,10),(b.User.Id,BoardRole.Admin,1));
        var p=await Preview(c);Assert.Equal(b.User.Name,p.OwnedBoards.Single().NewOwnerName);Assert.Equal(HttpStatusCode.Accepted,(await Delete(c,Request(p))).StatusCode);await Expire(app);
        var board=(await admin.GetFromJsonAsync<BoardDetailsDto>($"/api/boards/{g.Board.Id}"))!;Assert.Equal(b.User.Id,board.OwnerId);Assert.NotEqual(Convert.ToBase64String(g.Board.RowVersion),board.Version);
        var details=(await admin.GetFromJsonAsync<CardDetailsDto>($"/api/cards/{g.Card.Id}"))!;Assert.True(details.Card.HasImage);Assert.Null(details.Card.Assignee);Assert.Equal("Спільний опис",details.Description);
        var comments=(await admin.GetFromJsonAsync<PagedResult<CommentDto>>($"/api/cards/{g.Card.Id}/comments"))!;Assert.Equal(1,comments.TotalCount);var comment=Assert.Single(comments.Items);Assert.Equal(Guid.Empty,comment.Author.Id);Assert.Equal("",comment.Author.Email);Assert.Equal("Видалений користувач",comment.Author.Name);Assert.False(comment.Author.HasAvatar);Assert.Equal("Історія обговорення",comment.Text);
        await using var db=fixture.CreateContext();Assert.Null((await db.Cards.FindAsync(g.Card.Id))!.CreatedById);Assert.Null((await db.Comments.FindAsync(g.Comment.Id))!.AuthorId);Assert.Equal(new byte[]{1,2,3},(await db.CardImages.SingleAsync(i=>i.CardId==g.Card.Id)).OriginalData);var successor=await db.BoardMembers.SingleAsync(x=>x.BoardId==g.Board.Id&&x.UserId==b.User.Id);Assert.True(successor.CanCreateCards&&successor.CanDeleteCards);Assert.False(await db.BoardMembers.AnyAsync(x=>x.UserId==a.User.Id));
    }
    [SqlFact]
    public async Task OldestRemainingMemberBecomesOwnerAndAdminWhenNoOtherAdminExists()
    {
        using var app=clock.Factory(fixture.ConnectionString);using var c=app.CreateClient();using var peer=app.CreateClient();using var newer=app.CreateClient();var a=await User(c);var b=await User(peer,"Перший учасник");var z=await User(newer,"Новий учасник");var g=await Graph(a.User.Id,(z.User.Id,BoardRole.Member,1),(b.User.Id,BoardRole.Member,5));
        var p=await Preview(c);Assert.Equal(b.User.Name,p.OwnedBoards.Single().NewOwnerName);Assert.Equal(HttpStatusCode.Accepted,(await Delete(c,Request(p))).StatusCode);await Expire(app);
        await using var db=fixture.CreateContext();Assert.Equal(b.User.Id,(await db.Boards.FindAsync(g.Board.Id))!.OwnerId);Assert.Equal(BoardRole.Admin,(await db.BoardMembers.FindAsync(g.Board.Id,b.User.Id))!.Role);Assert.Equal(BoardRole.Member,(await db.BoardMembers.FindAsync(g.Board.Id,z.User.Id))!.Role);
    }
    [SqlFact]
    public async Task FormerMemberDeletionDoesNotExposePrivateBoardAndStillAnonymizesHistory()
    {
        using var app=clock.Factory(fixture.ConnectionString);using var c=app.CreateClient();using var peer=app.CreateClient();var a=await User(c);var b=await User(peer);var g=await Graph(b.User.Id);
        await using(var db=fixture.CreateContext()){(await db.Cards.FindAsync(g.Card.Id))!.CreatedById=a.User.Id;(await db.Comments.FindAsync(g.Comment.Id))!.AuthorId=a.User.Id;await db.SaveChangesAsync();}
        var p=await Preview(c);Assert.Empty(p.OwnedBoards);Assert.Equal(0,p.MembershipsToLeave);Assert.Equal(HttpStatusCode.Accepted,(await Delete(c,Request(p))).StatusCode);await Expire(app);
        await using var check=fixture.CreateContext();Assert.Equal(b.User.Id,(await check.Boards.FindAsync(g.Board.Id))!.OwnerId);Assert.Null((await check.Comments.FindAsync(g.Comment.Id))!.AuthorId);Assert.Null((await check.Cards.FindAsync(g.Card.Id))!.CreatedById);
    }
    [SqlFact]
    public async Task OrdinaryMemberLeavesWithoutChangingTheOtherOwnerOrTheirHistory()
    {
        using var app=clock.Factory(fixture.ConnectionString);using var c=app.CreateClient();using var peer=app.CreateClient();var a=await User(c);var b=await User(peer);var g=await Graph(b.User.Id,(a.User.Id,BoardRole.Member,1));
        await using(var db=fixture.CreateContext()){(await db.Cards.FindAsync(g.Card.Id))!.AssigneeId=a.User.Id;await db.SaveChangesAsync();}
        var p=await Preview(c);Assert.Empty(p.OwnedBoards);Assert.Equal(1,p.MembershipsToLeave);Assert.Equal(HttpStatusCode.Accepted,(await Delete(c,Request(p))).StatusCode);await Expire(app);
        await using var check=fixture.CreateContext();Assert.Equal(b.User.Id,(await check.Boards.FindAsync(g.Board.Id))!.OwnerId);var card=(await check.Cards.FindAsync(g.Card.Id))!;Assert.Equal(b.User.Id,card.CreatedById);Assert.Null(card.AssigneeId);Assert.Equal(b.User.Id,(await check.Comments.FindAsync(g.Comment.Id))!.AuthorId);
    }
    [SqlFact]
    public async Task MissingConfirmationWrongPasswordWrongEmailAndStalePreviewCannotMutateAnything()
    {
        using var app=clock.Factory(fixture.ConnectionString);using var c=app.CreateClient();using var anonymous=app.CreateClient();var a=await User(c);var g=await Graph(a.User.Id);var p=await Preview(c);
        foreach(var change in new Action<DeleteAccountRequest>[] {r=>r.CurrentPassword="wrong",r=>r.CurrentPassword=new string('Ж',73),r=>r.ConfirmationEmail="other@example.test",r=>r.ConfirmationToken="",r=>r.ConfirmationToken=new string('0',64),r=>r.ExpectedVersion=Convert.ToBase64String(new byte[8])})
        {
            var request=Request(p);change(request);Assert.Contains((await Delete(c,request)).StatusCode,new[]{HttpStatusCode.BadRequest,HttpStatusCode.Conflict});
        }
        Assert.Equal(HttpStatusCode.Unauthorized,(await Delete(anonymous,Request(p))).StatusCode);Assert.Equal(HttpStatusCode.NotFound,(await c.DeleteAsync("/api/profile/"+a.User.Id)).StatusCode);
        await using var db=fixture.CreateContext();Assert.True(await db.Users.AnyAsync(u=>u.Id==a.User.Id));Assert.Equal(g.Board.RowVersion,(await db.Boards.FindAsync(g.Board.Id))!.RowVersion);Assert.NotNull((await db.Cards.FindAsync(g.Card.Id))!.AssigneeId);
    }
    [SqlFact]
    public async Task BoardMembershipChangeAfterPreviewRollsBackAllDeletionEffects()
    {
        using var app=clock.Factory(fixture.ConnectionString);using var c=app.CreateClient();using var peer=app.CreateClient();var a=await User(c);var b=await User(peer);var solo=await Graph(a.User.Id);var shared=await Graph(a.User.Id,(b.User.Id,BoardRole.Member,1));var p=await Preview(c);
        var response=await c.PutAsJsonAsync($"/api/boards/{shared.Board.Id}",new UpdateBoardRequest{Name="Зміни після перегляду",ExpectedBoardVersion=Convert.ToBase64String(shared.Board.RowVersion)});response.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict,(await Delete(c,Request(p))).StatusCode);
        await using var db=fixture.CreateContext();Assert.True(await db.Users.AnyAsync(u=>u.Id==a.User.Id));Assert.True(await db.Boards.AnyAsync(x=>x.Id==solo.Board.Id));Assert.Equal(a.User.Id,(await db.Boards.FindAsync(shared.Board.Id))!.OwnerId);Assert.Equal(solo.Board.RowVersion,(await db.Boards.FindAsync(solo.Board.Id))!.RowVersion);
    }
    [SqlFact]
    public async Task ConcurrentInvitationAndDeletionCommitOnlyAConsistentOutcome()
    {
        using var app=clock.Factory(fixture.ConnectionString);using var c=app.CreateClient();using var invite=app.CreateClient();using var peer=app.CreateClient();var a=await User(c);var b=await User(peer);invite.DefaultRequestHeaders.Authorization=c.DefaultRequestHeaders.Authorization;var g=await Graph(a.User.Id);var p=await Preview(c);
        var deleting=Delete(c,Request(p));var adding=invite.PostAsJsonAsync($"/api/boards/{g.Board.Id}/members",new AddMemberRequest{Email=b.User.Email,ExpectedBoardVersion=Convert.ToBase64String(g.Board.RowVersion)});await Task.WhenAll(deleting,adding);
        Assert.Contains(deleting.Result.StatusCode,new[]{HttpStatusCode.Accepted,HttpStatusCode.Conflict});Assert.Contains(adding.Result.StatusCode,new[]{HttpStatusCode.Created,HttpStatusCode.Unauthorized,HttpStatusCode.NotFound,HttpStatusCode.Conflict});
        if(deleting.Result.StatusCode==HttpStatusCode.Accepted)await Expire(app);
        await using var db=fixture.CreateContext();if(deleting.Result.StatusCode==HttpStatusCode.Accepted)
        {
            Assert.False(await db.Users.AnyAsync(x=>x.Id==a.User.Id));
            if(adding.Result.StatusCode==HttpStatusCode.Created)Assert.Equal(b.User.Id,(await db.Boards.FindAsync(g.Board.Id))!.OwnerId);
            else Assert.False(await db.Boards.AnyAsync(x=>x.Id==g.Board.Id));
        }
        else{Assert.True(await db.Users.AnyAsync(x=>x.Id==a.User.Id));Assert.True(await db.Boards.AnyAsync(x=>x.Id==g.Board.Id));}
        Assert.False(await db.BoardMembers.AnyAsync(m=>!db.Users.Any(u=>u.Id==m.UserId)||!db.Boards.Any(board=>board.Id==m.BoardId)));
    }
}
