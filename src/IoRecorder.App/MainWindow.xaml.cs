using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IoRecorder.Core;
using Microsoft.Win32;

namespace IoRecorder.App;

public partial class MainWindow : Window
{
    private CancellationTokenSource? _stop;
    private EtwRecorder? _recorder;
    private bool _recording,_closeAfterWork;
    private readonly Stopwatch _elapsed=new();
    private readonly DispatcherTimer _timer=new() { Interval=TimeSpan.FromSeconds(1) };
    private string? _session;
    private AnalysisResult? _analysis;
    private readonly string[] _args;
    private string? _replayDataset,_replayConsumed;
    // 05: the recording of the last replay, the one to show next, and the loading in progress (awaited by the smoke test).
    private string? _lastTrace,_compareTrace;
    private Task _compareLoad=Task.CompletedTask;
    private int _replayScope;
    private readonly IProgress<string> _progress;

    public MainWindow(string[] args)
    {
        _args=args; InitializeComponent(); DarkTitleBar.Apply(this); _progress=new Progress<string>(s=>LogText.Text=s);
        StorageText.Text=AppPaths.Root; RecordDestination.Text=AppPaths.Recordings+@"\日時_内容"; BenchDestination.Text=AppPaths.Benchmarks+@"\日時_構成名_seed";
        LoadSettings(); InitBench(); UpdateLaunchUi();
        AdminStatus.Text=EtwRecorder.IsAdministrator?"管理者で実行中":"通常権限（計測はできません）";
        VersionText.Text=$"v{ToolVersion.Text} · Windows x64";
        RootsBox.TextChanged+=(s,e)=>UpdateLabelHint(); UpdateLabelHint();
        ChainWheel(LiveFiles); ChainWheel(ResultFiles); ChainWheel(EventGrid); ChainWheel(PatternGrid); ChainWheel(WriteGrid); ChainWheel(ChunkGrid); ChainWheel(CompareFilesGrid);
        // Recording the replay needs ETW, so administrator rights.
        if(!EtwRecorder.IsAdministrator) { RecordReplayBox.IsChecked=false; RecordReplayBox.IsEnabled=false; RecordReplayBox.ToolTip="管理者権限で起動したときだけ使えます（ETWで記録するため）。"; }
        _timer.Tick+=Tick; _timer.Start();
        Closing+=OnClosing;
        Loaded+=async(s,e)=>
        {
            ShowPage(0);
            int smoke=Array.IndexOf(_args,"--smoke-test");
            if(smoke>=0 && smoke+1<_args.Length)
            {
                try { await SmokeTest(_args[smoke+1]); Application.Current.Shutdown(0); }
                catch(Exception ex) { Directory.CreateDirectory(_args[smoke+1]); File.WriteAllText(Path.Combine(_args[smoke+1],"ui-failure.txt"),ex.ToString()); Application.Current.Shutdown(1); }
            }
            else
            {
                // After a crash: a measurement session left running is stopped, and records that were cut off are finished.
                _ = Task.Run(()=>
                {
                    try
                    {
                        int sessions=EtwRecorder.StopOrphanSessions(), records=RecordRecovery.PrepareAll(AppPaths.Recordings);
                        if(sessions+records>0) Dispatcher.Invoke(()=>LogText.Text=$"前回途中で終わっていたもの: 計測セッション {sessions} 件を止め、記録 {records} 件を書き出されていた所までで確定しました。");
                    }
                    catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
                });
                if(_args.Contains("--bench")) ShowPage(3);
                int sessionArg=Array.IndexOf(_args,"--session");
                if(sessionArg>=0 && sessionArg+1<_args.Length) { await LoadSessionAsync(_args[sessionArg+1]); ShowPage(1); }
            }
        };
    }
    private void ShowPage(int page)
    {
        var panels=new FrameworkElement[] { RecordPanel,ResultsPanel,ReplayPanel,BenchPanel,ComparePanel };
        var nav=new System.Windows.Controls.Button[] { NavRecord,NavResults,NavReplay,NavBench,NavCompare };
        for(int i=0;i<panels.Length;i++)
        { panels[i].Visibility=page==i?Visibility.Visible:Visibility.Collapsed; nav[i].Background=page==i?new SolidColorBrush(Color.FromRgb(33,50,71)):Brushes.Transparent; }
        PageTitle.Text=page switch { 0=>"ライブ計測",1=>"記録を開く",2=>"記録の再生",3=>"ベンチマーク",_=>"比較" };
        PageSubtitle.Text=page switch { 0=>"指定したプロセス・フォルダのファイルI/Oを記録します。表示は1〜数秒遅れます。",1=>"保存済みの記録を開き、ファイル別・時系列・イベントを確認します。",2=>"記録したI/Oを、乱数のダミーファイルに対して再生します。",3=>"構成ファイルとseedから決まったI/O負荷を作り、試験先で実行します。",_=>"開いている記録と、その再生の記録を並べて比べます。" };
        ReplaySessionText.Text=_session??"先に「記録を開く」でセッションを選択してください。";
        if(page==4) _compareLoad=LoadCompareRunsAsync();
    }
    private void ShowRecord(object sender,RoutedEventArgs e)=>ShowPage(0);
    private void ShowResults(object sender,RoutedEventArgs e)=>ShowPage(1);
    private void ShowReplay(object sender,RoutedEventArgs e)=>ShowPage(2);
    private void ShowCompare(object sender,RoutedEventArgs e)=>ShowPage(4);
    // Numbers typed into the form, with a message that names the field and shows what was typed.
    private static double Number(string text,string name,double min,double max,bool whole=false)
    {
        if(!double.TryParse(text.Trim(),NumberStyles.Float,CultureInfo.InvariantCulture,out var value) || value<min || value>max || whole && value!=Math.Floor(value))
            throw new FormatException($"{name}は{min.ToString(CultureInfo.InvariantCulture)}〜{max.ToString(CultureInfo.InvariantCulture)}の{(whole?"整数":"数値")}で入力してください（入力: \"{text}\"）。");
        return value;
    }
    private static string[] Split(string text)=>text.Split([';','\r','\n'],StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
    private RecordingOptions ReadOptions()
    {
        var roots=Split(RootsBox.Text); var app=_launchApp;
        if(app is null && roots.Length==0) throw new ArgumentException("計測対象のアプリケーションか、対象フォルダを指定してください。");
        return new()
        {
            // What is recorded follows from what is given: the application, the folders, or both (either one is enough).
            Mode=app is null?CaptureMode.Folder:roots.Length==0?CaptureMode.Process:CaptureMode.Hybrid,
            // The application's program, the processes it starts and the other programs installed with it.
            ProcessNames=app is null?Array.Empty<string>():new[] { app.ProcessName },IncludeChildren=true,
            ProgramFolders=app is not null && AppLauncher.InstallFolder(app.Path) is { } folder?new[] { folder }:Array.Empty<string>(),
            // Size probing stays on in the GUI; the CLI keeps --no-size-probe for special cases.
            Roots=roots,ProbeSizes=true,
            LucidExecutable=string.IsNullOrWhiteSpace(LucidBox.Text)?null:LucidBox.Text.Trim(),
            LucidInstance=string.IsNullOrWhiteSpace(LucidIdBox.Text)?null:LucidIdBox.Text.Trim()
        };
    }
    private async void StartRecording(object sender,RoutedEventArgs e)
    {
        if(_stop is not null) return;
        try
        {
            var options=ReadOptions();
            if(_launchApp is { } app && LaunchBox.IsChecked==true)
            {
                // A second start of most editing applications only hands over to the running one, whose start-up is past.
                if(AppLauncher.IsRunning(app))
                {
                    var answer=MessageBox.Show(this,$"{app.Name} はもう起動しています。このまま記録すると、起動したときの読み込み（前回のプロジェクトや素材の一覧など）は記録に入りません。\n\nこのまま記録を始めますか？\n\n「いいえ」で中止します。{app.Name} を終了してから、もう一度「計測を開始」を押してください。",
                        "IO Recorder",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No);
                    if(answer!=MessageBoxResult.Yes) return;
                }
                else options.Launch=app;
            }
            string subject=Subject(options.ProcessNames,options.ProcessIds,options.Roots);
            string typed=LabelBox.Text.Trim(),label=typed.Length>0?typed:AutoLabel(subject);
            // Folder: Recordings\yyyyMMdd-HHmmss_<label or what is measured>.
            string output=AppPaths.NewFolder(AppPaths.Recordings,typed.Length>0?typed:subject);
            options.Validate(output); SaveSettings();
            if(!EtwRecorder.IsAdministrator) throw new UnauthorizedAccessException("ETW計測には管理者権限が必要です。IO Recorderを管理者として起動してください。");
            _session=output; _recorder=new(); _stop=new(); _recording=true; _elapsed.Restart(); SetBusy(true);
            StatusBadge.Text="●  RECORDING"; QualityText.Text="ETWセッションを準備中…";
            var result=await _recorder.RecordAsync(output,options,label,_stop.Token,_progress);
            _analysis=result; _lastRecording=output; ApplyMetrics(result,false); await LoadSessionAsync(output);
            StatusBadge.Text="●  SAVED";
            LogText.Text="保存完了: "+output+"（送るときは「記録をzipで書き出す…」）";
        }
        catch(Exception ex) { ReportError(ex); StatusBadge.Text="●  ERROR"; }
        finally { FinishWork(); }
    }
    // What is measured: process names (or PIDs), else the folder names. Used for the automatic label and the folder name.
    private static string Subject(IEnumerable<string> processes,IEnumerable<int> pids,IEnumerable<string> roots)
    {
        var names=processes.Select(p=>Path.GetFileNameWithoutExtension(p.Trim())).Where(p=>p.Length>0).Concat(pids.Select(p=>"PID"+p)).ToList();
        if(names.Count==0) names=roots.Select(r=>Path.GetFileName(Path.TrimEndingDirectorySeparator(r.Trim())) is { Length:>0 } n?n:r.Trim().TrimEnd('\\',':')).Where(n=>n.Length>0).ToList();
        return string.Join("・",names);
    }
    // An empty label becomes "yyyy/MM/dd <subject>試験" when recording starts; the box shows what it will be.
    private static string AutoLabel(string subject)=>$"{DateTime.Now.ToString("yyyy/MM/dd",CultureInfo.InvariantCulture)} {(subject.Length>0?subject+"試験":"計測試験")}";
    private void UpdateLabelHint()=>LabelBox.Tag="空欄なら「"+AutoLabel(Subject(_launchApp is { } app?new[] { app.ProcessName }:Array.Empty<string>(),Array.Empty<int>(),Split(RootsBox.Text)))+"」";
    private void OpenStorage(object sender,RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(AppPaths.Root); Process.Start(new ProcessStartInfo(AppPaths.Root) { UseShellExecute=true }); }
        catch(Exception ex) { ReportError(ex); }
    }
    private void Tick(object? sender,EventArgs e)
    {
        if(_recording && _recorder is not null)
        {
            ElapsedText.Text=_elapsed.Elapsed.ToString(@"hh\:mm\:ss");
            ApplyMetrics(_recorder.Metrics.Snapshot(),true);
            if(_recorder.LatestMemory is { } m) MemoryText.Text=$"{m.AvailableBytes/1073741824.0:N1} / {m.TotalBytes/1073741824.0:N1} GiB";
            QualityText.Text=$"保存済み {_recorder.EventsWritten:N0} · 保存欠落 {_recorder.QueueEventsDropped:N0}";
        }
        else
        {
            // Replay, dummy generation, reset, benchmark, CSV export: the time since the work started (kept when it ends).
            if(_elapsed.IsRunning) ElapsedText.Text=_elapsed.Elapsed.ToString(@"hh\:mm\:ss");
            try { var m=MemorySampler.Sample(0); MemoryText.Text=$"{m.AvailableBytes/1073741824.0:N1} / {m.TotalBytes/1073741824.0:N1} GiB"; } catch { }
        }
    }
    // 01 while recording (cards show the last second) and once the record is saved (cards show the whole record).
    private void ApplyMetrics(AnalysisResult a,bool live)
    {
        int second=(int)_elapsed.Elapsed.TotalSeconds-2;
        if(live) LiveCards.ShowLive(a,a.Timeline.LastOrDefault(p=>p.Second==second)); else LiveCards.ShowRecord(a);
        FileCountText.Text=$"{a.FileCount:N0} files";LiveFiles.ItemsSource=a.Files.Take(100).ToArray();LiveGraph.SetTimeline(a.Timeline,live,live?(int)_elapsed.Elapsed.TotalSeconds-1:null);
    }
    // A list inside a scrolling page hands the wheel to the page once it reaches its own top or bottom.
    private static void ChainWheel(DataGrid grid)
    {
        grid.PreviewMouseWheel+=(sender,e)=>
        {
            if(Descendant<ScrollViewer>(grid) is not { } inner) return;
            bool atEdge=e.Delta>0?inner.VerticalOffset<=0:inner.VerticalOffset>=inner.ScrollableHeight;
            if(!atEdge) return;
            DependencyObject? parent=VisualTreeHelper.GetParent(grid);
            while(parent is not null && parent is not ScrollViewer) parent=VisualTreeHelper.GetParent(parent);
            if(parent is not ScrollViewer page) return;
            e.Handled=true;
            page.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice,e.Timestamp,e.Delta) { RoutedEvent=MouseWheelEvent,Source=grid });
        };
    }
    private static T? Descendant<T>(DependencyObject root) where T:DependencyObject
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
        {
            var child=VisualTreeHelper.GetChild(root,i);
            if(child is T match) return match;
            if(Descendant<T>(child) is { } found) return found;
        }
        return null;
    }
    private void SetBusy(bool busy)
    {
        // Every long piece of work starts here, so the clock at the top right counts it (FinishWork stops it).
        if(busy) { _elapsed.Restart(); ElapsedText.Text="00:00:00"; }
        StartButton.IsEnabled=!busy;StopButton.IsEnabled=busy&&_recording;ExportRecordButton.IsEnabled=!busy&&_lastRecording is not null;
        GenerateButton.IsEnabled=!busy;ReplayButton.IsEnabled=!busy;CancelReplayButton.IsEnabled=busy&&!_recording;
        BenchGenerateButton.IsEnabled=BenchRunButton.IsEnabled=!busy;BenchCancelButton.IsEnabled=busy&&_benchRunning;
        BenchResetButton.IsEnabled=!busy&&_benchConsumed is not null;ReplayResetButton.IsEnabled=!busy&&_replayConsumed is not null;
    }
    private void FinishWork()
    {
        _recording=false;_elapsed.Stop();_stop?.Dispose();_stop=null;SetBusy(false);
        if(_closeAfterWork) Close();
    }
    private void StopWork(object sender,RoutedEventArgs e) { _stop?.Cancel();LogText.Text="停止して記録を確定しています…"; }
    private void ReportError(Exception ex) { LogText.Text=ex.Message; MessageBox.Show(this,ex.Message,"IO Recorder",MessageBoxButton.OK,MessageBoxImage.Error); }

    private string? PickFolder(string title)
    { var d=new OpenFolderDialog { Title=title,Multiselect=false };return d.ShowDialog(this)==true?d.FolderName:null; }
    private void BrowseRoot(object sender,RoutedEventArgs e) { if(PickFolder("計測対象フォルダ") is { } p) RootsBox.Text=string.IsNullOrWhiteSpace(RootsBox.Text)?p:RootsBox.Text+Environment.NewLine+p; }
    private async void ReplayReset(object sender,RoutedEventArgs e)
    {
        if(_stop is not null || _replayConsumed is null) return;
        try
        {
            string dataset=_replayConsumed; _stop=new(); SetBusy(true); StatusBadge.Text="●  RESETTING";
            var token=_stop.Token;
            var summary=await Task.Run(()=>DatasetBuilder.ResetAsync(dataset,_progress,token));
            _replayDataset=dataset; _replayConsumed=null; StatusBadge.Text="●  READY";
            ReplayResultText.Text=$"初期状態に戻しました\n削除 {summary.DeletedFiles:N0} ファイル / 作り直し {summary.RecreatedFiles:N0} / 書き戻し {summary.RewrittenBytes/1048576.0:N1} MiB\n{dataset}\n\nアップロード・キャッシュ条件を整えたら、もう一度リプレイできます。";
        }
        catch(OperationCanceledException) { LogText.Text="中止しました。もう一度「ダミーを初期状態に戻す」を押してください。";StatusBadge.Text="●  CANCELLED"; }
        catch(Exception ex) { ReportError(ex);StatusBadge.Text="●  ERROR"; }
        finally { FinishWork(); }
    }
    private void BrowseTarget(object sender,RoutedEventArgs e) { if(PickFolder("試験先フォルダ（NAS・LucidLinkなど）") is { } p) TargetBox.Text=p; }
    private async void OpenSession(object sender,RoutedEventArgs e)
    { if(PickFolder("session.jsonを含む記録フォルダ") is { } p) { try { await LoadSessionAsync(p); } catch(Exception ex) { ReportError(ex); } } }
    private async Task LoadSessionAsync(string path)
    {
        var manifest=JsonStore.Read<SessionManifest>(Path.Combine(path,"session.json"));
        // The summary saved when the record was finished is reused; only an old or missing one is recomputed. A record narrowed
        // to some folders (02 「場所で絞り込む」) is summarized for them, apart from the whole record's summary.
        var locations=LocationFilter.Load(path);
        var a=await Task.Run(()=>SessionAnalyzer.Load(path,_progress,locations));
        _session=Path.GetFullPath(path);_analysis=a;_locations=locations;_replayDataset=_replayConsumed=null;SessionBox.Text=_session;ReplaySessionText.Text=_session;
        ShowLocations();
        // 02 has its own summary; 01 stays the page for recording and is not changed by opening a record.
        SummaryText.Text=$"{manifest.Label}  ／  {manifest.StartedUtc.ToLocalTime():yyyy/MM/dd HH:mm:ss}  ／  {a.DurationSeconds:N1} 秒";
        SummaryQuality.Text=$"{manifest.State} · ETW欠落 {(manifest.EtwLossCountAvailable?manifest.EtwEventsLost.ToString("N0"):"未取得")} · 保存欠落 {manifest.QueueEventsDropped:N0} · 完了未取得 {a.MissingCompletions:N0} · 失敗した操作 {a.FailedOperations:N0}{(a.FastIoRefusals>0?$"（高速I/Oの拒否 {a.FastIoRefusals:N0} 件は、すぐ出し直されたので数えていません）":"")}{(a.OtherProcessEvents>0?$" · 対象のファイルへの他のプロセスのI/O {a.OtherProcessEvents:N0} 件（ページングI/O以外は集計に含めない）":"")} · サイズは非同期観測値";
        if(manifest.State==RecordRecovery.Interrupted) SummaryQuality.Text="途中で終わった記録（ツールかPCが止まった）です。書き出されていた所までを表示しています。  ·  "+SummaryQuality.Text;
        if(manifest.State=="SyntheticDemo") SummaryQuality.Text="DEMO / 合成データです。実測値ではありません。  ·  "+SummaryQuality.Text;
        if(OtherProcessIo.Summary(manifest,a) is { } others) SummaryQuality.Text+="\n記録しなかったプロセスで、同じドライブへのI/Oが多かったもの（次の記録で対象に足せます）: "+others;
        ResultCards.ShowRecord(a);ResultGraph.SetTimeline(a.Timeline);ResultFiles.ItemsSource=a.Files;ShowPatterns(a);ResultsScroll.ScrollToTop();
        await RefreshEvents();LogText.Text="記録を開きました: "+path;
    }
    // 02: how each file was read, and the chunk-size estimate (see AccessPatternBuilder).
    private void ShowPatterns(AnalysisResult a)
    {
        static string Size(long bytes)=>bytes>=1L<<30?$"{bytes/1073741824.0:N2} GiB":bytes>=1L<<20?$"{bytes/1048576.0:N1} MiB":$"{bytes/1024.0:N0} KiB";
        static string Pct(double? value)=>value.HasValue?$"{value.Value*100:N0}%":"—";
        // Efficiency is the inverse of the amplification: what was really used of what was fetched or uploaded.
        static string Efficiency(double? amplification)=>amplification is > 0?$"{100/amplification.Value:N1}%":"—";
        long total=Math.Max(1,a.AccessPatterns.Sum(p=>p.ReadBytes));
        ShapeSummary.Text=string.Join("  ·  ",a.AccessPatterns.Where(p=>p.Reads>0).GroupBy(p=>p.Shape).OrderByDescending(g=>g.Sum(p=>p.ReadBytes))
            .Select(g=>$"{g.Key} {g.Count():N0}件（読み込みの{g.Sum(p=>p.ReadBytes)*100.0/total:N0}%）"));
        // Amount and its share in brackets, as in "3.5 MiB（78%）".
        static string Share(long part,long whole)=>part==0?"0":$"{Size(part)}（{part*100.0/Math.Max(1,whole):N0}%）";
        PatternGrid.ItemsSource=a.AccessPatterns.Where(p=>p.Reads>0).Take(1000).Select(p=>
        {
            // 読み取り量 = 再読み取り量 + 読み取り範囲; the range is shown against the file size (the coverage).
            long repeat=Math.Max(0,p.ReadBytes-p.UniqueReadBytes);
            string range=p.SizeBytes>0?$"{Size(p.UniqueReadBytes)} / {Size(p.SizeBytes)}（{Pct(p.ReadCoverage)}）":Size(p.UniqueReadBytes);
            return new PatternRow(p.Shape,p.Path,Size(p.ReadBytes),Share(repeat,p.ReadBytes),range,$"≤{Size(p.ReadSizeP50Bytes)}",Pct(p.SequentialRatio+p.NearRatio),
                p.ReadBytes,repeat,p.ReadCoverage??0,p.ReadSizeP50Bytes,p.SequentialRatio+p.NearRatio);
        }).ToArray();
        long written=Math.Max(1,a.AccessPatterns.Sum(p=>p.WriteBytes));
        WriteShapeSummary.Text=string.Join("  ·  ",a.AccessPatterns.Where(p=>p.Writes>0).GroupBy(p=>p.WriteShape).OrderByDescending(g=>g.Sum(p=>p.WriteBytes))
            .Select(g=>$"{g.Key} {g.Count():N0}件（書き込みの{g.Sum(p=>p.WriteBytes)*100.0/written:N0}%）"));
        WriteGrid.ItemsSource=a.AccessPatterns.Where(p=>p.Writes>0).OrderByDescending(p=>p.WriteBytes).Take(1000).Select(p=>
        {
            // 書き込み量 = 再書き込み量 + 既存の上書き + 新しい部分 (every written byte is in exactly one of them).
            long rewrite=Math.Max(0,p.WriteBytes-p.UniqueWriteBytes),added=Math.Max(0,p.UniqueWriteBytes-p.ExistingOverwriteBytes);
            string overwrite=!p.ExistedAtStart?"新規":p.InitialSizeBytes==0?"0 / 0（空のファイル）":$"{(p.ExistingOverwriteBytes==0?"0":Size(p.ExistingOverwriteBytes))} / {Size(p.InitialSizeBytes)}（{p.ExistingOverwriteBytes*100.0/p.InitialSizeBytes:N0}%）";
            return new WriteRow(p.WriteShape,p.Path,Size(p.WriteBytes),Share(rewrite,p.WriteBytes),overwrite,added==0?"0":Size(added),$"≤{Size(p.WriteSizeP50Bytes)}",Pct(p.WriteContinuousRatio),
                p.WriteBytes,rewrite,p.ExistingOverwriteBytes,added,p.WriteSizeP50Bytes,p.WriteContinuousRatio);
        }).ToArray();
        ChunkGrid.ItemsSource=a.ChunkEstimates.Select(c=>new ChunkRow(Size(c.ChunkBytes),c.ReadChunks.ToString("N0"),Size(c.ReadFetchBytes),Efficiency(c.ReadAmplification),
            c.WriteChunks.ToString("N0"),Size(c.WriteUploadBytes),Size(c.WriteEachBytes),Efficiency(c.WriteAmplification),c.WritePartialChunks.ToString("N0"))).ToArray();
        PatternNote.Text=a.AccessRangesCapped?"範囲の集計が上限に達したため、読み取り範囲・書き込み範囲は下限値で、カバー率・再読み取り量・再書き込み量・効率は目安です。":"";
        PatternNote.Visibility=PatternNote.Text.Length>0?Visibility.Visible:Visibility.Collapsed;
    }
    // Display text plus the numbers the columns sort by.
    private sealed record PatternRow(string Shape,string Path,string Read,string Repeat,string RangeOfSize,string RequestSize,string Continuous,
        long ReadBytes,long RepeatBytes,double CoverageValue,long RequestBytes,double ContinuousValue);
    private sealed record WriteRow(string Shape,string Path,string Write,string Rewrite,string Overwrite,string NewPart,string WriteSize,string Continuous,
        long WriteBytes,long RewriteBytes,long OverwriteBytes,long NewBytes,long WriteSizeBytes,double ContinuousValue);
    private sealed record ChunkRow(string Unit,string ReadChunks,string ReadBytes,string ReadEfficiency,string WriteChunks,string WriteBytes,string WriteEachBytes,string WriteEfficiency,string WritePartial);
    private async void SearchEvents(object sender,RoutedEventArgs e) { try { await RefreshEvents(); } catch(Exception ex) { ReportError(ex); } }
    private async Task RefreshEvents()
    {
        if(_session is null) return;
        string path=_session,query=EventFilter.Text.Trim(); var locations=_locations;
        var rows=await Task.Run(()=>
        {
            var queue=new Queue<EventRow>();
            // Without a search (and without narrowing) only the last events are shown, so only the end of a large record is read.
            var source=query.Length==0 && locations.IsEmpty?RecordLog.Tail<IoEvent>(path,RecordLog.Events,2000):RecordLog.Read<IoEvent>(path,RecordLog.Events);
            foreach(var e in source)
            {
                if(!locations.Includes(e.Path)) continue;
                if(query.Length>0 &&!e.Path.Contains(query,StringComparison.OrdinalIgnoreCase) && !e.ProcessName.Contains(query,StringComparison.OrdinalIgnoreCase) && !e.Operation.ToString().Contains(query,StringComparison.OrdinalIgnoreCase)) continue;
                queue.Enqueue(new(e.StartUs/1_000_000.0,e.Operation.ToString(),e.Path,e.Size,e.ProcessName,e.ProcessId,e.Offset,e.NtStatus));
                if(queue.Count>2000) queue.Dequeue();
            }
            return queue.OrderBy(e=>e.Seconds).ToArray();
        });
        if(_session==path) EventGrid.ItemsSource=rows;
    }
    private async void ExportCsv(object sender,RoutedEventArgs e)
    {
        if(_session is null || _stop is not null) return;
        try
        {
            string session=_session; _stop=new(); SetBusy(true);
            var file=await Task.Run(()=>SessionAnalyzer.ExportCsv(session,_progress));
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList={ "/select,"+file },UseShellExecute=false });
        }
        catch(Exception ex) { ReportError(ex); }
        finally { FinishWork(); }
    }
    private async void OpenReport(object sender,RoutedEventArgs e)
    {
        if(_session is null) return;
        try
        {
            string session=_session; var locations=_locations;
            // A record narrowed to some folders has its own report, written with its summary.
            string report=Path.Combine(session,locations.IsEmpty?"report.html":NarrowedAnalysis.ReportName);
            if(!File.Exists(report))
            {
                if(locations.IsEmpty) await Task.Run(()=>SessionAnalyzer.Export(session,_progress));
                else await Task.Run(()=>{ File.Delete(Path.Combine(session,NarrowedAnalysis.FileName)); SessionAnalyzer.Load(session,_progress,locations); });
            }
            Process.Start(new ProcessStartInfo(report) { UseShellExecute=true });
        }
        catch(Exception ex) { ReportError(ex); }
    }
    private async void GenerateDataset(object sender,RoutedEventArgs e)
    {
        if(_stop is not null) return;
        try
        {
            if(_session is null) throw new InvalidOperationException("先に記録を開いてください。");
            string root=PathPolicy.NormalizeRoot(TargetBox.Text.Trim());
            int seed=Seeds.Value(Seeds.Normalize(SeedBox.Text)??"42");
            // Test target\yyyyMMdd-HHmmss_replay_<recording folder>, created fresh for every generation.
            string target=AppPaths.NewFolder(root,"replay_"+Path.GetFileName(_session)),session=_session;
            // Only the files the replay with this scope touches are generated; the replay must use the same scope.
            int scope=ReplayScope.SelectedIndex; var locations=_locations;
            _replayDataset=null; _stop=new();SetBusy(true);StatusBadge.Text="●  GENERATING";
            var token=_stop.Token;
            var m=await Task.Run(()=>DatasetBuilder.GenerateAsync(session,target,seed,_progress,token,processOnly:scope==1,rootOnly:scope==2,locations:locations));
            _replayDataset=target; _replayScope=scope; _replayLocations=locations.Key; SaveSettings();
            ReplayResultText.Text=$"生成完了\n{m.InitialSizes.Count:N0} ファイル / {m.InitialSizes.Values.Sum()/1048576.0:N1} MiB（{((ComboBoxItem)ReplayScope.SelectedItem).Content}）"
                +(locations.IsEmpty?"":$"\n場所: {locations.Text}")
                +(m.ExcludedPaths>0?$"\n対象外 {m.ExcludedPaths:N0} パス（変換・作成できない名前。再生ではスキップします）":"")
                +$"\n{target}\n\nアップロード・キャッシュ条件を確認したら、リプレイを開始できます。"
                +(TestLocation.IndexWarning(target) is { } indexed?"\n\n注意: "+indexed:"");
            StatusBadge.Text="●  READY";
        }
        catch(OperationCanceledException) { LogText.Text="生成を中止しました。生成途中のフォルダは再生できません。";StatusBadge.Text="●  CANCELLED"; }
        catch(Exception ex) { ReportError(ex);StatusBadge.Text="●  ERROR"; }
        finally { FinishWork(); }
    }
    private async void RunReplay(object sender,RoutedEventArgs e)
    {
        if(_stop is not null) return;
        try
        {
            if(_session is null) throw new InvalidOperationException("先に記録を開いてください。");
            if(_replayDataset is null) throw new InvalidOperationException(_replayConsumed is not null?"このダミーは再生済みです。「ダミーを初期状態に戻す」を押すか、ダミーを生成し直してください。":"先に「1. ダミーファイルを生成」してください。");
            if(ReplayScope.SelectedIndex!=_replayScope) throw new InvalidOperationException("「再生する範囲」をダミー生成のときから変えています。ダミーはその範囲のファイルだけで作ってあるので、この範囲で再生するにはダミーを生成し直してください。");
            if(_replayLocations is not null && _replayLocations!=_locations.Key) throw new InvalidOperationException("場所の絞り込みをダミー生成のときから変えています。ダミーはその場所のファイルだけで作ってあるので、いまの場所で再生するにはダミーを生成し直してください。");
            string target=_replayDataset;
            var options=new ReplayOptions { Mode=Enum.Parse<ReplayMode>((string)((ComboBoxItem)TimingBox.SelectedItem).Tag),Speed=Number(SpeedBox.Text,"速度倍率",0.001,1000),Workers=(int)Number(WorkersBox.Text,"最大並列数",1,256,true),ProcessOnly=ReplayScope.SelectedIndex==1,RootOnly=ReplayScope.SelectedIndex==2 };
            _stop=new();SetBusy(true);StatusBadge.Text="●  REPLAYING";
            string session=_session; var token=_stop.Token; bool record=RecordReplayBox.IsChecked==true;
            // With recording, the test target's I/O is captured during the replay for the comparison on 05.
            var result=record?(await Task.Run(()=>ReplayCapture.RunAsync(session,target,options,_progress,token))).Summary:await Task.Run(()=>new ReplayEngine().RunAsync(session,target,options,_progress,token));
            _replayDataset=null; _replayConsumed=target;
            _lastTrace=result.TraceFolder is { } trace?Path.Combine(session,trace):null;
            CompareReplayButton.Visibility=_lastTrace is null?Visibility.Collapsed:Visibility.Visible;
            ReplayResultText.Text=$"{(result.Cancelled?"中止":"再生完了")}  /  {result.ElapsedSeconds:N1} 秒\n実行 {result.Executed:N0} / 失敗 {result.Failed:N0} / スキップ {result.Skipped:N0}"
                +(result.ReproducedFailures+result.FailuresNotReproduced>0?$"\n記録で失敗した要求の出し直し: 同じく失敗 {result.ReproducedFailures:N0} / 失敗しなかった {result.FailuresNotReproduced:N0}（実行に含む）":"")
                +(result.TemporaryOpens>0?$"\n一時的に開いた回数 {result.TemporaryOpens:N0}（開いた記録がないファイルへの要求のため）":"")
                +$"\nRead {result.ReadBytes/1048576.0:N1} MiB\nWrite {result.WriteBytes/1048576.0:N1} MiB\nP95 {result.P95Ms:N2} ms / P99 {result.P99Ms:N2} ms\n投入の遅れ（{(options.Mode==ReplayMode.ThinkTime?"記録の間隔":"記録の時刻")}との差） 中央値 {result.PacingErrorP50Ms:N3} ms / P95 {result.PacingErrorP95Ms:N3} ms / 最大 {result.MaxSchedulingLagMs:N1} ms\n\n詳細は記録フォルダの replay-*.json / JSONL に保存しました。"+(_lastTrace is null?"":$"\n再生の記録: {result.TraceFolder}")
                +string.Concat(result.Notes.Where(n=>n.StartsWith("再生の前に",StringComparison.Ordinal)||n.StartsWith("試験先がユーザー",StringComparison.Ordinal)).Select(n=>"\n\n注意: "+n));
            StatusBadge.Text=result.Failed>0?"●  CHECK RESULTS":"●  COMPLETE";
        }
        catch(Exception ex) { ReportError(ex);StatusBadge.Text="●  ERROR"; }
        finally { FinishWork(); }
    }
    private void SaveSettings()
    {
        // Smoke tests fill the form with demo values; they must never overwrite the user's settings.
        if(_args.Contains("--smoke-test")) return;
        Directory.CreateDirectory(AppPaths.Root);
        JsonStore.Write(AppPaths.SettingsFile,new Settings { Roots=RootsBox.Text,Label=LabelBox.Text,Lucid=LucidBox.Text,LucidId=LucidIdBox.Text,
            ReplayTarget=TargetBox.Text,BenchConfig=BenchConfigBox.Text,BenchTarget=BenchTargetBox.Text,LaunchApp=_launchApp,Launch=LaunchBox.IsChecked==true });
    }
    private void LoadSettings()
    {
        if(!File.Exists(AppPaths.SettingsFile) || _args.Contains("--smoke-test")) return;
        try
        {
            var s=JsonStore.Read<Settings>(AppPaths.SettingsFile);RootsBox.Text=s.Roots;
            LabelBox.Text=s.Label;LucidBox.Text=s.Lucid;LucidIdBox.Text=s.LucidId;
            TargetBox.Text=s.ReplayTarget;BenchConfigBox.Text=s.BenchConfig;BenchTargetBox.Text=s.BenchTarget;
            // The chosen application is kept while its program is still installed; the process box keeps what was saved.
            LaunchBox.IsChecked=s.Launch; _launchApp=s.LaunchApp is { } app && File.Exists(app.Path)?app:null;
        }
        catch(Exception ex) { LogText.Text="設定ファイルを読み込めませんでした: "+ex.Message; }
    }
    private void OnClosing(object? sender,CancelEventArgs e)
    {
        if(_stop is not null) { e.Cancel=true;_closeAfterWork=true;_stop.Cancel();LogText.Text="処理を停止して保存後に終了します…";return; }
        if(!_args.Contains("--smoke-test")) SaveSettings();
    }
    private async Task SmokeTest(string output)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        var demo=Path.Combine(output,"ui-demo-"+Guid.NewGuid().ToString("N")[..6]);
        await Task.Run(()=>DemoSession.Create(demo));await LoadSessionAsync(demo);
        StatusBadge.Text="●  DEMO / 合成データ";
        LabelBox.Text="DEMO / 画面確認用の合成データ";RootsBox.Text=@"D:\Production";
        bool rasterAvailable=false;
        async Task Capture(string name)
        {
            await Task.Delay(300);UpdateLayout();
            var root=(FrameworkElement)Content;
            var bitmap=new RenderTargetBitmap((int)root.ActualWidth,(int)root.ActualHeight,96,96,PixelFormats.Pbgra32);
            var drawing=new DrawingVisual();
            // The window background is not part of Content; without it, light text on the page itself is lost on a transparent PNG.
            using(var context=drawing.RenderOpen()) { context.DrawRectangle(Background,null,new Rect(0,0,root.ActualWidth,root.ActualHeight)); context.DrawRectangle(new VisualBrush(root),null,new Rect(0,0,root.ActualWidth,root.ActualHeight)); }
            bitmap.Render(drawing);
            var pixels=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(pixels,bitmap.PixelWidth*4,0);
            rasterAvailable|=pixels.Any(p=>p!=0);
            File.AppendAllText(Path.Combine(output,"render-state.txt"),$"{name}: root={root.ActualWidth}x{root.ActualHeight}, visible={root.IsVisible}, children={VisualTreeHelper.GetChildrenCount(root)}, window={IsVisible}\n");
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(output,name+".png"));encoder.Save(file);
        }
        // 01 as it looks right after a recording is saved (opening a record does not change 01).
        if(_analysis is null || _analysis.Timeline.All(s=>s.ReadP95Ms is null)) throw new InvalidDataException("1秒ごとのレイテンシが集計されていません。");
        ApplyMetrics(_analysis,false);
        ShowPage(0);await Capture("live");
        // The text box menu opens in its own popup window, so it is rendered on its own.
        if(LabelBox.ContextMenu is { } menu)
        {
            LabelBox.Focus(); LabelBox.SelectAll(); menu.PlacementTarget=LabelBox; menu.IsOpen=true; await Task.Delay(300); menu.UpdateLayout();
            var image=new RenderTargetBitmap(Math.Max(1,(int)menu.ActualWidth),Math.Max(1,(int)menu.ActualHeight),96,96,PixelFormats.Pbgra32); image.Render(menu);
            var png=new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using(var file=File.Create(Path.Combine(output,"text-menu.png"))) png.Save(file);
            menu.IsOpen=false;
        }
        ShowPage(1);await Capture("results");
        ResultsScroll.ScrollToEnd();await Capture("results-scrolled");
        ResultsScroll.ScrollToVerticalOffset(ResultsScroll.VerticalOffset+PatternGrid.TranslatePoint(new Point(0,0),ResultsScroll).Y-60);await Capture("results-patterns");
        // A "?" explanation opens in its own popup window, so it is rendered on its own.
        var tip=PatternHelp.Open(); await Task.Delay(300); tip.UpdateLayout();
        var help=new RenderTargetBitmap(Math.Max(1,(int)tip.ActualWidth),Math.Max(1,(int)tip.ActualHeight),96,96,PixelFormats.Pbgra32); help.Render(tip);
        var helpPng=new PngBitmapEncoder(); helpPng.Frames.Add(BitmapFrame.Create(help)); using(var file=File.Create(Path.Combine(output,"help-popup.png"))) helpPng.Save(file);
        tip.IsOpen=false; ResultsScroll.ScrollToTop();
        // 05: a synthetic recording of a replay of the demo (a real one needs ETW, so administrator rights).
        await Task.Run(()=>DemoSession.CreateReplayTrace(demo,Path.Combine(output,"ui-compare-dataset")));
        ShowPage(4); await _compareLoad; await Capture("compare");
        CompareScroll.ScrollToVerticalOffset(CompareScroll.VerticalOffset+CompareGraph.TranslatePoint(new Point(0,0),CompareScroll).Y-60); await Capture("compare-graph");
        CompareScroll.ScrollToEnd(); await Capture("compare-scrolled"); CompareScroll.ScrollToTop();
        if(CompareValuesGrid.Items.Count==0 || CompareFilesGrid.Items.Count==0 || CompareProcessGrid.Items.Count==0) throw new InvalidDataException("UI実行時の比較の表示に失敗しました。");
        CompareScopeBox.SelectedIndex=1; await _compareLoad; await Capture("compare-replayed");
        if(CompareValuesGrid.Items.Count==0 || !CompareNote.Text.Contains("再生した要求だけ")) throw new InvalidDataException("UI実行時の「再生した要求だけ」の比較に失敗しました。");
        CompareScopeBox.SelectedIndex=0; await _compareLoad;
        // 02 narrowed to one folder (「場所で絞り込む」 keeps it in the record): the tree of folders, then a page of that folder only.
        var tree=LocationTree(SessionAnalyzer.Load(demo).Files);
        if(tree.Count!=1 || tree[0].Path!="D:" || tree[0].Children.Single().Name!="Production") throw new InvalidDataException("UI実行時の場所の一覧が不正です。");
        LocationFilter.Save(demo,LocationFilter.Of([@"D:\Production\scene_001\frames"])); await LoadSessionAsync(demo); ShowPage(1); await Capture("results-locations");
        if(ResultFiles.Items.Count!=16 || !LocationText.Text.Contains(@"scene_001\frames") || !ReplayLocationText.Text.Contains("frames")) throw new InvalidDataException("UI実行時の場所の絞り込みに失敗しました。");
        LocationFilter.Save(demo,new()); await LoadSessionAsync(demo);
        // The two dialogs, shown without waiting for them and rendered on their own.
        async Task CaptureDialog(Window dialog,string name)
        {
            dialog.Show(); await Task.Delay(3000); dialog.UpdateLayout();
            var root=(FrameworkElement)dialog.Content;
            var image=new RenderTargetBitmap(Math.Max(1,(int)root.ActualWidth),Math.Max(1,(int)root.ActualHeight),96,96,PixelFormats.Pbgra32);
            var drawing=new DrawingVisual();
            using(var context=drawing.RenderOpen()) { context.DrawRectangle(dialog.Background,null,new Rect(0,0,root.ActualWidth,root.ActualHeight)); context.DrawRectangle(new VisualBrush(root),null,new Rect(0,0,root.ActualWidth,root.ActualHeight)); }
            image.Render(drawing);
            var png=new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using(var file=File.Create(Path.Combine(output,name+".png"))) png.Save(file);
            dialog.Close();
        }
        if(LocationDialog(SessionAnalyzer.Load(demo),LocationFilter.Of([@"D:\Production\scene_001\frames"])).Dialog is not { } places) throw new InvalidDataException("UI実行時の場所の画面を作れませんでした。");
        await CaptureDialog(places,"locations-dialog");
        await CaptureDialog(AppDialog(),"app-dialog"); await CaptureDialog(AppDialog(1),"app-dialog-running");
        EventFilter.Text="SetEndOfFile";await RefreshEvents();
        if(EventGrid.Items.Count!=1) throw new InvalidDataException("イベント検索の結果が不正です。");
        string dataset=Path.Combine(output,"ui-dataset-"+Guid.NewGuid().ToString("N")[..6]);TargetBox.Text=dataset;
        await Task.Run(()=>DatasetBuilder.GenerateAsync(demo,dataset));
        var result=await Task.Run(()=>new ReplayEngine().RunAsync(demo,dataset,new ReplayOptions { Speed=1000,Workers=4 }));
        if(result.Failed>0 || result.WriteBytes!=262144) throw new InvalidDataException("UI実行時のリプレイ検証に失敗しました。");
        ShowPage(2);ReplayResultText.Text=$"UIスモークテスト完了\n実行 {result.Executed:N0} / 失敗 {result.Failed:N0}\nRead {result.ReadBytes/1048576.0:N1} MiB\nWrite {result.WriteBytes/1048576.0:N2} MiB\nP95 {result.P95Ms:N2} ms";await Capture("replay");
        // Benchmark page: a small configuration through the same buttons' code path (generate → run).
        var bench=new BenchmarkConfig
        {
            Label="UI smoke benchmark",Seed="7",Threads=4,TotalTransfer="24MiB",
            FileSets=[new() { Name="frames",Count=20,Size="256KiB",SizeJitter=0.2 },new() { Name="video",Count=2,Size="4MiB" },new() { Name="renders",Count=0,Size="256KiB" }],
            Workloads=[new() { Name="連番の読み込み",Weight=50,FileSet="frames",Operation=BenchOperation.Read,Pattern=BenchPattern.WholeFile,BlockSize="64KiB",FileOrder=BenchFileOrder.Sequential },
                       new() { Name="動画のランダム読み",Weight=30,FileSet="video",Operation=BenchOperation.Read,Pattern=BenchPattern.Random,BlockSize="16KiB",RunLength="256KiB" },
                       new() { Name="レンダリング出力",Weight=20,FileSet="renders",Operation=BenchOperation.Write,Pattern=BenchPattern.WholeFile,BlockSize="64KiB" }]
        };
        string benchConfig=Path.Combine(output,"bench-config.json"); JsonStore.Write(benchConfig,bench);
        ShowPage(3); BenchConfigBox.Text=benchConfig; BenchSeedBox.Text=""; BenchTargetBox.Text=Path.Combine(output,"bench-target"); ShowBenchConfig();
        if(!await RunBenchAsync(true,false) || !await RunBenchAsync(false,true) || BenchResultsGrid.Items.Count!=3 || _benchReport is null || !File.Exists(_benchReport)) throw new InvalidDataException("UI実行時のベンチマークに失敗しました。");
        if(!await ResetBenchDatasetAsync() || !await RunBenchAsync(false,true)) throw new InvalidDataException("UI実行時の「初期状態に戻す」と再実行に失敗しました。");
        await Capture("benchmark");
        JsonStore.Write(Path.Combine(output,"ui-verification.json"),new { passed=true,rasterPreviewAvailable=rasterAvailable,events=_analysis?.TotalEvents,result.Executed,result.Failed,result.ReadBytes,result.WriteBytes,benchmark=BenchTotal.Text });
    }
    private sealed record EventRow(double Seconds,string Operation,string Path,long Bytes,string Process,int Pid,long Offset,uint? Status);
    private sealed class Settings
    {
        public string Roots {get;set;}="";public string Label {get;set;}="";
        public string Lucid {get;set;}="";public string LucidId {get;set;}="";
        public string ReplayTarget {get;set;}="";public string BenchConfig {get;set;}="";public string BenchTarget {get;set;}="";
        public AppLaunch? LaunchApp {get;set;} public bool Launch {get;set;}=true;
    }
}
