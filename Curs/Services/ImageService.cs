using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using KanbanContracts;

namespace Curs.Services;

public sealed class ImageService : IDisposable
{
    private readonly ApiClient api;
    private readonly SessionService session;
    private readonly SemaphoreSlim downloads = new(4);
    private readonly Dictionary<(Guid, string), byte[]> cache = [];
    private readonly Queue<(Guid, string)> order = new();
    private readonly object gate = new();
    private int generation, size;
    private string? cacheOwner;
    public int CachedCount { get { lock (gate) return cache.Count; } }
    public ImageService(ApiClient api, SessionService session) { this.api=api; this.session=session; session.Changed+=Clear; }
    public async Task<byte[]> Read(Guid card,string version,bool thumbnail,CancellationToken token)
    {
        var key=(card,version); int epoch; string? authorization;
        lock(gate) { authorization=session.AccessToken; if(cacheOwner!=authorization)Clear(); epoch=generation; if(authorization is null)throw new OperationCanceledException(); if(thumbnail&&cache.TryGetValue(key,out var hit))return hit; }
        if(authorization is null)throw new OperationCanceledException();
        await downloads.WaitAsync(token);
        try
        {
            lock(gate){if(epoch!=generation||authorization!=session.AccessToken)throw new OperationCanceledException();if(thumbnail&&cache.TryGetValue(key,out var hit))return hit;}
            var bytes=await api.BytesAsync($"api/cards/{card}/image"+(thumbnail?"/thumbnail":""),token);
            token.ThrowIfCancellationRequested();
            lock(gate)
            {
                if(epoch!=generation||authorization!=session.AccessToken)throw new OperationCanceledException();
                if(thumbnail&&!cache.ContainsKey(key))
                {
                    cache.Add(key,bytes);order.Enqueue(key);size+=bytes.Length;
                    while(size>8*1024*1024||cache.Count>128){var old=order.Dequeue();if(cache.Remove(old,out var removed))size-=removed.Length;}
                }
            }
            return bytes;
        }
        finally{downloads.Release();}
    }
    public Task<ImageMutationDto?> Upload(Guid card,byte[] bytes,string filename,ResourceVersionQuery version,CancellationToken token)
    {
        if(bytes.Length>ImageLimits.MaxBytes)throw new InvalidOperationException("Фото: не більше 5 МіБ.");
        return Send();
        async Task<ImageMutationDto?> Send()
        {
            using var form=new MultipartFormDataContent();form.Add(new StringContent(version.ExpectedBoardVersion),"ExpectedBoardVersion");form.Add(new StringContent(version.ExpectedVersion),"ExpectedVersion");form.Add(new ByteArrayContent(bytes),"File",Path.GetFileName(filename));
            return await api.MultipartAsync<ImageMutationDto>($"api/cards/{card}/image",form,token);
        }
    }
    public Task<object?> Delete(Guid card,ResourceVersionQuery version,CancellationToken token)=>api.SendAsync<object>(HttpMethod.Delete,$"api/cards/{card}/image?expectedBoardVersion={Uri.EscapeDataString(version.ExpectedBoardVersion)}&expectedVersion={Uri.EscapeDataString(version.ExpectedVersion)}",cancellationToken:token);
    public void Clear(){lock(gate){generation++;cacheOwner=session.AccessToken;size=0;cache.Clear();order.Clear();}}
    public void Dispose(){session.Changed-=Clear;Clear();}
}
