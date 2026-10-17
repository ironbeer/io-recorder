using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using IoRecorder.Core;
using Microsoft.Win32;

namespace IoRecorder.App;

// Benchmark page: generate (I/O list from config + seed, then dummy files on the target) → run, with the replay engine.
// Generating and running are separate steps so LucidLink uploads and cache conditions can be prepared in between.
public partial class MainWindow
{
    private string? _benchFolder,_benchDataset,_benchConsumed,_benchReport;
    private BenchmarkPlan? _benchPlanInfo;
    private bool _benchRunning;
    private static string BundledSamples=>Path.Combine(AppContext.BaseDirectory,"samples");
    private static string SampleConfig=>Path.Combine(AppPaths.Configs,"benchmark-anime.json");

    private void ShowBench(object sender,RoutedEventArgs e)=>ShowPage(3);
    private void InitBench()
    {
        // Configurations live in Documents\IORecorder\Configs; the bundled samples are copied there once.
        try { AppPaths.EnsureSampleConfigs(BundledSamples); } catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { LogText.Text="サンプル構成をコピーできませんでした: "+ex.Message; }
        if((string.IsNullOrWhiteSpace(BenchConfigBox.Text) || !File.Exists(BenchConfigBox.Text)) && File.Exists(SampleConfig)) BenchConfigBox.Text=SampleConfig;
        BenchConfigBox.LostFocus+=(s,e)=>ShowBenchConfig();
        ShowBenchConfig(); UpdateBenchSteps(0);
    }
    private void ShowBenchConfig()
    {
        try
        {
            if(!File.Exists(BenchConfigBox.Text.Trim())) { BenchWorkloadsGrid.ItemsSource=null; BenchConfigSummary.Text=BenchConfigBox.Text.Length>0?"構成ファイルが見つかりません":""; return; }
            ShowBenchRows(BenchmarkPlanner.Load(BenchConfigBox.Text.Trim()),null);
        }
        catch(Exception ex) { BenchWorkloadsGrid.ItemsSource=null; BenchConfigSummary.Text="構成を読めません: "+ex.Message; }
    }
    private void ShowBenchRows(BenchmarkConfig c,BenchmarkPlan? plan)
    {
        double sum=c.Workloads.Sum(w=>Math.Max(0,w.Weight));
        var sets=c.FileSets.GroupBy(f=>f.Name,StringComparer.OrdinalIgnoreCase).ToDictionary(g=>g.Key,g=>g.First(),StringComparer.OrdinalIgnoreCase);
        string Pattern(BenchPattern p)=>p switch { BenchPattern.Sequential=>"シーケンシャル",BenchPattern.Random=>"ランダム",_=>"ファイル全体" };
        BenchWorkloadsGrid.ItemsSource=c.Workloads.Select(w=>new BenchRow(w.Name,
            sum>0?(w.Weight/sum).ToString("P0",CultureInfo.InvariantCulture):"—",
            $"{w.Operation} · {Pattern(w.Pattern)} · {w.BlockSize}",
            sets.TryGetValue(w.FileSet,out var s)?(s.Count>0?$"{s.Name}: {s.Size} × {s.Count:N0}":$"{s.Name}: 新規 {s.Size}"):w.FileSet,
            plan is not null && plan.WorkloadBytes.TryGetValue(w.Name,out var b)?Bytes(b):"—")).ToArray();
        BenchConfigSummary.Text=$"seed {c.Seed} · {c.Threads} スレッド · {BenchmarkPlanner.Describe(c)}"+(plan is null?"":$" · ダミー {plan.Files:N0} ファイル / {Bytes(plan.InitialBytes)}"+(plan.NewFileBytes>0?$" · 新規書き込み {Bytes(plan.NewFileBytes)}":""));
    }
    private static string Bytes(long bytes)=>bytes>=1L<<30?$"{bytes/1073741824.0:N2} GiB":$"{bytes/1048576.0:N1} MiB";
    private void UseBenchSample(object sender,RoutedEventArgs e)
    {
        try
        {
            // Re-creates the sample in Configs if it was deleted; an existing (possibly edited) copy is kept.
            AppPaths.EnsureSampleConfigs(BundledSamples);
            if(!File.Exists(SampleConfig)) throw new FileNotFoundException("同梱のサンプル構成が見つかりません: "+BundledSamples);
            BenchConfigBox.Text=SampleConfig; ShowBenchConfig();
        }
        catch(Exception ex) { ReportError(ex); }
    }
    private void BrowseBenchConfig(object sender,RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.Configs);
        var d=new OpenFileDialog { Title="ベンチマーク構成ファイル",Filter="JSON (*.json)|*.json|すべてのファイル|*.*",InitialDirectory=AppPaths.Configs };
        if(d.ShowDialog(this)==true) { BenchConfigBox.Text=d.FileName; ShowBenchConfig(); }
    }
    private void EditBenchConfig(object sender,RoutedEventArgs e)
    {
        try
        {
            string path=BenchConfigBox.Text.Trim();
            if(!File.Exists(path)) throw new FileNotFoundException("構成ファイルが見つかりません: "+path);
            Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList={ path },UseShellExecute=false });
            LogText.Text="構成ファイルをメモ帳で開きました。保存後にダミーファイルを生成すると反映されます: "+path;
        }
        catch(Exception ex) { ReportError(ex); }
    }
    private void BrowseBenchTarget(object sender,RoutedEventArgs e) { if(PickFolder("試験先フォルダ（NAS・LucidLinkなど）") is { } p) BenchTargetBox.Text=p; }
    private void OpenBenchFolder(object sender,RoutedEventArgs e)
    {
        string dir=_benchFolder ?? AppPaths.Benchmarks;
        Directory.CreateDirectory(dir);
        if(Directory.Exists(dir)) Process.Start(new ProcessStartInfo(dir) { UseShellExecute=true });
    }
    private void OpenBenchReport(object sender,RoutedEventArgs e)
    {
        if(_benchReport is not null && File.Exists(_benchReport)) Process.Start(new ProcessStartInfo(_benchReport) { UseShellExecute=true });
    }
    private async void BenchGenerate(object sender,RoutedEventArgs e)=>await RunBenchAsync(true,false);
    private async void BenchRun(object sender,RoutedEventArgs e)=>await RunBenchAsync(false,true);
    private async void BenchReset(object sender,RoutedEventArgs e)=>await ResetBenchDatasetAsync();
    private async Task<bool> ResetBenchDatasetAsync()
    {
        if(_stop is not null || _benchConsumed is null) return false;
        bool ok=false;
        try
        {
            string dataset=_benchConsumed;
            _stop=new(); _benchRunning=true; _elapsed.Restart(); SetBusy(true); StatusBadge.Text="●  RESETTING";
            var token=_stop.Token; var work=new Progress<WorkProgress>(ShowBenchProgress);
            await Task.Run(()=>DatasetBuilder.ResetAsync(dataset,_progress,token,work));
            _benchDataset=dataset; _benchConsumed=null; ok=true; StatusBadge.Text="●  READY";
            BenchStage.Text="初期状態に戻しました。LucidLinkならアップロードとキャッシュ条件を整えてから、もう一度実行できます。";
        }
        catch(OperationCanceledException) { BenchStage.Text="中止しました。もう一度「ダミーを初期状態に戻す」を押してください。"; StatusBadge.Text="●  CANCELLED"; }
        catch(Exception ex) { BenchStage.Text="エラー: "+ex.Message; StatusBadge.Text="●  ERROR"; ReportError(ex); }
        finally { _benchRunning=false; FinishWork(); UpdateBenchSteps(0); }
        return ok;
    }

    private async Task<bool> RunBenchAsync(bool generate,bool run)
    {
        if(_stop is not null) return false;
        bool ok=false;
        try
        {
            if(run && !generate && _benchDataset is null) throw new InvalidOperationException(_benchConsumed is not null?"このダミーは実行済みです。「ダミーを初期状態に戻す」を押すか、ダミーを生成し直してください。":"先に「1. ダミーファイルを生成」してください。");
            BenchmarkConfig? config=null; string? targetRoot=null,configName=null;
            if(generate)
            {
                configName=Path.GetFileNameWithoutExtension(BenchConfigBox.Text.Trim());
                config=BenchmarkPlanner.Load(BenchConfigBox.Text.Trim());
                // Blank or symbols only (e.g. a stray `) keeps the configuration's seed; any other text is a seed.
                if(Seeds.Normalize(BenchSeedBox.Text) is { } seed) config.Seed=seed;
                targetRoot=PathPolicy.NormalizeRoot(BenchTargetBox.Text.Trim());
            }
            _stop=new(); _benchRunning=true; _elapsed.Restart(); SetBusy(true); StatusBadge.Text="●  BENCHMARK";
            var token=_stop.Token; var work=new Progress<WorkProgress>(ShowBenchProgress);
            if(generate)
            {
                // The I/O list is derived from the configuration and seed each time; it is an internal step of generating.
                // Benchmarks\yyyyMMdd-HHmmss_<config>_seed<N>; the dummy folder on the test target gets the same name.
                string folder=AppPaths.NewFolder(AppPaths.Benchmarks,$"{configName}_{Seeds.Tag(Seeds.Normalize(config!.Seed)??"42")}"),name=Path.GetFileName(folder);
                UpdateBenchSteps(1); BenchResultsGrid.ItemsSource=null; BenchTotal.Text=""; _benchReport=null; _benchDataset=_benchConsumed=null;
                _benchPlanInfo=await Task.Run(()=>BenchmarkPlanner.Create(config,folder,work),token);
                _benchFolder=folder; ShowBenchRows(config,_benchPlanInfo);
                token.ThrowIfCancellationRequested();
                string dataset=Path.Combine(targetRoot!,name);
                for(int i=2;Directory.Exists(dataset) && Directory.EnumerateFileSystemEntries(dataset).Any();i++) dataset=Path.Combine(targetRoot!,name+"-"+i);
                // Before writing anything: the dummy plus the files the run will create must fit on the test target.
                BenchmarkPlanner.CheckSpace(_benchPlanInfo,dataset,includeDummy:true);
                int contentSeed=Seeds.Value(config.Seed);
                await Task.Run(()=>DatasetBuilder.GenerateAsync(folder,dataset,contentSeed,_progress,token,work));
                _benchDataset=dataset; SaveSettings();
                if(!run) BenchStage.Text="ダミーファイルを生成しました。準備ができたら「2. ベンチマークを実行」を押してください。";
            }
            if(run)
            {
                token.ThrowIfCancellationRequested();
                string session=_benchFolder!,dataset=_benchDataset!;
                var info=_benchPlanInfo ?? JsonStore.Read<BenchmarkPlan>(Path.Combine(session,BenchmarkPlanner.PlanFile));
                UpdateBenchSteps(2);
                BenchmarkPlanner.CheckSpace(info,dataset,includeDummy:false);
                var result=await Task.Run(()=>new ReplayEngine().RunAsync(session,dataset,new ReplayOptions { Mode=info.RecommendedTiming,Workers=info.Config.Threads },_progress,token,work));
                _benchConsumed=dataset; _benchDataset=null;
                var rows=await Task.Run(()=>BenchmarkReport.Write(session,result,dataset));
                _benchReport=BenchmarkReport.ReportPath(session,result);
                BenchResultsGrid.ItemsSource=rows;
                BenchTotal.Text=$"合計 {rows.Sum(r=>r.MiBps):N1} MiB/s · {rows.Sum(r=>r.Iops):N0} IOPS · {result.ElapsedSeconds:N1} 秒 · 失敗 {result.Failed:N0}";
                StatusBadge.Text=result.Cancelled?"●  CANCELLED":result.Failed>0?"●  CHECK RESULTS":"●  COMPLETE";
                ok=!result.Cancelled && result.Failed==0;
                if(!result.Cancelled) BenchStage.Text="完了しました。同じダミーでもう一度実行するときは「ダミーを初期状態に戻す」を押してください。";
            }
            else { StatusBadge.Text="●  READY"; ok=true; }
        }
        catch(OperationCanceledException) { BenchStage.Text="中止しました。"; StatusBadge.Text="●  CANCELLED"; }
        catch(Exception ex) { BenchStage.Text="エラー: "+ex.Message; StatusBadge.Text="●  ERROR"; ReportError(ex); }
        finally { _benchRunning=false; FinishWork(); UpdateBenchSteps(0); }
        return ok;
    }
    private void ShowBenchProgress(WorkProgress p)
    {
        BenchStage.Text=p.Stage; BenchDetail.Text=p.Detail;
        BenchProgress.IsIndeterminate=p.Total==0 && _benchRunning; BenchProgress.Value=p.Fraction;
        BenchPercent.Text=p.Total>0?p.Fraction.ToString("P1",CultureInfo.InvariantCulture):"";
        LogText.Text=p.Stage+" · "+p.Detail;
    }
    private void UpdateBenchSteps(int active)
    {
        string Step(int n,string label,bool done)=>(n==active?"▶ ":done?"✓ ":"")+$"{n} {label}";
        BenchSteps.Text=$"{Step(1,"生成",_benchDataset is not null || _benchConsumed is not null)}   ›   {Step(2,"実行",BenchResultsGrid.ItemsSource is not null)}";
        BenchPaths.Text=$"レポート: {_benchFolder ?? "—"}\n試験データ: "+(_benchDataset ?? (_benchConsumed is not null?_benchConsumed+"（実行済み・「初期状態に戻す」で再利用可）":"—（生成すると作成）"));
        BenchReportButton.IsEnabled=_benchReport is not null && !_benchRunning;
        BenchResetButton.IsEnabled=_benchConsumed is not null && _stop is null;
        if(active==0) BenchProgress.IsIndeterminate=false;
    }
    private sealed record BenchRow(string Name,string Share,string Kind,string Files,string Planned);
}
