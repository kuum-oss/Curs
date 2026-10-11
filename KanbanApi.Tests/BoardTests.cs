using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using KanbanApi.Domain;
using KanbanApi.Infrastructure;
using KanbanContracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KanbanApi.Tests;

[Collection("sql")]
public sealed class BoardTests(SqlFixture fixture)
{
    private async Task<User> UserAsync()
    {
        var user = new User { Email = Guid.NewGuid().ToString("N") + "@example.test", Name = "Тестовий учасник", PasswordHash = "unused-in-board-tests" };
        user.NormalizedEmail = user.Email.ToUpperInvariant();
        await using var db = fixture.CreateContext(); db.Users.Add(user); await db.SaveChangesAsync(); return user;
    }
    private static HttpClient As(WebApplicationFactory<Program> app, User user)
    {
        var client = app.CreateClient();
        var jwt = app.Services.GetRequiredService<JwtService>().Issue(new UserDto(user.Id, user.Email, user.Name));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt.AccessToken); return client;
    }
    private static async Task<BoardDetailsDto> Create(HttpClient client) =>
        (await (await client.PostAsJsonAsync("/api/boards", new CreateBoardRequest { Name = "  Командна дошка  ", Description = "  Опис  " })).Content.ReadFromJsonAsync<BoardDetailsDto>())!;
    private static async Task<BoardDetailsDto> Get(HttpClient client, Guid id) => (await client.GetFromJsonAsync<BoardDetailsDto>($"/api/boards/{id}"))!;
    private static async Task Add(HttpClient client, BoardDetailsDto board, User user) =>
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"/api/boards/{board.Id}/members", new AddMemberRequest { Email = user.Email.ToUpperInvariant(), ExpectedBoardVersion = board.Version })).StatusCode);
    private static string Query(string version) => "?expectedBoardVersion=" + Uri.EscapeDataString(version);

    [SqlFact]
    public async Task BoardLifecycleCreatesAdministratorAndPersistsEdits()
    {
        using var app = ApiFoundationTests.Factory(fixture.ConnectionString); var user = await UserAsync(); using var client = As(app, user);
        var board = await Create(client); Assert.Equal("Командна дошка", board.Name); Assert.Equal("Опис", board.Description);
        Assert.Equal(BoardRole.Admin, board.Role); Assert.True(board.CanCreateCards); Assert.True(board.CanDeleteCards);
        Assert.Equal(8, Convert.FromBase64String(board.Version).Length);
        var list = (await client.GetFromJsonAsync<List<BoardSummaryDto>>("/api/boards"))!; Assert.Single(list); Assert.Equal(board.Id, list[0].Id);
        var members = (await client.GetFromJsonAsync<List<MemberDto>>($"/api/boards/{board.Id}/members"))!;
        Assert.Single(members); Assert.Equal(user.Id, members[0].User.Id); Assert.Equal(BoardRole.Admin, members[0].Role);
        var edited = await client.PutAsJsonAsync($"/api/boards/{board.Id}", new UpdateBoardRequest { Name = "Нова назва", Description = "Новий опис", ExpectedBoardVersion = board.Version });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode); var next = await Get(client, board.Id);
        Assert.Equal("Нова назва", next.Name); Assert.NotEqual(board.Version, next.Version);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/boards/{board.Id}" + Query(next.Version))).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<List<BoardSummaryDto>>("/api/boards"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/boards/{board.Id}")).StatusCode);
    }

    [SqlFact]
    public async Task EveryBoardRouteRequiresAuthentication()
    {
        using var app = ApiFoundationTests.Factory(fixture.ConnectionString); using var client = app.CreateClient();
        var id = Guid.NewGuid();
        foreach (var (method, path) in new[] { (HttpMethod.Get,"/api/boards"), (HttpMethod.Post,"/api/boards"),
            (HttpMethod.Get,$"/api/boards/{id}"), (HttpMethod.Put,$"/api/boards/{id}"), (HttpMethod.Delete,$"/api/boards/{id}"),
            (HttpMethod.Get,$"/api/boards/{id}/members"), (HttpMethod.Post,$"/api/boards/{id}/members"),
            (HttpMethod.Patch,$"/api/boards/{id}/members/{id}"), (HttpMethod.Delete,$"/api/boards/{id}/members/{id}") })
        {
            using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(new { }) };
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(request)).StatusCode);
        }
    }

    [SqlFact]
    public async Task OutsiderCannotReadOrMutateBoardOrLearnItsVersion()
    {
        using var app = ApiFoundationTests.Factory(fixture.ConnectionString); using var admin = As(app, await UserAsync()); using var stranger = As(app, await UserAsync());
        var board = await Create(admin); Assert.Empty((await stranger.GetFromJsonAsync<List<BoardSummaryDto>>("/api/boards"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/boards/{board.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/boards/{board.Id}/members")).StatusCode);
        var stale = Convert.ToBase64String(new byte[8]);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PutAsJsonAsync($"/api/boards/{board.Id}", new UpdateBoardRequest { Name = "attack", ExpectedBoardVersion = stale })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsJsonAsync($"/api/boards/{board.Id}/members", new AddMemberRequest { Email = "nobody@example.test", ExpectedBoardVersion = stale })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PatchAsJsonAsync($"/api/boards/{board.Id}/members/{Guid.NewGuid()}", new UpdateMemberPermissionsRequest { ExpectedBoardVersion = stale })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.DeleteAsync($"/api/boards/{board.Id}" + Query(stale))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.DeleteAsync($"/api/boards/{board.Id}/members/{Guid.NewGuid()}" + Query(stale))).StatusCode);
    }

    [SqlFact]
    public async Task MembersReadButAllAdministrativeMutationsAreForbidden()
    {
        using var app = ApiFoundationTests.Factory(fixture.ConnectionString); var person = await UserAsync(); using var admin = As(app, await UserAsync()); using var member = As(app, person);
        var board = await Create(admin); await Add(admin, board, person); board = await Get(member, board.Id);
        Assert.Equal(BoardRole.Member, board.Role); Assert.True(board.CanCreateCards); Assert.True(board.CanDeleteCards);
        Assert.Equal(2, (await member.GetFromJsonAsync<List<MemberDto>>($"/api/boards/{board.Id}/members"))!.Count);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync($"/api/boards/{board.Id}", new UpdateBoardRequest { Name = "attack", ExpectedBoardVersion = board.Version })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.DeleteAsync($"/api/boards/{board.Id}" + Query(board.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync($"/api/boards/{board.Id}/members", new AddMemberRequest { Email = person.Email, ExpectedBoardVersion = board.Version })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PatchAsJsonAsync($"/api/boards/{board.Id}/members/{person.Id}", new UpdateMemberPermissionsRequest { ExpectedBoardVersion = board.Version })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.DeleteAsync($"/api/boards/{board.Id}/members/{person.Id}" + Query(board.Version))).StatusCode);
    }

    [SqlFact]
    public async Task MembershipRejectsDuplicatesAndMissingUsersAndProtectsAdmin()
    {
        using var app = ApiFoundationTests.Factory(fixture.ConnectionString); var owner = await UserAsync(); var person = await UserAsync(); using var admin = As(app, owner);
        var board = await Create(admin);
        var missing = await admin.PostAsJsonAsync($"/api/boards/{board.Id}/members", new AddMemberRequest { Email = "missing@example.test", ExpectedBoardVersion = board.Version });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode); Assert.Equal("user_not_found", (await missing.Content.ReadFromJsonAsync<ApiProblem>())!.Code);
        Assert.Equal(board.Version, (await Get(admin, board.Id)).Version); // Failed mutation rolls back the touch.
        await Add(admin, board, person); board = await Get(admin, board.Id);
        var duplicate = await admin.PostAsJsonAsync($"/api/boards/{board.Id}/members", new AddMemberRequest { Email = person.Email, ExpectedBoardVersion = board.Version });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode); Assert.Equal("member_exists", (await duplicate.Content.ReadFromJsonAsync<ApiProblem>())!.Code);
        var protectedAdmin = await admin.DeleteAsync($"/api/boards/{board.Id}/members/{owner.Id}" + Query(board.Version));
        Assert.Equal(HttpStatusCode.Conflict, protectedAdmin.StatusCode); Assert.Equal("admin_protected", (await protectedAdmin.Content.ReadFromJsonAsync<ApiProblem>())!.Code);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PatchAsJsonAsync($"/api/boards/{board.Id}/members/{owner.Id}", new UpdateMemberPermissionsRequest { ExpectedBoardVersion = board.Version })).StatusCode);
        Assert.Equal(2, (await admin.GetFromJsonAsync<List<MemberDto>>($"/api/boards/{board.Id}/members"))!.Count);
    }

    [SqlFact]
    public async Task PermissionsAndRevocationAffectNextRequestsWithSameToken()
    {
        using var app = ApiFoundationTests.Factory(fixture.ConnectionString); var person = await UserAsync(); using var admin = As(app, await UserAsync()); using var member = As(app, person);
        var board = await Create(admin); await Add(admin, board, person); board = await Get(admin, board.Id);
        var changed = await admin.PatchAsJsonAsync($"/api/boards/{board.Id}/members/{person.Id}", new UpdateMemberPermissionsRequest { CanCreateCards = false, CanDeleteCards = true, ExpectedBoardVersion = board.Version });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode); var view = await Get(member, board.Id);
        Assert.False(view.CanCreateCards); Assert.True(view.CanDeleteCards); Assert.NotEqual(board.Version, view.Version);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/boards/{board.Id}/members/{person.Id}" + Query(view.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync($"/api/boards/{board.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync($"/api/boards/{board.Id}/members")).StatusCode);
        Assert.Empty((await member.GetFromJsonAsync<List<BoardSummaryDto>>("/api/boards"))!);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/api/auth/me")).StatusCode); // The account token remains valid.
    }

    [SqlFact]
    public async Task InvalidInputAndStaleVersionsCannotChangeBoard()
    {
        using var app = ApiFoundationTests.Factory(fixture.ConnectionString); using var admin = As(app, await UserAsync()); var board = await Create(admin);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/boards", new CreateBoardRequest { Name = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/boards", new CreateBoardRequest { Name = new string('x',151) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/boards/{board.Id}", new UpdateBoardRequest { Name = "new", ExpectedBoardVersion = "bad" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.DeleteAsync($"/api/boards/{board.Id}")).StatusCode);
        var update = new UpdateBoardRequest { Name = "first", ExpectedBoardVersion = board.Version };
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/boards/{board.Id}", update)).StatusCode);
        update.Name = "stale";
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"/api/boards/{board.Id}", update)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/boards/{board.Id}" + Query(board.Version))).StatusCode);
        Assert.Equal("first", (await Get(admin, board.Id)).Name);
    }

    [SqlFact]
    public async Task ConcurrentEditorsWithSameVersionHaveOneWinner()
    {
        using var app = ApiFoundationTests.Factory(fixture.ConnectionString); using var admin = As(app, await UserAsync()); var board = await Create(admin);
        var results = await Task.WhenAll(admin.PutAsJsonAsync($"/api/boards/{board.Id}", new UpdateBoardRequest { Name = "A", ExpectedBoardVersion = board.Version }),
            admin.PutAsJsonAsync($"/api/boards/{board.Id}", new UpdateBoardRequest { Name = "B", ExpectedBoardVersion = board.Version }));
        Assert.Single(results, x => x.StatusCode == HttpStatusCode.OK); Assert.Single(results, x => x.StatusCode == HttpStatusCode.Conflict);
    }

    private async Task<(Card Card, Comment Comment, Tag Tag)> SeedGraph(Guid boardId, User author)
    {
        await using var db = fixture.CreateContext(); var col = new BoardColumn { BoardId = boardId, Name = "Todo" };
        var card = new Card { ColumnId = col.Id, Title = "Картка", AssigneeId = author.Id, CreatedById = author.Id };
        var tag = new Tag { BoardId = boardId, Name = "Тег", NormalizedName = "ТЕГ" };
        var comment = new Comment { CardId = card.Id, AuthorId = author.Id, Text = "Історія" };
        db.AddRange(col,card,tag,comment,new CardTag { CardId = card.Id, TagId = tag.Id },new CardImage
            { CardId = card.Id, ContentType = "image/png", ThumbnailContentType = "image/png", OriginalName = "test.png", SizeBytes = 1, OriginalData = [1], ThumbnailData = [1] });
        await db.SaveChangesAsync(); return (card,comment,tag);
    }

    [SqlFact]
    public async Task RevocationClearsOnlyThisBoardsAssignmentsAndPreservesHistory()
    {
        using var app = ApiFoundationTests.Factory(fixture.ConnectionString); var person = await UserAsync(); using var admin = As(app, await UserAsync());
        var first = await Create(admin); var other = await Create(admin); await Add(admin, first, person); await Add(admin, other, person);
        var a = await SeedGraph(first.Id, person); var b = await SeedGraph(other.Id, person); first = await Get(admin, first.Id);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/boards/{first.Id}/members/{person.Id}" + Query(first.Version))).StatusCode);
        await using var db = fixture.CreateContext(); var cleared = await db.Cards.SingleAsync(x => x.Id == a.Card.Id);
        Assert.Null(cleared.AssigneeId); Assert.Equal(person.Id, cleared.CreatedById); Assert.NotEqual(a.Card.RowVersion, cleared.RowVersion);
        Assert.Equal(person.Id, (await db.Cards.SingleAsync(x => x.Id == b.Card.Id)).AssigneeId);
        Assert.True(await db.Comments.AnyAsync(x => x.Id == a.Comment.Id && x.AuthorId == person.Id));
        Assert.True(await db.BoardMembers.AnyAsync(x => x.BoardId == other.Id && x.UserId == person.Id));
    }

    [SqlFact]
    public async Task DeletingFullBoardRemovesItsGraphAndPreservesOtherBoardAndUsers()
    {
        using var app = ApiFoundationTests.Factory(fixture.ConnectionString); var owner = await UserAsync(); using var admin = As(app, owner);
        var first = await Create(admin); var other = await Create(admin); var a = await SeedGraph(first.Id,owner); var b = await SeedGraph(other.Id,owner);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/boards/{first.Id}" + Query(first.Version))).StatusCode);
        await using var db = fixture.CreateContext();
        Assert.False(await db.Boards.AnyAsync(x => x.Id == first.Id)); Assert.False(await db.Columns.AnyAsync(x => x.BoardId == first.Id));
        Assert.False(await db.Cards.AnyAsync(x => x.Id == a.Card.Id)); Assert.False(await db.CardImages.AnyAsync(x => x.CardId == a.Card.Id));
        Assert.False(await db.CardTags.AnyAsync(x => x.CardId == a.Card.Id)); Assert.False(await db.Tags.AnyAsync(x => x.Id == a.Tag.Id));
        Assert.False(await db.Comments.AnyAsync(x => x.Id == a.Comment.Id)); Assert.False(await db.BoardMembers.AnyAsync(x => x.BoardId == first.Id));
        Assert.True(await db.Cards.AnyAsync(x => x.Id == b.Card.Id)); Assert.True(await db.Users.AnyAsync(x => x.Id == owner.Id));
    }
    [SqlFact]
    public async Task FailedBoardDeletionRollsBackEntireGraphAndVersion()
    {
        using var app = ApiFoundationTests.Factory(fixture.ConnectionString); var owner = await UserAsync(); using var admin = As(app,owner);
        var board = await Create(admin); var graph = await SeedGraph(board.Id, owner);
        await using var db = fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER [Ph03DeleteFailure] ON [Boards] AFTER DELETE AS THROW 50001, 'forced test failure', 1;");
        try
        {
            Assert.Equal(HttpStatusCode.InternalServerError, (await admin.DeleteAsync($"/api/boards/{board.Id}" + Query(board.Version))).StatusCode);
            Assert.Equal(board.Version, (await Get(admin, board.Id)).Version);
            Assert.True(await db.CardImages.AnyAsync(x => x.CardId == graph.Card.Id));
            Assert.True(await db.CardTags.AnyAsync(x => x.CardId == graph.Card.Id));
            Assert.True(await db.Comments.AnyAsync(x => x.Id == graph.Comment.Id));
            Assert.True(await db.Cards.AnyAsync(x => x.Id == graph.Card.Id));
            Assert.True(await db.BoardMembers.AnyAsync(x => x.BoardId == board.Id));
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER [Ph03DeleteFailure]"); }
    }

    [SqlFact]
    public async Task FailedRevocationRollsBackAssignmentsMembershipAndVersion()
    {
        using var app = ApiFoundationTests.Factory(fixture.ConnectionString); var person = await UserAsync(); using var admin = As(app, await UserAsync());
        var board = await Create(admin); await Add(admin,board,person); board = await Get(admin,board.Id); var graph = await SeedGraph(board.Id,person);
        await using var db = fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE [Ph03Hold] ([BoardId] uniqueidentifier NOT NULL, [UserId] uniqueidentifier NOT NULL, FOREIGN KEY ([BoardId],[UserId]) REFERENCES [BoardMembers]([BoardId],[UserId]));");
        await db.Database.ExecuteSqlAsync($"INSERT INTO [Ph03Hold] VALUES ({board.Id},{person.Id});");
        try
        {
            Assert.Equal(HttpStatusCode.InternalServerError, (await admin.DeleteAsync($"/api/boards/{board.Id}/members/{person.Id}" + Query(board.Version))).StatusCode);
            Assert.Equal(board.Version, (await Get(admin,board.Id)).Version);
            var card = await db.Cards.AsNoTracking().SingleAsync(x => x.Id == graph.Card.Id);
            Assert.Equal(person.Id,card.AssigneeId); Assert.Equal(graph.Card.RowVersion,card.RowVersion);
            Assert.True(await db.BoardMembers.AnyAsync(x => x.BoardId == board.Id && x.UserId == person.Id));
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TABLE [Ph03Hold]"); }
    }

    [SqlFact]
    public async Task MemberIdFromAnotherBoardCannotBeChangedOrRemoved()
    {
        using var app = ApiFoundationTests.Factory(fixture.ConnectionString); var person = await UserAsync(); using var admin = As(app,await UserAsync());
        var a = await Create(admin); var b = await Create(admin); await Add(admin,b,person);
        Assert.Equal(HttpStatusCode.NotFound,(await admin.PatchAsJsonAsync($"/api/boards/{a.Id}/members/{person.Id}",new UpdateMemberPermissionsRequest { ExpectedBoardVersion = a.Version })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await admin.DeleteAsync($"/api/boards/{a.Id}/members/{person.Id}"+Query(a.Version))).StatusCode);
        await using var db = fixture.CreateContext(); var member = await db.BoardMembers.SingleAsync(x => x.BoardId == b.Id && x.UserId == person.Id);
        Assert.True(member.CanCreateCards); Assert.True(member.CanDeleteCards); Assert.Equal(a.Version,(await Get(admin,a.Id)).Version);
    }

}
