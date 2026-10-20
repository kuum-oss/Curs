using KanbanApi.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;
namespace KanbanApi.Tests;
public sealed class ChatCardMigrationTests
{
    [SqlFact]
    public async Task DowngradePreservesLinksAndEmptyRoundTripKeepsOriginalText()
    {
        var f=new SqlFixture();await f.InitializeAsync();try
        {
            await using var db=f.CreateContext();var u=new User{Email="shared@example.test",NormalizedEmail="SHARED@EXAMPLE.TEST",Name="Історія",PasswordHash="unused"};var chat=new DirectChat{FirstUserId=u.Id};var message=new DirectMessage{ChatId=chat.Id,SenderId=u.Id,ClientMessageId=Guid.NewGuid(),SharedCardId=Guid.NewGuid(),Text="Початковий текст"};db.AddRange(u,chat,message);await db.SaveChangesAsync();
            var ex=await Assert.ThrowsAsync<SqlException>(()=>db.GetService<IMigrator>().MigrateAsync("20261019192346_NotificationCenter"));Assert.Equal(50008,ex.Number);Assert.NotNull((await db.DirectMessages.AsNoTracking().SingleAsync()).SharedCardId);
            await db.DirectMessages.ExecuteUpdateAsync(s=>s.SetProperty(m=>m.SharedCardId,(Guid?)null));await db.GetService<IMigrator>().MigrateAsync("20261019192346_NotificationCenter");await db.Database.MigrateAsync();Assert.Equal("Початковий текст",(await db.DirectMessages.AsNoTracking().SingleAsync()).Text);
        }finally{await f.DisposeAsync();}
    }
}
