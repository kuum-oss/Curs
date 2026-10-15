using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Curs.Services;
using KanbanContracts;

namespace Curs.ViewModels;

public sealed class AvatarViewModel : ViewModelBase, IDisposable
{
    private Bitmap? image;
    private bool loading;
    private readonly CancellationTokenSource lifetime=new();
    private static readonly SemaphoreSlim downloads=new(4);
    public UserDto User { get; }
    public string Name=>User.Name;
    public string Initials=>string.Concat(User.Name.Split(' ',StringSplitOptions.RemoveEmptyEntries).Take(2).Select(s=>s[0])).ToUpperInvariant();
    public Bitmap? Image { get=>image; private set{image=value;Notify();Notify(nameof(HasImage));} }
    public bool HasImage=>Image is not null;
    public AvatarViewModel(UserDto user){User=user;}
    public async Task Load(ClientServices services)
    {
        if(!User.HasAvatar||Image is not null||loading||lifetime.IsCancellationRequested)return;loading=true;
        var observed=services.Session.AccessToken;
        try
        {
            await downloads.WaitAsync(lifetime.Token);
            try
            {
                var bytes=await services.Api.BytesAsync($"api/users/{User.Id}/avatar",lifetime.Token);
                lifetime.Token.ThrowIfCancellationRequested();if(observed!=services.Session.AccessToken)return;
                Image=new Bitmap(new MemoryStream(bytes));
            }
            finally{downloads.Release();}
        }
        catch(OperationCanceledException){}catch(Exception){}finally{loading=false;}
    }
    public void Dispose(){lifetime.Cancel();var old=Image;Image=null;old?.Dispose();}
}
