using System.Net;
using System.Net.Http.Json;
using System.Text;
using KanbanApi.Application;
using KanbanApi.Domain;
using KanbanContracts;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;
namespace KanbanApi.Tests;
[Collection("sql")]
public sealed class BackupTests(SqlFixture fixture)
{
    [SqlFact]
    public async Task FullRoundTripPreservesContentAndPhotosWithoutGrantingMembershipOrImpersonation()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);var g=await new NotificationTests(fixture).Pair();using var a=ChecklistTests.Client(app,g.Owner);var card=await ChecklistTests.Create(a,g.Board.Id,g.Column.Id,ChecklistTests.Items());var photo=await new ImageProcessor().Process(ImageProcessorTests.ScreenshotLikePng(),default);
        await using(var db=fixture.CreateContext())
        {var b=await db.Boards.SingleAsync(x=>x.Id==g.Board.Id);b.CoverOriginalData=photo.Original;b.CoverThumbnailData=photo.Thumbnail;b.CoverContentType=photo.ContentType;var c=await db.Cards.SingleAsync(x=>x.Id==card.Card.Id);c.AssigneeId=g.Peer.Id;c.Description="Незмінний опис\nДругий рядок";c.IsArchived=true;db.CardImages.Add(new(){CardId=c.Id,ContentType=photo.ContentType,OriginalName="test.png",OriginalData=photo.Original,ThumbnailData=photo.Thumbnail,ThumbnailContentType="image/png",SizeBytes=photo.Original.Length});db.Comments.Add(new(){CardId=c.Id,AuthorId=g.Peer.Id,Text="Незмінний коментар"});await db.SaveChangesAsync();}
        var backup=(await a.GetFromJsonAsync<BoardBackup>($"/api/boards/{g.Board.Id}/backup"))!;BackupService.Validate(backup);var source=Assert.Single(backup.Data.Cards);Assert.Equal(g.Peer.Name,source.AssigneeName);Assert.NotNull(source.Image);Assert.NotNull(backup.Data.Cover);
        var request=new RestoreBoardRequest(Guid.NewGuid(),backup);var responses=await Task.WhenAll(a.PostAsJsonAsync("/api/backups/restore",request),a.PostAsJsonAsync("/api/backups/restore",request));foreach(var response in responses)response.EnsureSuccessStatusCode();var result=(await responses[0].Content.ReadFromJsonAsync<RestoreBoardResult>())!;var retry=(await responses[1].Content.ReadFromJsonAsync<RestoreBoardResult>())!;Assert.Equal(result.BoardId,retry.BoardId);Assert.NotEqual(g.Board.Id,result.BoardId);
        var restored=(await a.GetFromJsonAsync<BoardBackup>($"/api/boards/{result.BoardId}/backup"))!;var target=Assert.Single(restored.Data.Cards);Assert.NotEqual(source.Key,target.Key);Assert.Equal(source.Title,target.Title);Assert.Equal(source.Description,target.Description);Assert.Equal(source.AssigneeName,target.AssigneeName);Assert.Equal(source.Checklist,target.Checklist);Assert.Equal(source.Comments,target.Comments);Assert.Equal(source.Image,target.Image);Assert.Equal(backup.Data.Cover,restored.Data.Cover);Assert.True(target.IsArchived);
        await using(var db=fixture.CreateContext()){Assert.Single(await db.BoardMembers.Where(x=>x.BoardId==result.BoardId).ToListAsync());var c=await db.Cards.SingleAsync(x=>x.Id==target.Key);Assert.Null(c.AssigneeId);var comment=await db.Comments.SingleAsync(x=>x.CardId==c.Id);Assert.Null(comment.AuthorId);Assert.Equal(g.Peer.Name,comment.SourceAuthorName);Assert.Equal(g.Owner.Id,(await db.Boards.SingleAsync(x=>x.Id==result.BoardId)).OwnerId);}
        var details=(await a.GetFromJsonAsync<CardDetailsDto>($"/api/cards/{target.Key}"))!;Assert.Equal(source.AssigneeName,details.Card.SourceAssigneeName);var comments=(await a.GetFromJsonAsync<PagedResult<CommentDto>>($"/api/cards/{target.Key}/comments"))!;Assert.Contains("з копії",comments.Items[0].Author.Name);
    }
    [SqlFact]
    public async Task CorruptionBadGraphOrImageNeverCreatesAPartialBoardAndOperationIdCannotChangePayload()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);var g=await new NotificationTests(fixture).Pair();using var a=ChecklistTests.Client(app,g.Owner);await ChecklistTests.Create(a,g.Board.Id,g.Column.Id);var original=(await a.GetFromJsonAsync<BoardBackup>($"/api/boards/{g.Board.Id}/backup"))!;
        await using var db=fixture.CreateContext();var count=await db.Boards.CountAsync();
        var corrupt=original with{Data=original.Data with{Name="Підміна"}};Assert.Equal(HttpStatusCode.BadRequest,(await a.PostAsJsonAsync("/api/backups/restore",new RestoreBoardRequest(Guid.NewGuid(),corrupt))).StatusCode);
        var invalid=original.Data with{Cards=[original.Data.Cards[0] with{ColumnKey=Guid.NewGuid()}]};Assert.Equal(HttpStatusCode.BadRequest,(await a.PostAsJsonAsync("/api/backups/restore",new RestoreBoardRequest(Guid.NewGuid(),original with{Data=invalid,Checksum=BackupService.Hash(invalid)}))).StatusCode);
        var badImage=original.Data with{Cover=[1,2,3]};Assert.Equal(HttpStatusCode.UnsupportedMediaType,(await a.PostAsJsonAsync("/api/backups/restore",new RestoreBoardRequest(Guid.NewGuid(),original with{Data=badImage,Checksum=BackupService.Hash(badImage)}))).StatusCode);Assert.Equal(count,await db.Boards.CountAsync());
        var op=Guid.NewGuid();(await a.PostAsJsonAsync("/api/backups/restore",new RestoreBoardRequest(op,original))).EnsureSuccessStatusCode();var changed=original.Data with{Name="Інший файл"};Assert.Equal(HttpStatusCode.Conflict,(await a.PostAsJsonAsync("/api/backups/restore",new RestoreBoardRequest(op,original with{Data=changed,Checksum=BackupService.Hash(changed)}))).StatusCode);
    }
    [SqlFact]
    public async Task OnlyBoardMembersCanExportAndAnonymousCannotRestore()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);var g=await new NotificationTests(fixture).Pair();var outsider=await new ChecklistTests(fixture).Seed();using var stranger=ChecklistTests.Client(app,outsider.User);using var a=ChecklistTests.Client(app,g.Owner);var copy=(await a.GetFromJsonAsync<BoardBackup>($"/api/boards/{g.Board.Id}/backup"))!;foreach(var suffix in new[]{"backup","export.csv"})Assert.Equal(HttpStatusCode.NotFound,(await stranger.GetAsync($"/api/boards/{g.Board.Id}/{suffix}")).StatusCode);using var anonymous=app.CreateClient();Assert.Equal(HttpStatusCode.Unauthorized,(await anonymous.PostAsJsonAsync("/api/backups/restore",new RestoreBoardRequest(Guid.NewGuid(),copy))).StatusCode);Assert.Equal(HttpStatusCode.BadRequest,(await a.PostAsJsonAsync("/api/backups/restore",new RestoreBoardRequest(Guid.NewGuid(),copy with{Version=999}))).StatusCode);
    }
    [SqlFact]
    public async Task CsvQuotesUnicodeNewlinesAndNeutralizesSpreadsheetFormulas()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);var g=await new NotificationTests(fixture).Pair();using var a=ChecklistTests.Client(app,g.Owner);var card=await ChecklistTests.Create(a,g.Board.Id,g.Column.Id);await using(var db=fixture.CreateContext()){var c=await db.Cards.SingleAsync(x=>x.Id==card.Card.Id);c.Title="=HYPERLINK(\"bad\")";c.Description="Опис, з комою\r\nДругий рядок";await db.SaveChangesAsync();}var bytes=await a.GetByteArrayAsync($"/api/boards/{g.Board.Id}/export.csv");Assert.True(bytes.AsSpan().StartsWith(new byte[]{239,187,191}));var csv=Encoding.UTF8.GetString(bytes);Assert.Contains("\"'=HYPERLINK(\"\"bad\"\")\"",csv);Assert.Contains("\"Опис, з комою\r\nДругий рядок\"",csv);
    }
}
public sealed class BackupMigrationTests
{
    [SqlFact]
    public async Task DowngradeCannotLoseHistoricalNames()
    {
        var f=new SqlFixture();await f.InitializeAsync();try{await using var db=f.CreateContext();var u=new User{Email="import@test.local",NormalizedEmail="IMPORT@TEST.LOCAL",Name="Тест",PasswordHash="unused"};db.Users.Add(u);db.Boards.Add(new(){OwnerId=u.Id,Name="Імпорт",RestoreChecksum=new('A',64)});await db.SaveChangesAsync();var ex=await Assert.ThrowsAsync<SqlException>(()=>db.GetService<IMigrator>().MigrateAsync("20261019200219_ChatCardSharing"));Assert.Equal(50009,ex.Number);Assert.NotNull((await db.Boards.SingleAsync()).RestoreChecksum);await db.Boards.ExecuteUpdateAsync(s=>s.SetProperty(b=>b.RestoreChecksum,(string?)null));await db.GetService<IMigrator>().MigrateAsync("20261019200219_ChatCardSharing");await db.Database.MigrateAsync();Assert.Single(await db.Boards.ToListAsync());}finally{await f.DisposeAsync();}
    }
}
