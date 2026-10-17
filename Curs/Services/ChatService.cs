using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using KanbanContracts;

namespace Curs.Services;

public sealed class ChatService(ApiClient api)
{
    public async Task<IReadOnlyList<ChatDto>> List(CancellationToken token) => await api.SendAsync<List<ChatDto>>(HttpMethod.Get,"api/chats",cancellationToken:token)??[];
    public async Task<ChatDto> Start(Guid peer,CancellationToken token) => await api.SendAsync<ChatDto>(HttpMethod.Post,"api/chats",new StartChatRequest{RecipientId=peer},token)??throw new InvalidOperationException();
    public async Task<ChatMessagesDto> Messages(Guid chat,CancellationToken token,long? before=null,long? after=null) => await api.SendAsync<ChatMessagesDto>(HttpMethod.Get,$"api/chats/{chat}/messages"+(before is {} b?$"?beforeId={b}":after is {} a?$"?afterId={a}":""),cancellationToken:token)??throw new InvalidOperationException();
    public async Task<ChatMessageDto> Send(Guid chat,string text,Guid nonce,CancellationToken token) => await api.SendAsync<ChatMessageDto>(HttpMethod.Post,$"api/chats/{chat}/messages",new SendChatMessageRequest{Text=text,ClientMessageId=nonce},token)??throw new InvalidOperationException();
    public Task Read(Guid chat,long id,CancellationToken token) => api.SendAsync<object>(HttpMethod.Post,$"api/chats/{chat}/read",new ChatReadRequest{LastReadMessageId=id},token);
}
