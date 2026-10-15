using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using KanbanContracts;
namespace Curs.Views;
internal static class MediaPicker
{
    public static async Task<(byte[] Bytes,string Name)?> Pick(Control control,string title)
    {
        if(TopLevel.GetTopLevel(control) is not {} root)return null;
        var files=await root.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions{Title=title,AllowMultiple=false,FileTypeFilter=[new FilePickerFileType("JPG, PNG, WEBP"){Patterns=["*.jpg","*.jpeg","*.png","*.webp"],MimeTypes=["image/jpeg","image/png","image/webp"]}]});
        if(files.Count==0)return null;await using var input=await files[0].OpenReadAsync();using var bytes=new MemoryStream();var chunk=new byte[65536];int read;
        while((read=await input.ReadAsync(chunk))!=0){if(bytes.Length+read>ImageLimits.MaxBytes)throw new ArgumentException("Фото: не більше 5 МіБ.");await bytes.WriteAsync(chunk.AsMemory(0,read));}
        return(bytes.ToArray(),files[0].Name);
    }
}
