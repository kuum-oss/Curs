using KanbanApi.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;
namespace KanbanApi.Tests;
[Collection("sql")]
public sealed class ChatMigrationTests
{
    [SqlFact]
    public async Task DowngradeCannotSilentlyErasePrivateMessages()
    {
        var fixture=new SqlFixture();await fixture.InitializeAsync();
        try
        {
            await using var db=fixture.CreateContext();var a=new User{Email=Guid.NewGuid()+"@example.test",NormalizedEmail=Guid.NewGuid()+"@EXAMPLE.TEST",Name="A",PasswordHash="test-only"};var b=new User{Email=Guid.NewGuid()+"@example.test",NormalizedEmail=Guid.NewGuid()+"@EXAMPLE.TEST",Name="B",PasswordHash="test-only"};var chat=new DirectChat{FirstUserId=a.Id,SecondUserId=b.Id};db.AddRange(a,b,chat,new DirectMessage{ChatId=chat.Id,SenderId=a.Id,ClientMessageId=Guid.NewGuid(),Text="Зберегти"});await db.SaveChangesAsync();
            var error=await Assert.ThrowsAsync<SqlException>(()=>db.GetService<IMigrator>().MigrateAsync("20261017110642_AccountRecoveryWindow"));Assert.Equal(50003,error.Number);Assert.True(await db.DirectMessages.AnyAsync(m=>m.ChatId==chat.Id));
        }
        finally{await fixture.DisposeAsync();}
    }
}
