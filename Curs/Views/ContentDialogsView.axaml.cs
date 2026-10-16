using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Curs.ViewModels;
namespace Curs.Views;
public partial class ContentDialogsView:UserControl
{
    public ContentDialogsView()=>AvaloniaXamlLoader.Load(this);
    private void AvatarAttached(object? sender,Avalonia.VisualTreeAttachmentEventArgs e){if(sender is Control{DataContext:AvatarViewModel avatar}&&DataContext is BoardContentViewModel model)model.LoadAvatar(avatar);}
    private async void ChooseCover(object? sender,RoutedEventArgs e)
    {
        if(DataContext is not BoardContentViewModel model||!model.CanChangeCover)return;var board=model.BoardId;var card=model.EditingCardId;
        try
        {
            if(await MediaPicker.Pick(this,"Фото-обкладинка") is not {} file)return;
            while(model.IsPolling&&model.BoardId==board&&model.EditingCardId==card)await System.Threading.Tasks.Task.Delay(30);
            if(model.BoardId==board&&model.EditingCardId==card&&model.CanChangeCover)await model.UploadCoverAsync(file.Bytes,file.Name);
        }
        catch(Exception ex){model.ImageError(ex is ArgumentException?ex.Message:"Не вдалося відкрити фото.");}
    }
}
