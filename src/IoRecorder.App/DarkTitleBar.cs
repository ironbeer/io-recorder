using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace IoRecorder.App;

// Dark caption on Windows 10 20H1+ and 11; Windows 11 also takes the exact caption and border colours. Older systems ignore it.
internal static class DarkTitleBar
{
    public static void Apply(Window window)=>window.SourceInitialized+=(s,e)=>
    {
        var handle=new WindowInteropHelper(window).Handle;
        int dark=1,caption=0x001D120C,border=0x004D3729; // COLORREF 0x00BBGGRR: #0C121D, #29374D
        DwmSetWindowAttribute(handle,20,ref dark,sizeof(int));
        DwmSetWindowAttribute(handle,35,ref caption,sizeof(int));
        DwmSetWindowAttribute(handle,34,ref border,sizeof(int));
    };
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window,int attribute,ref int value,int size);
}
