using System.Diagnostics;
using System.Windows;
using IoRecorder.Core;
using Microsoft.Win32;

namespace IoRecorder.App;

// A record as one zip to send (01 after a recording, 02 for the open record) and back (02 「zipから取り込む」): RecordArchive.
public partial class MainWindow
{
    // The record saved last in this window (01's button exports it, whatever 02 has open).
    private string? _lastRecording;

    private void ExportLastRecording(object sender,RoutedEventArgs e) { if(_lastRecording is { } session) _=ExportRecordAsync(session); }
    private void ExportOpenRecord(object sender,RoutedEventArgs e)
    {
        if(_session is { } session) _=ExportRecordAsync(session);
        else LogText.Text="先に「記録を開く…」で記録を開いてください。";
    }
    private async Task ExportRecordAsync(string session)
    {
        if(_stop is not null) return;
        var d=new OpenFolderDialog { Title="zipの保存先のフォルダ",Multiselect=false,InitialDirectory=Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) };
        AddNetworkPlaces(d);
        if(d.ShowDialog(this)!=true) return;
        string folder=d.FolderName;
        try
        {
            _stop=new(); SetBusy(true); StatusBadge.Text="●  EXPORTING";
            var token=_stop.Token; var work=new Progress<WorkProgress>(p=>LogText.Text=$"{p.Stage}  {p.Fraction*100:N0}%  ·  {p.Detail}");
            var zip=await Task.Run(()=>RecordArchive.ExportAsync(session,folder,work,token));
            StatusBadge.Text="●  SAVED"; LogText.Text="zipに書き出しました: "+zip;
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList={ "/select,"+zip },UseShellExecute=false });
        }
        catch(OperationCanceledException) { LogText.Text="zipの書き出しを中止しました。"; StatusBadge.Text="●  CANCELLED"; }
        catch(Exception ex) { ReportError(ex); StatusBadge.Text="●  ERROR"; }
        finally { FinishWork(); }
    }
    private async void ImportArchive(object sender,RoutedEventArgs e)
    {
        if(_stop is not null) return;
        var d=new OpenFileDialog { Title="記録のzip（「記録をzipで書き出す」で作ったもの）",Filter="zip (*.zip)|*.zip",InitialDirectory=Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) };
        AddNetworkPlaces(d);
        if(d.ShowDialog(this)!=true) return;
        string file=d.FileName;
        try
        {
            _stop=new(); SetBusy(true); StatusBadge.Text="●  IMPORTING";
            var token=_stop.Token; var work=new Progress<WorkProgress>(p=>LogText.Text=$"{p.Stage}  {p.Fraction*100:N0}%  ·  {p.Detail}");
            var folder=await Task.Run(()=>RecordArchive.Import(file,AppPaths.Recordings,work,token));
            await LoadSessionAsync(folder); ShowPage(1);
            StatusBadge.Text="●  READY"; LogText.Text="zipから取り込みました: "+folder;
        }
        catch(OperationCanceledException) { LogText.Text="取り込みを中止しました。"; StatusBadge.Text="●  CANCELLED"; }
        catch(Exception ex) { ReportError(ex); StatusBadge.Text="●  ERROR"; }
        finally { FinishWork(); }
    }
}
