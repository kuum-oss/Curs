using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using KanbanContracts;
namespace Curs.Services;
public sealed class BackupService(ApiClient api)
{
    public Task<byte[]> Export(Guid board,CancellationToken token)=>api.BytesAsync($"api/boards/{board}/backup",token,TimeSpan.FromMinutes(2));
    public Task<byte[]> Csv(Guid board,CancellationToken token)=>api.BytesAsync($"api/boards/{board}/export.csv",token,TimeSpan.FromMinutes(2));
    public async Task<RestoreBoardResult> Restore(Guid operation,BoardBackup data,CancellationToken token)=>(await api.SendAsync<RestoreBoardResult>(HttpMethod.Post,"api/backups/restore",new RestoreBoardRequest(operation,data),token,timeout:TimeSpan.FromMinutes(2)))!;
}
