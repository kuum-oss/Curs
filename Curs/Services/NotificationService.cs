using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using KanbanContracts;
namespace Curs.Services;
public sealed class NotificationService(ApiClient api)
{
    public async Task<NotificationsDto> List(bool unread,long? before,CancellationToken t)=>await api.SendAsync<NotificationsDto>(HttpMethod.Get,$"api/notifications?unreadOnly={unread.ToString().ToLowerInvariant()}"+(before is {} id?$"&beforeId={id}":""),cancellationToken:t)??throw new InvalidOperationException();
    public async Task<NotificationDto> Open(long id,CancellationToken t)=>await api.SendAsync<NotificationDto>(HttpMethod.Post,$"api/notifications/{id}/open",cancellationToken:t)??throw new InvalidOperationException();
    public Task Read(long id,CancellationToken t)=>api.SendAsync<object>(HttpMethod.Post,$"api/notifications/{id}/read",cancellationToken:t);
    public Task ReadAll(long through,CancellationToken t)=>api.SendAsync<object>(HttpMethod.Post,"api/notifications/read-all",new ReadNotificationsRequest{ThroughId=through},t);
}
