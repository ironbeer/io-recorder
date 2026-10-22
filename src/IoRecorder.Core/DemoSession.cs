using System.Text.Json;

namespace IoRecorder.Core;

public static class DemoSession
{
    public static string Create(string output)
    {
        if(Directory.Exists(output)&&Directory.EnumerateFileSystemEntries(output).Any()) throw new IOException("デモ保存先が空ではありません。");
        Directory.CreateDirectory(output);
        var start=DateTimeOffset.UtcNow;
        var manifest=new SessionManifest
        {
            Tool=ToolVersion.Text,Label="DEMO / アニメ制作ワークロード（合成データ）",State="SyntheticDemo",StartedUtc=start,EndedUtc=start.AddSeconds(12),
            Options=new() { Mode=CaptureMode.Hybrid,Roots=[@"D:\Production"],ProcessNames=["AfterFX"] },TotalPhysicalBytes=32UL*1024*1024*1024,
            Warnings=["これは画面確認用の合成データです。実測結果ではありません。"]
        };
        var events=new List<IoEvent>(); long seq=0;
        IoEvent Add(long time,IoOperation op,string path,string handle,int tid=100,long offset=0,long size=0)
        {
            var e=new IoEvent { Sequence=++seq,StartUs=time,EndUs=time+100+(seq%30)*110,Operation=op,ProcessId=4242,ThreadId=tid,ProcessName="AfterFX",Path=path,FileObject=handle,Offset=offset,Size=size,Transferred=op is IoOperation.Read or IoOperation.Write?size:null,IoFlags=op is IoOperation.Read or IoOperation.Write?0u:null,NtStatus=0,MatchesProcess=true,MatchesRoot=true };
            events.Add(e); return e;
        }
        string source=@"D:\Production\scene_001\background.psd";
        var open=Add(100000,IoOperation.Open,source,"A"); open.CreateDisposition=1; open.ExtraInfo=1; open.ShareAccess=7;
        for(int i=0;i<72;i++) Add(150000+i*135000L,IoOperation.Read,source,"A",100,(i%16)*65536,65536);
        Add(11_000_000,IoOperation.Cleanup,source,"A"); Add(11_100_000,IoOperation.Close,source,"A");
        for(int frame=0;frame<16;frame++)
        {
            string path=$@"D:\Production\scene_001\frames\frame_{frame+1:0000}.exr",h="F"+frame; long t=500000+frame*400000L;
            var e=Add(t,IoOperation.Open,path,h,101); e.CreateDisposition=1; e.ExtraInfo=1; e.ShareAccess=7;
            for(int j=0;j<8;j++) Add(t+10000+j*15000,IoOperation.Read,path,h,101,j*32768,32768);
            Add(t+180000,IoOperation.Cleanup,path,h,101); Add(t+190000,IoOperation.Close,path,h,101);
        }
        string created=@"D:\Production\render\preview.tmp";
        var c=Add(7_000_000,IoOperation.Open,created,"C",102); c.CreateDisposition=2;c.ExtraInfo=2;c.ShareAccess=7;
        Add(7_100_000,IoOperation.Write,created,"C",102,0,131072);
        Add(7_200_000,IoOperation.Write,created,"C",102,131072,131072);
        var eof=Add(7_300_000,IoOperation.SetEndOfFile,created,"C",102,0,196608);eof.InfoClass=20;eof.ExtraInfo=196608;
        Add(7_400_000,IoOperation.Flush,created,"C",102);Add(7_500_000,IoOperation.Cleanup,created,"C",102);Add(7_600_000,IoOperation.Close,created,"C",102);
        manifest.EventsWritten=events.Count;
        JsonStore.Write(Path.Combine(output,"session.json"),manifest);
        File.WriteAllLines(Path.Combine(output,"events.jsonl"),events.OrderBy(e=>e.EndUs).Select(e=>JsonSerializer.Serialize(e,JsonStore.Options)));
        File.WriteAllLines(Path.Combine(output,"sizes.jsonl"),events.Select(e=>e.Path).Distinct().Select(p=>JsonSerializer.Serialize(new SizeObservation { Path=p,ObservedUs=1_000_000,Size=p==source?2*1024*1024:p==created?196608:262144 },JsonStore.Options)));
        File.WriteAllLines(Path.Combine(output,"memory.jsonl"),Enumerable.Range(0,13).Select(s=>JsonSerializer.Serialize(new MemorySample { TimeUs=s*1_000_000,TotalBytes=manifest.TotalPhysicalBytes,AvailableBytes=(ulong)(20*1073741824L-s%5*512*1048576L),CommitBytes=12UL*1073741824,CommitLimitBytes=48UL*1073741824,SystemCacheBytes=4UL*1073741824,Processes=[new(4242,"AfterFX",5*1073741824L,6*1073741824L)] },JsonStore.Options)));
        SessionAnalyzer.Export(output);
        return output;
    }

    // A synthetic recording of a replay of session (for checks without ETW): the same requests on the dummy paths below
    // dataset, starting 1.5 s later and taking slowerBy times as long. Written to session\replays\<time> with its link.
    public static string CreateReplayTrace(string session,string dataset,double slowerBy=1.5)
    {
        session=Path.GetFullPath(session); dataset=Path.GetFullPath(dataset);
        string trace=AppPaths.NewFolder(Path.Combine(session,ReplayCapture.Folder),"replay");
        Directory.CreateDirectory(trace);
        var original=JsonStore.Read<SessionManifest>(Path.Combine(session,"session.json"));
        var link=new ReplayLink { Tool=ToolVersion.Text,OriginalSession=session,Dataset=dataset,StartedUtc=DateTimeOffset.UtcNow,Options=new() { Mode=ReplayMode.ThinkTime },ProcessId=Environment.ProcessId };
        string Dummy(string path)
        {
            if(link.Paths.FirstOrDefault(p=>p.Value.Equals(path,StringComparison.OrdinalIgnoreCase)) is { Key: not null } known) return known.Key;
            string full=Path.Combine(dataset,PathPolicy.DatasetRelative(path)); link.Paths[full]=path; return full;
        }
        var events=RecordLog.Read<IoEvent>(session, RecordLog.Events).Where(e=>e.IsFileIo && !string.IsNullOrEmpty(e.Path)).Select(e=>
        {
            long duration=(e.EndUs??e.StartUs)-e.StartUs;
            e.Path=Dummy(e.Path); e.StartUs+=1_500_000; e.EndUs=e.StartUs+(long)(duration*slowerBy);
            e.ProcessId=Environment.ProcessId; e.ProcessName="IORecorder"; e.MatchesProcess=false; e.MatchesRoot=true; return e;
        }).ToList();
        var start=DateTimeOffset.UtcNow;
        JsonStore.Write(Path.Combine(trace,"session.json"),new SessionManifest
        {
            Tool=ToolVersion.Text,Label=$"再生の記録: {original.Label}（合成データ）",State="SyntheticDemo",StartedUtc=start,EndedUtc=start+(original.EndedUtc-original.StartedUtc)+TimeSpan.FromSeconds(3),
            Options=new() { Mode=CaptureMode.Folder,Roots=[dataset] },EventsWritten=events.Count,Warnings=["画面確認用の合成データです。実測結果ではありません。"]
        });
        File.WriteAllLines(Path.Combine(trace,"events.jsonl"),events.Select(e=>JsonSerializer.Serialize(e,JsonStore.Options)));
        File.WriteAllText(Path.Combine(trace,"sizes.jsonl"),""); File.WriteAllText(Path.Combine(trace,"memory.jsonl"),"");
        SessionAnalyzer.Export(trace);
        link.ElapsedSeconds=events.Count==0?0:(events.Max(e=>e.EndUs??e.StartUs)-events.Min(e=>e.StartUs))/1_000_000.0;
        JsonStore.Write(Path.Combine(trace,ReplayCapture.LinkFile),link);
        return trace;
    }
}
