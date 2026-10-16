using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using KanbanContracts;
namespace Curs.Views;
internal static class MediaPicker
{
    public static async Task<(byte[] Bytes,string Name)?> Pick(Control control,string title)
    {
        if(TopLevel.GetTopLevel(control) is not {} root)return null;
        var files=await root.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions{Title=title,AllowMultiple=false,FileTypeFilter=[new FilePickerFileType("JPG, PNG, WEBP"){Patterns=["*.jpg","*.jpeg","*.png","*.webp"],MimeTypes=["image/jpeg","image/png","image/webp"]}]});
        if(files.Count==0)return null;
        return await Read(files[0],default);
    }
    public static async Task<(byte[] Bytes,string Name)> Read(IStorageFile file,CancellationToken token)
    {
        await using var input=await file.OpenReadAsync();using var bytes=new MemoryStream();var chunk=new byte[65536];int read;
        while((read=await input.ReadAsync(chunk,token))!=0){if(bytes.Length+read>ImageLimits.MaxBytes)throw new ArgumentException("Фото: не більше 5 МіБ.");await bytes.WriteAsync(chunk.AsMemory(0,read),token);}
        return(bytes.ToArray(),file.Name);
    }
    public static void DragOver(Control target,DragEventArgs e,bool allowed)
    {
        var copy=allowed&&e.DataTransfer.Contains(DataFormat.File)&&(e.DragEffects&DragDropEffects.Copy)!=0;
        e.DragEffects=copy?DragDropEffects.Copy:DragDropEffects.None;
        target.Classes.Set("file-over",copy);e.Handled=true;
    }
    public static IStorageFile SingleFile(DragEventArgs e)
    {
        var files=e.DataTransfer.TryGetFiles();
        if(files is not [IStorageFile file])throw new ArgumentException("Перетягніть один файл JPG, PNG або WEBP.");
        return file;
    }
}
