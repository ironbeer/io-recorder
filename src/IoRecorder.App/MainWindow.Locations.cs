using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IoRecorder.Core;

namespace IoRecorder.App;

// 02 / 03 / 05: the folders the open record is narrowed to (LocationFilter, kept in the record), chosen from a tree of the
// folders the record touched with their amounts.
public partial class MainWindow
{
    private LocationFilter _locations=new();
    // The folders the generated dummy was made for (its replay replays only them).
    private string? _replayLocations;

    private sealed class LocationNode(string path,string name,LocationNode? parent) : INotifyPropertyChanged
    {
        public string Path { get; } = path;
        public string Name { get; } = name;
        public LocationNode? Parent { get; } = parent;
        public List<LocationNode> Children { get; } = [];
        public long ByteCount { get; private set; }
        public long Operations { get; private set; }
        // What is directly in this folder (not in its folders).
        public long OwnBytes { get; private set; }
        public long OwnOperations { get; private set; }
        public void Add(long bytes,long operations) { ByteCount+=bytes; Operations+=operations; }
        public void AddOwn(long bytes,long operations) { OwnBytes+=bytes; OwnOperations+=operations; }
        public string Label=>$"{Name}　　{(ByteCount>0?MainWindow.Bytes(ByteCount):"0")} · {Operations:N0} 件";
        private bool _expanded;
        public bool IsExpanded { get=>_expanded; set { _expanded=value; Changed(nameof(IsExpanded)); } }
        private bool? _checked=false;
        // A folder is chosen with everything in it; a folder with only some of its folders chosen shows a dash.
        public bool? IsChecked { get=>_checked; set=>Set(value ?? false,true,true); }
        public void Set(bool? value,bool down,bool up)
        {
            if(value==_checked) return;
            _checked=value;
            if(down && value.HasValue) foreach(var c in Children) c.Set(value,true,false);
            if(up) Parent?.Recalculate();
            Changed(nameof(IsChecked));
        }
        public void Recalculate()=>Set(Children.All(c=>c.IsChecked==true)?true:Children.All(c=>c.IsChecked==false)?false:null,false,true);
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed(string name)=>PropertyChanged?.Invoke(this,new(name));
    }

    private async void ChooseLocations(object sender,RoutedEventArgs e)
    {
        if(_session is null) { LogText.Text="先に「記録を開く…」で記録を開いてください。"; return; }
        if(_stop is not null) return;
        string session=_session;
        AnalysisResult whole;
        // The tree shows every folder of the whole record, whatever it is narrowed to now.
        try { whole=await Task.Run(()=>SessionAnalyzer.Load(session,_progress)); } catch(Exception ex) { ReportError(ex); return; }
        var current=LocationFilter.Load(session);
        var (dialog,chosen)=LocationDialog(whole,current);
        if(dialog is null) { MessageBox.Show(this,"この記録には、場所の分かるファイルI/Oがありません。","IO Recorder",MessageBoxButton.OK,MessageBoxImage.Information); return; }
        dialog.ShowDialog();
        if(chosen() is not { } filter || filter.Key==current.Key || _session!=session) return;
        try
        {
            LocationFilter.Save(session,filter);
            await LoadSessionAsync(session);
            LogText.Text=filter.IsEmpty?"場所の絞り込みを外しました（すべての場所）。":"場所を絞り込みました: "+filter.Text;
        }
        catch(Exception ex) { ReportError(ex); }
    }
    // The dialog with the folder tree of the whole record (null when no I/O has a known path), and what it chose (null when closed).
    private (Window? Dialog,Func<LocationFilter?> Chosen) LocationDialog(AnalysisResult whole,LocationFilter current)
    {
        var roots=LocationTree(whole.Files);
        if(roots.Count==0) return (null,()=>null);
        void Mark(LocationNode node)
        {
            if(current.IsEmpty || current.Includes(node.Path)) { node.Set(true,true,false); return; }
            foreach(var c in node.Children) Mark(c);
            if(node.Children.Count>0) node.Set(node.Children.All(c=>c.IsChecked==true)?true:node.Children.All(c=>c.IsChecked==false)?false:null,false,false);
        }
        // Open down to what is partly chosen, and through folders that hold only one folder.
        void Expand(LocationNode node) { if(node.IsChecked is null || node.Children.Count==1) { node.IsExpanded=true; foreach(var c in node.Children) Expand(c); } }
        foreach(var r in roots) { Mark(r); r.IsExpanded=true; foreach(var c in r.Children) Expand(c); }
        long unknown=whole.Files.Where(f=>string.IsNullOrEmpty(f.Path)).Sum(f=>f.Reads+f.Writes+f.Metadata);
        var tree=new TreeView { Margin=new Thickness(8,8,8,0),BorderThickness=new Thickness(0),ItemsSource=roots,ItemTemplate=(DataTemplate)FindResource("LocationTemplate"),ItemContainerStyle=(Style)FindResource("LocationItem") };
        VirtualizingPanel.SetIsVirtualizing(tree,true);
        var note=new TextBlock { Margin=new Thickness(14,12,14,0),FontSize=11.5,TextWrapping=TextWrapping.Wrap,Foreground=(Brush)FindResource("Muted"),
            Text="記録がI/Oを出したフォルダと、その量（読み書きしたバイト数・操作の件数）です。チェックを付けた場所の中のI/Oだけで、02の集計、03のダミーと再生、05の比較を行います（この記録に保存します）。"
                +(unknown>0?$"\nパスが分からないI/O（{unknown:N0} 件）は、場所を絞り込むと含まれません。":"") };
        var all=new Button { Content="すべて選ぶ",Padding=new Thickness(10,7,10,7) };
        var none=new Button { Content="すべて外す",Padding=new Thickness(10,7,10,7),Margin=new Thickness(6,0,0,0) };
        var apply=new Button { Content="この場所で絞り込む",Padding=new Thickness(14,8,14,8),Style=(Style)FindResource("Primary"),HorizontalAlignment=HorizontalAlignment.Right };
        var buttons=new DockPanel { Margin=new Thickness(12) }; var left=new StackPanel { Orientation=Orientation.Horizontal }; left.Children.Add(all); left.Children.Add(none);
        DockPanel.SetDock(left,Dock.Left); buttons.Children.Add(left); buttons.Children.Add(apply);
        var panel=new DockPanel(); DockPanel.SetDock(note,Dock.Top); DockPanel.SetDock(buttons,Dock.Bottom); panel.Children.Add(note); panel.Children.Add(buttons); panel.Children.Add(tree);
        var dialog=new Window { Owner=this,Title="場所で絞り込む",Width=720,Height=680,WindowStartupLocation=WindowStartupLocation.CenterOwner,Content=panel,Icon=Icon,ShowInTaskbar=false };
        DarkTitleBar.Apply(dialog);
        all.Click+=(s,args)=>{ foreach(var r in roots) r.Set(true,true,false); };
        none.Click+=(s,args)=>{ foreach(var r in roots) r.Set(false,true,false); };
        LocationFilter? chosen=null;
        apply.Click+=(s,args)=>
        {
            var folders=new List<string>();
            void Collect(LocationNode node) { if(node.IsChecked==true) folders.Add(node.Path); else if(node.IsChecked is null) foreach(var c in node.Children) Collect(c); }
            foreach(var r in roots) Collect(r);
            if(folders.Count==0) { MessageBox.Show(dialog,"場所を1つ以上選んでください。","IO Recorder",MessageBoxButton.OK,MessageBoxImage.Information); return; }
            // Every location chosen is the whole record (no narrowing).
            chosen=roots.All(r=>r.IsChecked==true)?new():LocationFilter.Of(folders);
            dialog.Close();
        };
        return (dialog,()=>chosen);
    }

    // The folders of the files a record touched, with the amounts below each: drives and shares at the top, the busiest first.
    private static List<LocationNode> LocationTree(IEnumerable<FileMetrics> files)
    {
        var nodes=new Dictionary<string,LocationNode>(StringComparer.OrdinalIgnoreCase); var roots=new List<LocationNode>();
        foreach(var f in files)
        {
            if(string.IsNullOrEmpty(f.Path)) continue;
            string path=f.Path.Replace('/','\\');
            if(path.StartsWith(@"\\?\UNC\",StringComparison.OrdinalIgnoreCase)) path=@"\\"+path[8..]; else if(path.StartsWith(@"\\?\",StringComparison.Ordinal)) path=path[4..];
            string root=PathPolicy.VolumeOf(path); if(root.Length==0) continue;
            var parts=path[Math.Min(path.Length,root.Length)..].Split('\\',StringSplitOptions.RemoveEmptyEntries);
            long bytes=f.ReadBytes+f.WriteBytes,operations=f.Reads+f.Writes+f.Metadata;
            if(!nodes.TryGetValue(root,out var node)) { nodes[root]=node=new(root,root.EndsWith(':')?root+"\\":root,null); roots.Add(node); }
            node.Add(bytes,operations);
            // The folders above the file (its own name is not a node).
            string key=root;
            for(int i=0;i<parts.Length-1;i++)
            {
                key+="\\"+parts[i];
                if(!nodes.TryGetValue(key,out var child)) { nodes[key]=child=new(key,parts[i],node); node.Children.Add(child); }
                child.Add(bytes,operations); node=child;
            }
            node.AddOwn(bytes,operations);
        }
        // A folder with files of its own and folders below gets an entry for its own files, so that choosing some of its folders
        // does not take its files too (LocationFilter "<folder>\*").
        foreach(var node in nodes.Values.Where(n=>n.Children.Count>0 && n.OwnOperations>0).ToList())
        {
            var own=new LocationNode(node.Path+LocationFilter.DirectSuffix,"（このフォルダの直下のファイル）",node);
            own.Add(node.OwnBytes,node.OwnOperations); node.Children.Add(own);
        }
        void Sort(List<LocationNode> list) { list.Sort((a,b)=>b.ByteCount!=a.ByteCount?b.ByteCount.CompareTo(a.ByteCount):b.Operations.CompareTo(a.Operations)); foreach(var n in list) Sort(n.Children); }
        Sort(roots);
        return roots;
    }
    // Shown on 02 and 03.
    private void ShowLocations()
    {
        string text=_locations.IsEmpty?"場所: すべての場所（「場所で絞り込む…」で、素材のフォルダなどに絞れます）":"場所を絞り込んでいます（02の集計・03のダミーと再生・05の比較）: "+_locations.Text;
        LocationText.Text=text; ReplayLocationText.Text=_locations.IsEmpty?"すべての場所（02の「場所で絞り込む…」で変えられます）":_locations.Text;
    }
}
