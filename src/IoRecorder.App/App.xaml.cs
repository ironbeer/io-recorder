using System.Windows;

namespace IoRecorder.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if(e.Args.Contains("--smoke-test"))
        {
            System.Windows.Media.RenderOptions.ProcessRenderMode=System.Windows.Interop.RenderMode.SoftwareOnly;
            // Smoke tests keep everything they write (settings, configs, reports) inside their own output folder.
            int at=Array.IndexOf(e.Args,"--smoke-test");
            if(at+1<e.Args.Length) Environment.SetEnvironmentVariable("IORECORDER_HOME",System.IO.Path.Combine(System.IO.Path.GetFullPath(e.Args[at+1]),"home"));
        }
        DispatcherUnhandledException+=(s,args)=>
        {
            System.Windows.MessageBox.Show(args.Exception.Message,"IO Recorder",MessageBoxButton.OK,MessageBoxImage.Error);
            args.Handled=true;
        };
        new MainWindow(e.Args).Show();
    }
}
