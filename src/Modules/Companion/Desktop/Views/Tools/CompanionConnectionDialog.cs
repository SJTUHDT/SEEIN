using Seein.Core;
using System.Windows;
using System.Windows.Controls;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;

namespace Seein.Tray;

internal sealed class CompanionConnectionDialog : Window
{
    public CompanionConnectionDialog(Window owner)
    {
        Owner=owner;Title="大肥鱼连接设置";Width=560;SizeToContent=SizeToContent.Height;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty,"WindowBrush");SetResourceReference(ForegroundProperty,"TextBrush");
        var panel=new StackPanel{Margin=new Thickness(24)};Content=panel;
        var settings=CompanionConnectionSettings.Load();
        TextBox Field(string label,string value){panel.Children.Add(new TextBlock{Text=label,Margin=new Thickness(0,12,0,6)});var box=new TextBox{Text=value};panel.Children.Add(box);return box;}
        var directory=Field("大肥鱼安装目录（包含 tools/workbench/server.py）",settings.WorkbenchRoot);
        var astrbot=Field("AstrBot 本机后台地址",settings.AstrBotAddress);
        var qq=Field("QQ 本机后台地址",settings.QqAddress);
        panel.Children.Add(new TextBlock{Text="修改安装目录后，请停止并重新启动桌宠，使对话桥采用新目录。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,0)});
        var feedback=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,0)};panel.Children.Add(feedback);
        var save=new Button{Content="保存并重新连接",Margin=new Thickness(0,16,0,0)};
        save.Click+=(_,_)=>{try{new CompanionConnectionSettings(directory.Text.Trim(),astrbot.Text.Trim(),qq.Text.Trim()).Save();DialogResult=true;}catch(Exception ex)when(ex is ArgumentException or System.IO.IOException or UnauthorizedAccessException){feedback.Text=ex.Message;}};
        panel.Children.Add(save);
    }
}
