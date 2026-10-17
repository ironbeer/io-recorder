using System.Windows.Controls;
using IoRecorder.Core;

namespace IoRecorder.App;

// The four number cards above the graphs: on 01 while recording (the last second), and for a whole record on 01 after
// it is saved and on 02.
public partial class MetricCards : UserControl
{
    public MetricCards() { InitializeComponent(); }

    public void ShowLive(AnalysisResult a,SecondMetrics? s)
    {
        ReadLabel.Text="READ / MiB/s（直近1秒）";WriteLabel.Text="WRITE / MiB/s（直近1秒）";
        IopsLabel.Text="R+W / IOPS（直近1秒）";LatencyLabel.Text="P95 LATENCY / ms（開始から）";
        ReadValue.Text=(s?.ReadMiB??0).ToString("N1");WriteValue.Text=(s?.WriteMiB??0).ToString("N1");
        IopsValue.Text=((s?.Reads??0)+(s?.Writes??0)).ToString("N0");
        ReadDetail.Text=$"合計 {a.ReadBytes/1048576.0:N1} MiB";WriteDetail.Text=$"合計 {a.WriteBytes/1048576.0:N1} MiB";
        IopsDetail.Text=$"{a.TotalEvents:N0} events";
        ShowLatency(a);
    }

    // Averages over the whole record hide the busy moments, so the busiest second is shown next to them.
    public void ShowRecord(AnalysisResult a)
    {
        double seconds=Math.Max(1,a.DurationSeconds);
        ReadLabel.Text="READ / MiB/s（記録全体の平均）";WriteLabel.Text="WRITE / MiB/s（記録全体の平均）";
        IopsLabel.Text="R+W / IOPS（記録全体の平均）";LatencyLabel.Text="P95 LATENCY / ms（記録全体）";
        ReadValue.Text=(a.ReadBytes/1048576.0/seconds).ToString("N1");WriteValue.Text=(a.WriteBytes/1048576.0/seconds).ToString("N1");
        IopsValue.Text=((a.Reads+a.Writes)/seconds).ToString("N0");
        ReadDetail.Text=$"最大 {a.Timeline.Select(p=>p.ReadMiB).DefaultIfEmpty(0).Max():N1} MiB/s · 合計 {a.ReadBytes/1048576.0:N1} MiB";
        WriteDetail.Text=$"最大 {a.Timeline.Select(p=>p.WriteMiB).DefaultIfEmpty(0).Max():N1} MiB/s · 合計 {a.WriteBytes/1048576.0:N1} MiB";
        IopsDetail.Text=$"最大 {a.Timeline.Select(p=>p.Reads+p.Writes).DefaultIfEmpty(0).Max():N0} IOPS · 合計 {a.Reads+a.Writes:N0} 回";
        ShowLatency(a);
    }

    private void ShowLatency(AnalysisResult a)
    {
        LatencyValue.Text=a.P95Ms?.ToString("N2")??"—";
        LatencyDetail.Text=$"P50 {a.P50Ms?.ToString("N2")??"—"} · P99 {a.P99Ms?.ToString("N2")??"—"}";
    }
}
