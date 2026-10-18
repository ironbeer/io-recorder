using System.IO;
using System.Windows;
using System.Windows.Controls;
using IoRecorder.Core;

namespace IoRecorder.App;

// 05: the open record next to the recording of one of its replays (ReplayCapture, ReplayComparison).
public partial class MainWindow
{
    private sealed record CompareRun(string Folder,string Title);
    private sealed record ValueRow(string Item,string Original,string Replay,string Difference);
    private sealed record FileRow(string Presence,string Path,string Read,string Write,string Latency,string LatencyChange,long ReadBytes,long WriteBytes,double ReplayP95,double LatencyDelta);
    private sealed record OperationRow(string Operation,string Original,string Replay,string Difference);
    private sealed record ProcessRow(string Process,string OriginalOperations,string ReplayOperations,string Read,string Write);
    private string? _compareOriginal;
    private bool _listingRuns;

    private async Task LoadCompareRunsAsync()
    {
        string? preferred=_compareTrace;
        _listingRuns=true;
        try
        {
            CompareRunBox.ItemsSource=null; ClearComparison();
            if(_session is null) { CompareSummary.Text="「記録を開く」で記録を開いてから、このページを開いてください。"; return; }
            // A recording of a replay that is open in 02 compares with its own original record.
            string original=_session,link=Path.Combine(_session,ReplayCapture.LinkFile);
            if(File.Exists(link)) { original=JsonStore.Read<ReplayLink>(link).OriginalSession; preferred??=_session; }
            var runs=await Task.Run(()=>ReplayCapture.List(original));
            _compareOriginal=original;
            if(runs.Count==0)
            {
                CompareSummary.Text="この記録には、記録付きの再生がまだありません。";
                CompareNote.Text="03で「再生中のI/Oも記録する」をオンにして再生すると、ここで元の記録と比べられます（管理者権限が必要です）。";
                return;
            }
            var items=runs.Select(r=>new CompareRun(r.Folder,$"{Path.GetFileName(r.Folder)}  ·  {r.Link.Options.Mode}  ·  再生 {r.Link.ElapsedSeconds:N1} 秒  ·  {r.Link.Dataset}")).ToList();
            CompareRunBox.ItemsSource=items;
            CompareRunBox.SelectedItem=items.FirstOrDefault(i=>preferred is not null && i.Folder.Equals(Path.GetFullPath(preferred),StringComparison.OrdinalIgnoreCase))??items[0];
        }
        finally { _listingRuns=false; _compareTrace=null; }
        if(CompareRunBox.SelectedItem is CompareRun run) await ShowComparisonAsync(run.Folder);
    }

    private void CompareRunChanged(object sender,SelectionChangedEventArgs e)
    { if(!_listingRuns && CompareRunBox.SelectedItem is CompareRun run) _compareLoad=ShowComparisonAsync(run.Folder); }
    private void CompareScopeChanged(object sender,SelectionChangedEventArgs e)
    { if(IsLoaded && !_listingRuns && CompareRunBox?.SelectedItem is CompareRun run) _compareLoad=ShowComparisonAsync(run.Folder); }
    private ComparisonScope CompareScope=>(CompareScopeBox.SelectedItem as ComboBoxItem)?.Tag as string=="Replayed"?ComparisonScope.Replayed:ComparisonScope.All;

    private async Task ShowComparisonAsync(string trace)
    {
        if(_compareOriginal is not { } original) return;
        try
        {
            var scope=CompareScope;
            // The original record's narrowing (02 「場所で絞り込む」) applies to both sides.
            var locations=LocationFilter.Load(original);
            CompareSummary.Text="比較しています…（古い形式の記録は、集計し直すため時間がかかります）";
            var c=await Task.Run(()=>ReplayComparison.Build(original,trace,scope,locations));
            var manifest=JsonStore.Read<SessionManifest>(Path.Combine(original,"session.json"));
            CompareSummary.Text=$"元: {manifest.Label}  ／  再生: {c.Link.Options.Mode}・速度 {c.Link.Options.Speed:0.###}倍・最大並列 {c.Link.Options.Workers}・再生 {c.Link.ElapsedSeconds:N1} 秒";
            CompareNote.Text=(c.Scope==ComparisonScope.Replayed
                ?$"比べる範囲: 再生した要求だけ（再生したプロセス PID {c.ReplayProcessId} のI/Oと、元の記録のうち再生の対象にしたI/O）。試験先のOSやほかのプログラムのI/Oは含みません。"
                :"比べる範囲: 全体（両方の記録のすべてのプロセス）。再生側には、再生中に試験先のOSやほかのプログラムが出したI/Oも入ります。")
                +(c.Locations.Count>0?$"\n場所を絞り込んでいます（両方とも、この中のI/Oだけ）: {LocationFilter.TextOf(c.Locations)}":"")
                +(c.Link.Options.Locations.Count>0?$"\nこの再生のダミーは、場所を絞り込んで作ったものです: {LocationFilter.TextOf(c.Link.Options.Locations)}":"")
                +$"\n試験先のダミー: {c.Link.Dataset}　·　再生の記録: {trace}"
                +(c.IndexerNote is { } indexer?"\n注意: "+indexer:"");
            CompareValuesGrid.ItemsSource=c.Values.Select(v=>new ValueRow(v.Item,Value(v.Unit,v.Original),Value(v.Unit,v.Replay),Difference(v.Unit,v.Original,v.Replay))).ToArray();
            CompareGraph.SetComparison(c.OriginalTimeline,c.ReplayTimeline);
            CompareFilesGrid.ItemsSource=c.Files.Take(2000).Select(f=>new FileRow(f.Presence,f.Path,$"{Amount(f.OriginalReadBytes)} / {Amount(f.ReplayReadBytes)}",$"{Amount(f.OriginalWriteBytes)} / {Amount(f.ReplayWriteBytes)}",
                $"{Ms(f.OriginalP95Ms)} / {Ms(f.ReplayP95Ms)}",Difference(ComparisonUnit.Ms,f.OriginalP95Ms,f.ReplayP95Ms),
                Math.Max(f.OriginalReadBytes,f.ReplayReadBytes),Math.Max(f.OriginalWriteBytes,f.ReplayWriteBytes),f.ReplayP95Ms??0,(f.ReplayP95Ms??0)-(f.OriginalP95Ms??0))).ToArray();
            CompareFilesSummary.Text=string.Join("  ·  ",c.Files.GroupBy(f=>f.Presence).OrderBy(g=>g.Key=="両方"?0:g.Key=="元のみ"?1:2).Select(g=>$"{g.Key} {g.Count():N0}件"));
            CompareOpsGrid.ItemsSource=c.Operations.Select(o=>new OperationRow(o.Operation,o.Original.ToString("N0"),o.Replay.ToString("N0"),Difference(ComparisonUnit.Count,o.Original,o.Replay))).ToArray();
            CompareProcessGrid.ItemsSource=c.Processes.Select(p=>new ProcessRow(p.Process,p.OriginalOperations.ToString("N0"),p.ReplayOperations.ToString("N0"),
                $"{Amount(p.OriginalReadBytes)} / {Amount(p.ReplayReadBytes)}",$"{Amount(p.OriginalWriteBytes)} / {Amount(p.ReplayWriteBytes)}")).ToArray();
            CompareScroll.ScrollToTop();
        }
        catch(Exception ex) when(ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        { ClearComparison(); CompareSummary.Text="比較できませんでした: "+ex.Message; }
    }

    private void ClearComparison()
    {
        CompareSummary.Text=""; CompareNote.Text=""; CompareFilesSummary.Text="";
        CompareValuesGrid.ItemsSource=null; CompareFilesGrid.ItemsSource=null; CompareOpsGrid.ItemsSource=null; CompareProcessGrid.ItemsSource=null; CompareGraph.SetTimeline([]);
    }

    private void CompareLastReplay(object sender,RoutedEventArgs e) { _compareTrace=_lastTrace; ShowPage(4); }
    private async void OpenCompareTrace(object sender,RoutedEventArgs e)
    {
        if(CompareRunBox.SelectedItem is not CompareRun run) return;
        try { await LoadSessionAsync(run.Folder); ShowPage(1); } catch(Exception ex) { ReportError(ex); }
    }

    private static string Amount(long bytes)=>bytes==0?"0":Bytes(bytes);
    private static string Ms(double? value)=>value is { } v?$"{v:N2} ms":"—";
    private static string Value(ComparisonUnit unit,double? value)=>value is not { } v?"—":unit switch
    {
        ComparisonUnit.Seconds=>$"{v:N2} 秒",ComparisonUnit.Bytes=>Amount((long)v),ComparisonUnit.MiBps=>v<1?$"{v:N2} MiB/s":$"{v:N1} MiB/s",
        ComparisonUnit.Iops=>$"{v:N0} IOPS",ComparisonUnit.Ms=>$"{v:N2} ms",_=>$"{v:N0}"
    };
    // "再生 − 元" with its sign, and the change against the original in brackets.
    private static string Difference(ComparisonUnit unit,double? original,double? replay)
    {
        if(original is not { } a || replay is not { } b) return "—";
        double d=b-a;
        string amount=d==0?"0":(d>0?"+":"−")+Value(unit,Math.Abs(d));
        return a>0?$"{amount}（{(b/a-1)*100:+0;-0;0}%）":amount;
    }
}
