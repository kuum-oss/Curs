using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Curs.ViewModels;
namespace Curs.Views;
public partial class ProfileView:UserControl
{
    public ProfileView()=>AvaloniaXamlLoader.Load(this);
    private async void ChooseAvatar(object? sender,RoutedEventArgs e)
    {
        if(DataContext is not ProfileViewModel model||!model.CanEdit)return;
        try{if(await MediaPicker.Pick(this,"Фото профілю") is {} file&&model.CanEdit)await model.Upload(file.Bytes,file.Name);}catch(Exception ex){model.ImageError(ex is ArgumentException?ex.Message:"Не вдалося відкрити фото.");}
    }
}
