using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IoRecorder.Core;
using Microsoft.Win32;

namespace IoRecorder.App;

// 01: the application to measure, chosen from the Start menu and started once the recording runs (AppLauncher).
public partial class MainWindow
{
    private AppLaunch? _launchApp;

    private sealed class AppItem(AppLaunch app,string detail) : INotifyPropertyChanged
    {
        public AppLaunch App { get; } = app;
        public string Name => App.Name;
        public string Detail { get; } = detail;
        private ImageSource? _icon;
        public ImageSource? Icon { get => _icon; set { _icon=value; PropertyChanged?.Invoke(this,new(nameof(Icon))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    // 「アプリを選ぶ…」, or 「キャンセル」 once an application is chosen (it is no longer measured).
    private void AppButtonClick(object sender,RoutedEventArgs e)
    {
        if(_launchApp is null) { AppDialog().ShowDialog(); return; }
        SetLaunchApp(null); SaveSettings();
    }
    // Two lists to choose from: the Start menu applications and the programs running now. Either way a program is chosen
    // (not one process), so it is followed by its name and folder, and it can be started for the recording.
    private Window AppDialog(int tab=0)
    {
        var lists=new List<AppItem>?[2];
        int current=tab;
        var list=new ListBox { BorderThickness=new Thickness(0),Margin=new Thickness(8,8,8,0),ItemTemplate=AppTemplate() };
        ScrollViewer.SetHorizontalScrollBarVisibility(list,ScrollBarVisibility.Disabled);
        var filter=new TextBox { Margin=new Thickness(12,10,12,0),Tag="名前・場所で絞り込み" };
        void Show()
        {
            string q=filter.Text.Trim(); var all=lists[current] ?? [];
            list.ItemsSource=q.Length==0?all:all.Where(a=>a.Name.Contains(q,StringComparison.CurrentCultureIgnoreCase) || a.Detail.Contains(q,StringComparison.OrdinalIgnoreCase)).ToList();
            if(_launchApp is { } chosen) list.SelectedItem=all.FirstOrDefault(a=>a.App.Path.Equals(chosen.Path,StringComparison.OrdinalIgnoreCase));
        }
        filter.TextChanged+=(s,args)=>Show();
        var note=new TextBlock { Margin=new Thickness(14,8,14,0),FontSize=11,Foreground=(Brush)FindResource("Muted"),TextWrapping=TextWrapping.Wrap };
        var installed=new Button { Content="インストール済みのアプリ",Padding=new Thickness(14,7,14,7) };
        var running=new Button { Content="実行中のプロセス",Padding=new Thickness(14,7,14,7),Margin=new Thickness(6,0,0,0) };
        var tabs=new StackPanel { Orientation=Orientation.Horizontal,Margin=new Thickness(12,12,12,0) }; tabs.Children.Add(installed); tabs.Children.Add(running);
        var pick=new Button { Content="このアプリを選ぶ",Padding=new Thickness(14,8,14,8),Style=(Style)FindResource("Primary"),HorizontalAlignment=HorizontalAlignment.Right };
        var browse=new Button { Content="一覧にないアプリを選ぶ…",Padding=new Thickness(12,8,12,8),Margin=new Thickness(0,0,8,0) };
        var buttons=new DockPanel { Margin=new Thickness(12) }; DockPanel.SetDock(browse,Dock.Left); buttons.Children.Add(browse); buttons.Children.Add(pick);
        var panel=new DockPanel();
        foreach(var top in new FrameworkElement[] { tabs,filter,note }) { DockPanel.SetDock(top,Dock.Top); panel.Children.Add(top); }
        DockPanel.SetDock(buttons,Dock.Bottom); panel.Children.Add(buttons); panel.Children.Add(list);
        var dialog=new Window { Owner=this,Title="計測対象のアプリケーションを選択",Width=620,Height=680,WindowStartupLocation=WindowStartupLocation.CenterOwner,Content=panel,Icon=Icon,ShowInTaskbar=false };
        DarkTitleBar.Apply(dialog);
        void Choose(AppLaunch app) { SetLaunchApp(app); SaveSettings(); dialog.Close(); }
        pick.Click+=(s,args)=>{ if(list.SelectedItem is AppItem item) Choose(item.App); };
        list.MouseDoubleClick+=(s,args)=>{ if(list.SelectedItem is AppItem item) Choose(item.App); };
        browse.Click+=(s,args)=>
        {
            var d=new OpenFileDialog { Title="計測対象のアプリケーション（プログラムかショートカット）",Filter="プログラム・ショートカット (*.exe;*.lnk)|*.exe;*.lnk",InitialDirectory=Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) };
            if(d.ShowDialog(dialog)!=true) return;
            if(AppLauncher.FromFile(d.FileName) is { } app && File.Exists(app.Path)) Choose(app);
            else MessageBox.Show(dialog,"このファイルからはアプリを選べません: "+d.FileName,"IO Recorder",MessageBoxButton.OK,MessageBoxImage.Warning);
        };
        string[] notes=
        [
            "スタートメニューのアプリ {0:N0} 件。ダブルクリックでも選べます。選んだアプリのプログラムと、そのアプリが起動したプロセス、同じフォルダのプログラムを記録します。",
            "いま動いているプログラム {0:N0} 件（ウィンドウのあるものが上）。選ぶと、そのプログラムを、インストール済みのアプリから選んだときと同じように記録します。起動したときの読み込みも記録するには、アプリを閉じてから「計測を開始してアプリを起動」を押してください。"
        ];
        async Task SelectTab(int index)
        {
            current=index;
            foreach(var (button,i) in new[] { (installed,0),(running,1) })
            { button.Background=i==index?(Brush)FindResource("Highlight"):Brushes.Transparent; button.BorderBrush=i==index?(Brush)FindResource("Accent"):(Brush)FindResource("Border"); }
            if(lists[index] is null)
            {
                note.Text=index==0?"スタートメニューのアプリを読み込んでいます…":"動いているプログラムを調べています…"; list.ItemsSource=null;
                var items=await Task.Run(()=>
                {
                    try
                    {
                        return index==0?AppLauncher.StartMenu().Select(a=>new AppItem(a,a.Path)).ToList()
                            :AppLauncher.Running().Select(r=>new AppItem(r.App,$"PID {r.Pid}{(r.Count>1?$" ほか {r.Count-1} 個":"")}{(r.Title is { } title?" · "+title:"")}\n{r.App.Path}")).ToList();
                    }
                    catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { return new List<AppItem>(); }
                });
                lists[index]=items;
                // Icons come from the shell one by one; the list is usable before they arrive.
                var loader=new Thread(()=>{ foreach(var item in items) { var icon=ShellIcon.Of(item.App.Path); if(icon is not null) dialog.Dispatcher.BeginInvoke(new Action(()=>item.Icon=icon)); } }) { IsBackground=true };
                loader.SetApartmentState(ApartmentState.STA); loader.Start();
            }
            if(current!=index) return;
            note.Text=string.Format(notes[index],lists[index]!.Count); Show();
        }
        installed.Click+=async(s,args)=>await SelectTab(0);
        running.Click+=async(s,args)=>await SelectTab(1);
        dialog.Loaded+=async(s,args)=>{ filter.Focus(); await SelectTab(tab); };
        return dialog;
    }
    private static DataTemplate AppTemplate()
    {
        var row=new FrameworkElementFactory(typeof(DockPanel));
        var image=new FrameworkElementFactory(typeof(Image));
        image.SetValue(WidthProperty,24.0); image.SetValue(HeightProperty,24.0); image.SetValue(MarginProperty,new Thickness(2,2,10,2));
        image.SetValue(DockPanel.DockProperty,Dock.Left); image.SetBinding(Image.SourceProperty,new System.Windows.Data.Binding(nameof(AppItem.Icon)));
        var text=new FrameworkElementFactory(typeof(StackPanel));
        var name=new FrameworkElementFactory(typeof(TextBlock)); name.SetBinding(TextBlock.TextProperty,new System.Windows.Data.Binding(nameof(AppItem.Name)));
        var detail=new FrameworkElementFactory(typeof(TextBlock)); detail.SetBinding(TextBlock.TextProperty,new System.Windows.Data.Binding(nameof(AppItem.Detail)));
        detail.SetValue(TextBlock.FontSizeProperty,10.5); detail.SetValue(TextBlock.ForegroundProperty,new SolidColorBrush(Color.FromRgb(0x8A,0x9C,0xB8))); detail.SetValue(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis);
        text.AppendChild(name); text.AppendChild(detail);
        row.AppendChild(image); row.AppendChild(text);
        return new DataTemplate { VisualTree=row };
    }
    private void SetLaunchApp(AppLaunch? app) { _launchApp=app; UpdateLaunchUi(); }
    private void LaunchChanged(object sender,RoutedEventArgs e) { if(IsLoaded) UpdateLaunchUi(); }
    private void UpdateLaunchUi()
    {
        bool chosen=_launchApp is not null;
        AppText.Text=_launchApp is { } app?$"{app.Name}\n{app.Path}":"未選択（対象フォルダだけでも計測できます）";
        AppIcon.Source=_launchApp is { } shown?ShellIcon.Of(shown.Path):null; AppIcon.Visibility=AppIcon.Source is null?Visibility.Collapsed:Visibility.Visible;
        LaunchBox.IsEnabled=chosen;
        AppButton.Content=chosen?"キャンセル":"アプリを選ぶ…";
        AppButton.ToolTip=chosen?"このアプリを測るのをやめます。別のアプリにするときは、キャンセルしてから選び直します。":null;
        StartButton.Content=chosen && LaunchBox.IsChecked==true?"●  計測を開始してアプリを起動":"●  計測を開始";
        UpdateLabelHint();
    }
}

// The icon the shell shows for a file (24 px), or null.
internal static class ShellIcon
{
    public static ImageSource? Of(string path)
    {
        var info=new ShellFileInfo();
        if(SHGetFileInfo(path,0,ref info,(uint)Marshal.SizeOf<ShellFileInfo>(),0x100)==0 || info.Icon==0) return null; // SHGFI_ICON (large)
        try { var image=Imaging.CreateBitmapSourceFromHIcon(info.Icon,Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions()); image.Freeze(); return image; }
        catch(COMException) { return null; }
        finally { DestroyIcon(info.Icon); }
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct ShellFileInfo
    {
        public nint Icon; public int Index; public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=80)] public string TypeName;
    }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)] private static extern nint SHGetFileInfo(string path,uint attributes,ref ShellFileInfo info,uint size,uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
}
