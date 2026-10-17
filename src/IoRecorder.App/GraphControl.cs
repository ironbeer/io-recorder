using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using IoRecorder.Core;

namespace IoRecorder.App;

// Transfer, IOPS and P95 latency stacked on one time axis. Hovering draws a cursor through all three panels and shows
// that second's values in each panel's title line. With a comparison, the replay is drawn dashed over the original.
public sealed class GraphControl : FrameworkElement
{
    private static readonly Brush ReadBrush=Frozen(116,168,255),WriteBrush=Frozen(101,221,200),Muted=Frozen(148,165,190),TitleBrush=Frozen(220,230,244);
    private static readonly Pen GridPen=FrozenPen(Frozen(40,56,79),1),TimeGridPen=FrozenPen(Frozen(30,43,63),1),CursorPen=FrozenPen(Frozen(200,212,230),1);
    private static readonly Pen ReadPen=FrozenPen(ReadBrush,2),WritePen=FrozenPen(WriteBrush,2),ReadDash=Dashed(ReadBrush),WriteDash=Dashed(WriteBrush);
    private SecondMetrics[] _data=[];
    private SecondMetrics[]? _compare;
    private bool _hasData;
    private int? _cursor;
    public string EmptyText { get;set; }="計測を開始するとグラフを表示します";

    private sealed record Panel(string Title,Func<SecondMetrics,double?> Read,Func<SecondMetrics,double?> Write,bool Log,Func<double?,string> Value);
    private static readonly Panel[] Panels=
    [
        new("転送量（MiB/s）",s=>s.ReadMiB,s=>s.WriteMiB,false,v=>(v??0).ToString("N2")),
        new("IOPS（回/秒）",s=>s.Reads,s=>s.Writes,false,v=>(v??0).ToString("N0")),
        new("レイテンシ P95（ms・対数目盛）",s=>s.ReadP95Ms,s=>s.WriteP95Ms,true,v=>v?.ToString("N2")??"—"),
    ];

    public void SetTimeline(IEnumerable<SecondMetrics> points,bool recent=false,int? endSecond=null)
    {
        var bySecond=points.ToDictionary(p=>p.Second);
        _hasData=bySecond.Count>0;
        int end=Math.Max(0,endSecond??bySecond.Keys.DefaultIfEmpty(0).Max());
        int start=recent?Math.Max(0,end-119):0;
        _data=Dense(bySecond,start,end); _compare=null;
        InvalidateVisual();
    }

    // The original (solid) and a replay (dashed) on one time axis; both timelines start at their first second with I/O.
    public void SetComparison(IEnumerable<SecondMetrics> original,IEnumerable<SecondMetrics> replay)
    {
        var a=original.ToDictionary(p=>p.Second); var b=replay.ToDictionary(p=>p.Second);
        _hasData=a.Count+b.Count>0;
        int end=Math.Max(a.Keys.DefaultIfEmpty(0).Max(),b.Keys.DefaultIfEmpty(0).Max());
        _data=Dense(a,0,end); _compare=Dense(b,0,end);
        InvalidateVisual();
    }
    private static SecondMetrics[] Dense(Dictionary<int,SecondMetrics> bySecond,int start,int end)
        =>Enumerable.Range(start,end-start+1).Select(t=>bySecond.GetValueOrDefault(t)??new SecondMetrics { Second=t }).ToArray();

    private const double Left=56,AxisHeight=22,Gap=16,TitleHeight=22;
    private double Right=>ActualWidth-10;

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); double w=ActualWidth,h=ActualHeight;
        if(w<160||h<180) return;
        // Transparent fill so the whole area receives the mouse.
        dc.DrawRectangle(Brushes.Transparent,null,new Rect(0,0,w,h));
        double pixelsPerDip=VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText Text(string text,Brush brush,double size=10)=>new(text,CultureInfo.CurrentUICulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),size,brush,pixelsPerDip);
        if(!_hasData) { dc.DrawText(Text(EmptyText,Muted,11),new Point(Left+20,40));return; }
        int start=_data[0].Second,end=Math.Max(start+1,_data[^1].Second);
        double X(double t)=>Left+(t-start)/(end-start)*(Right-Left);
        double panelHeight=(h-AxisHeight-Gap*2)/3;
        int step=new[] { 1,2,5,10,15,30,60,120,300,600,900,1800,3600,7200,14400 }.FirstOrDefault(s=>(end-start)/(double)s<=8,14400);
        int firstTick=(start+step-1)/step*step;
        var hovered=_cursor is { } c && c>=start && c<=_data[^1].Second?_data[c-start]:null;
        var hoveredReplay=hovered is not null?_compare?[hovered.Second-start]:null;

        for(int i=0;i<Panels.Length;i++)
        {
            var panel=Panels[i];
            double top=i*(panelHeight+Gap),y0=top+TitleHeight,y1=top+panelHeight;
            dc.DrawText(Text(panel.Title,TitleBrush,11),new Point(Left,top));
            if(hovered is not null)
            {
                var readout=Text(hoveredReplay is null?$"{Clock(hovered.Second,end)}   Read {panel.Value(panel.Read(hovered))}   Write {panel.Value(panel.Write(hovered))}"
                    :$"{Clock(hovered.Second,end)}   Read {panel.Value(panel.Read(hovered))} / {panel.Value(panel.Read(hoveredReplay))}   Write {panel.Value(panel.Write(hovered))} / {panel.Value(panel.Write(hoveredReplay))}（元 / 再生）",Muted);
                dc.DrawText(readout,new Point(Math.Max(Left+150,Right-readout.Width),top+1));
            }
            var values=_data.Concat(_compare??[]).SelectMany(s=>new[] { panel.Read(s),panel.Write(s) }).Where(v=>v.HasValue && (!panel.Log || v>0)).Select(v=>v!.Value).ToList();
            Func<double,double> y;
            var ticks=new List<double>();
            if(panel.Log)
            {
                if(values.Count==0) { dc.DrawText(Text("この範囲にはレイテンシの記録がありません",Muted,11),new Point(Left+12,y0+24));continue; }
                int lo=(int)Math.Floor(Math.Log10(values.Min())),hi=(int)Math.Ceiling(Math.Log10(values.Max()));
                if(hi<=lo) hi=lo+1;
                y=v=>y1-(Math.Log10(v)-lo)/(hi-lo)*(y1-y0);
                for(int k=lo;k<=hi;k++) ticks.Add(Math.Pow(10,k));
            }
            else
            {
                double stepValue=Nice(Math.Max(values.DefaultIfEmpty(0).Max(),1e-6)/4);
                y=v=>y1-v/(stepValue*4)*(y1-y0);
                for(int k=0;k<=4;k++) ticks.Add(stepValue*k);
            }
            foreach(var v in ticks)
            {
                double ty=y(v);dc.DrawLine(GridPen,new Point(Left,ty),new Point(Right,ty));
                var label=Text(Format(v),Muted);dc.DrawText(label,new Point(Left-8-label.Width,ty-7));
            }
            for(int t=firstTick;t<=end;t+=step) if(t>start) dc.DrawLine(TimeGridPen,new Point(X(t),y0),new Point(X(t),y1));
            DrawSeries(dc,_data,panel.Read,panel.Log,ReadPen,ReadBrush,X,y);
            DrawSeries(dc,_data,panel.Write,panel.Log,WritePen,WriteBrush,X,y);
            if(_compare is not null)
            {
                DrawSeries(dc,_compare,panel.Read,panel.Log,ReadDash,ReadBrush,X,y);
                DrawSeries(dc,_compare,panel.Write,panel.Log,WriteDash,WriteBrush,X,y);
            }
        }
        for(int t=firstTick;t<=end;t+=step)
        {
            var label=Text(Clock(t,end),Muted);
            dc.DrawText(label,new Point(Math.Clamp(X(t)-label.Width/2,Left,Right-label.Width),h-16));
        }
        if(hovered is not null) dc.DrawLine(CursorPen,new Point(X(hovered.Second),0),new Point(X(hovered.Second),h-AxisHeight));
    }

    // Linear series treat a missing second as zero; latency leaves a gap and marks a lone second with a dot.
    private static void DrawSeries(DrawingContext dc,SecondMetrics[] data,Func<SecondMetrics,double?> value,bool log,Pen pen,Brush dot,Func<double,double> x,Func<double,double> y)
    {
        var geometry=new StreamGeometry();
        using(var context=geometry.Open())
        {
            bool drawing=false;
            for(int i=0;i<data.Length;i++)
            {
                double? v=value(data[i]);
                if(log && (v is null || v<=0)) { drawing=false;continue; }
                var point=new Point(x(data[i].Second),y(v??0));
                if(drawing) context.LineTo(point,true,false);
                else
                {
                    context.BeginFigure(point,false,false);drawing=true;
                    if(log && (i+1>=data.Length || value(data[i+1]) is not > 0)) dc.DrawEllipse(dot,null,point,2,2);
                }
            }
        }
        geometry.Freeze();dc.DrawGeometry(null,pen,geometry);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);if(!_hasData) return;
        int start=_data[0].Second,end=Math.Max(start+1,_data[^1].Second);
        double fraction=Math.Clamp((e.GetPosition(this).X-Left)/Math.Max(1,Right-Left),0,1);
        int second=Math.Min(_data[^1].Second,(int)Math.Round(start+fraction*(end-start)));
        if(second!=_cursor) { _cursor=second;InvalidateVisual(); }
    }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e);_cursor=null;InvalidateVisual(); }

    private static double Nice(double value)
    {
        double magnitude=Math.Pow(10,Math.Floor(Math.Log10(value))),f=value/magnitude;
        return (f<=1?1:f<=2?2:f<=2.5?2.5:f<=5?5:10)*magnitude;
    }
    private static string Format(double v)=>v>=100?v.ToString("N0"):v.ToString("0.###",CultureInfo.InvariantCulture);
    private static string Clock(int t,int end)=>end>=3600?$"{t/3600}:{t%3600/60:00}:{t%60:00}":end>=60?$"{t/60}:{t%60:00}":$"{t} s";
    private static SolidColorBrush Frozen(byte r,byte g,byte b) { var brush=new SolidColorBrush(Color.FromRgb(r,g,b));brush.Freeze();return brush; }
    private static Pen FrozenPen(Brush brush,double thickness) { var pen=new Pen(brush,thickness);pen.Freeze();return pen; }
    private static Pen Dashed(Brush brush) { var pen=new Pen(brush,1.6) { DashStyle=new DashStyle([4,3],0) };pen.Freeze();return pen; }
}
