using System.Diagnostics;
using System.Text.Json;
using IoRecorder.Core;

var testRoot=Path.Combine(Path.GetTempPath(),"IORecorder-tests-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);
// Keep everything the tools write (sort scratch, default folders) inside the test folder, never in the user's Documents.
Environment.SetEnvironmentVariable("IORECORDER_HOME",Path.Combine(testRoot,"home"));
var results=new List<(string Name,bool Passed,string? Error)>();
async Task Test(string name,Func<Task> test)
{
    try { await test();results.Add((name,true,null));Console.WriteLine("PASS "+name); }
    catch(Exception ex) { results.Add((name,false,ex.ToString()));Console.WriteLine("FAIL "+name+": "+ex.Message); }
}
void Check(bool condition,string message) { if(!condition) throw new Exception(message); }
async Task Throws<T>(Func<Task> action) where T:Exception
{ try { await action(); } catch(T) { return; } throw new Exception("Expected "+typeof(T).Name); }
string Sub(string name)=>Path.Combine(testRoot,name);

await Test("path boundaries, traversal, device mapping",async()=>
{
    Check(PathPolicy.IsUnder(@"C:\production\a.bin",@"C:\production"),"child");
    Check(!PathPolicy.IsUnder(@"C:\production-other\a.bin",@"C:\production"),"boundary");
    Check(PathPolicy.DatasetRelative(@"\\nas\share\a.bin")==@"UNC\nas\share\a.bin","UNC");
    await Throws<InvalidDataException>(()=>Task.Run(()=>PathPolicy.ResolveInside(testRoot,@"..\escape.bin")));
    await Throws<InvalidDataException>(()=>Task.Run(()=>PathPolicy.DatasetRelative(@"C:\x\CON.txt")));
    await Throws<InvalidDataException>(()=>Task.Run(()=>PathPolicy.DatasetRelative(@"C:\x\..\secret")));
    Check(new DevicePathMapper().Map(@"\Device\Mup\server\share\a")==@"\\server\share\a","Mup");
});
await Test("capture configuration rejects output inside target",async()=>
{
    await Throws<ArgumentException>(()=>Task.Run(()=>new RecordingOptions { Mode=CaptureMode.Folder,Roots=[@"C:\Production"] }.Validate(@"C:\Production\traces")));
    await Throws<ArgumentException>(()=>Task.Run(()=>new RecordingOptions { Mode=CaptureMode.Process,ProcessNames=[] }.Validate(Sub("record"))));
});
await Test("seeded random payload is deterministic and path specific",()=>
{
    var a=new byte[65536];var b=new byte[65536];var c=new byte[65536];
    var x=new DeterministicData(42,"a");var y=new DeterministicData(42,"a");var z=new DeterministicData(42,"b");
    x.Fill(a);y.Fill(b);z.Fill(c);
    Check(a.SequenceEqual(b),"same seed");Check(!a.SequenceEqual(c),"different files");Check(a.Distinct().Count()==256,"random bytes");return Task.CompletedTask;
});
await Test("memory sampler records physical and commit information",()=>
{
    var m=MemorySampler.Sample(123,[Environment.ProcessId]);
    Check(m.TotalBytes>=m.AvailableBytes&&m.AvailableBytes>0,"physical memory");
    Check(m.CommitLimitBytes>=m.CommitBytes&&m.CommitBytes>0,"commit");Check(m.Processes.Count==1,"process memory");return Task.CompletedTask;
});
string demo=DemoSession.Create(Sub("demo"));
await Test("analysis totals, range union, lazy file manifest",()=>
{
    var a=SessionAnalyzer.Analyze(demo);
    Check(a.Reads==200&&a.Writes==2,"request totals");Check(a.ReadBytes==8912896&&a.WriteBytes==262144,"byte totals");
    Check(a.UniqueReadBytesLowerBound==5*1048576,"unique range union");
    Check(a.FileManifest.Count==18,"only accessed files");
    var created=a.FileManifest.Single(f=>f.Path.EndsWith("preview.tmp"));
    Check(!created.InitiallyExists&&created.SuggestedInitialSize==0,"created file is absent initially");
    Check(a.FileManifest.Single(f=>f.Path.EndsWith("background.psd")).SizeConfidence.Contains("NotInitialSnapshot"),"uncertainty retained");return Task.CompletedTask;
});
await Test("HTML and CSV export handle hostile filenames as text",()=>
{
    string s=Sub("escaped");Directory.CreateDirectory(s);
    JsonStore.Write(Path.Combine(s,"session.json"),new SessionManifest { Label="<script>alert(1)</script>",State="Complete" });
    var e=new IoEvent { Sequence=1,Path=@"C:\Production\<script>.bin",Operation=IoOperation.Read,Size=4096,Transferred=4096,NtStatus=0,EndUs=10 };
    File.WriteAllText(Path.Combine(s,"events.jsonl"),JsonSerializer.Serialize(e,JsonStore.Options)+"\n");
    SessionAnalyzer.Export(s);
    Check(!File.ReadAllText(Path.Combine(s,"report.html")).Contains("<script>alert(1)</script>"),"HTML escaped");
    Check(!File.Exists(Path.Combine(s,"events.csv")),"CSV only on request");
    SessionAnalyzer.ExportCsv(s);
    Check(File.ReadAllText(Path.Combine(s,"events.csv")).Contains("\"C:\\Production\\<script>.bin\""),"CSV exists and quotes text");return Task.CompletedTask;
});
await Test("external sort preserves issue time across 25,000 record chunks",()=>
{
    string s=Sub("sort");Directory.CreateDirectory(s);
    using(var w=new StreamWriter(Path.Combine(s,"events.jsonl")))
        for(int i=50010;i>=0;i--) w.WriteLine(JsonSerializer.Serialize(new IoEvent { Sequence=i,StartUs=i/2,Operation=IoOperation.Read },JsonStore.Options));
    long previous=-1,seq=-1;int count=0;
    foreach(var e in EventOrder.Read(s)) { Check(e.StartUs>previous||e.StartUs==previous&&e.Sequence>=seq,"sorted order");previous=e.StartUs;seq=e.Sequence;count++; }
    Check(count==50011,"no lost records");return Task.CompletedTask;
});
foreach(var mode in new[]{ReplayMode.Timeline,ReplayMode.ThinkTime})
await Test("generate + replay "+mode,async()=>
{
    string target=Sub("dataset-"+mode);
    var m=await DatasetBuilder.GenerateAsync(demo,target);
    Check(m.InitialSizes.Count==17,"new file excluded");
    var result=await new ReplayEngine().RunAsync(demo,target,new() { Mode=mode,Speed=1000,Workers=4 });
    Check(result.Failed==0,"no failures");Check(result.ReadBytes==8912896&&result.WriteBytes==262144,"exact transfers");
    string output=PathPolicy.ResolveInside(target,m.Paths[@"D:\Production\render\preview.tmp"]);
    Check(new FileInfo(output).Length==196608,"EOF truncate");
    await Throws<InvalidOperationException>(()=>new ReplayEngine().RunAsync(demo,target,new()));
});
await Test("simultaneous read handles preserve read-only share semantics",async()=>
{
    string s=Sub("shared-read");Directory.CreateDirectory(s);JsonStore.Write(Path.Combine(s,"session.json"),new SessionManifest { State="Complete" });
    string path=@"C:\Production\shared.exr";
    var events=new[]
    {
        new IoEvent { Sequence=1,Operation=IoOperation.Open,Path=path,ProcessId=1,ThreadId=1,FileObject="A",CreateDisposition=1,ShareAccess=1,NtStatus=0,EndUs=10 },
        new IoEvent { Sequence=2,Operation=IoOperation.Open,Path=path,ProcessId=1,ThreadId=2,FileObject="B",CreateDisposition=1,ShareAccess=1,NtStatus=0,StartUs=20,EndUs=30 },
        new IoEvent { Sequence=3,Operation=IoOperation.Read,Path=path,ProcessId=1,ThreadId=1,FileObject="A",Size=4096,Transferred=4096,NtStatus=0,StartUs=40,EndUs=50 },
        new IoEvent { Sequence=4,Operation=IoOperation.Read,Path=path,ProcessId=1,ThreadId=2,FileObject="B",Offset=4096,Size=4096,Transferred=4096,NtStatus=0,StartUs=40,EndUs=60 },
        new IoEvent { Sequence=5,Operation=IoOperation.Cleanup,Path=path,ProcessId=1,ThreadId=1,FileObject="A",NtStatus=0,StartUs=100,EndUs=110 },
        new IoEvent { Sequence=6,Operation=IoOperation.Cleanup,Path=path,ProcessId=1,ThreadId=2,FileObject="B",NtStatus=0,StartUs=100,EndUs=110 }
    };
    File.WriteAllLines(Path.Combine(s,"events.jsonl"),events.Select(e=>JsonSerializer.Serialize(e,JsonStore.Options)));
    string d=Sub("shared-read-dataset");await DatasetBuilder.GenerateAsync(s,d);
    var result=await new ReplayEngine().RunAsync(s,d,new() { Speed=1,Workers=2 });
    Check(result.Failed==0&&result.ReadBytes==8192,"read-only opens must not request extra write permission");
});
await Test("create beyond EOF, truncate, allocation, rename and delete-on-close",async()=>
{
    string s=Sub("state-trace");Directory.CreateDirectory(s);var manifest=new SessionManifest { State="Complete" };JsonStore.Write(Path.Combine(s,"session.json"),manifest);
    var events=new List<IoEvent>();long seq=0;
    IoEvent Add(IoOperation op,string path,string handle,long size=0,long offset=0)
    {
        var e=new IoEvent { Sequence=++seq,StartUs=seq*100,EndUs=seq*100+10,ProcessId=1,ThreadId=1,Path=path,FileObject=handle,Operation=op,Size=size,Offset=offset,NtStatus=0,Transferred=op is IoOperation.Read or IoOperation.Write?size:null };
        events.Add(e);return e;
    }
    string first=@"E:\work\new.bin",renamed=@"E:\work\renamed.bin";
    var open=Add(IoOperation.Open,first,"A");open.CreateDisposition=2;open.ExtraInfo=2;open.ShareAccess=7;
    Add(IoOperation.Write,first,"A",8192,1048576);
    Add(IoOperation.SetEndOfFile,first,"A",4096);Add(IoOperation.SetAllocation,first,"A",8192);Add(IoOperation.Flush,first,"A");Add(IoOperation.Cleanup,first,"A");
    Add(IoOperation.Rename,first,"A").DestinationPath=renamed;
    var opened=Add(IoOperation.Open,renamed,"B");opened.CreateDisposition=1;opened.ExtraInfo=1;opened.ShareAccess=7;
    var delete=Add(IoOperation.Delete,renamed,"B");delete.InfoClass=13;delete.ExtraInfo=1;
    Add(IoOperation.Cleanup,renamed,"B");Add(IoOperation.Close,renamed,"B");
    File.WriteAllLines(Path.Combine(s,"events.jsonl"),events.Select(e=>JsonSerializer.Serialize(e,JsonStore.Options)));
    string dataset=Sub("state-dataset");var m=await DatasetBuilder.GenerateAsync(s,dataset);
    Check(m.InitialSizes.Count==0,"rename target not generated ahead of time");
    var result=await new ReplayEngine().RunAsync(s,dataset,new() { Speed=1000 });
    Check(result.Failed==0,"all state transitions succeed");
    Check(!File.Exists(PathPolicy.ResolveInside(dataset,m.Paths[renamed])),"delete applied on close");
});
await Test("failed source operations and unresolved rename never mutate",async()=>
{
    string s=Sub("failed-source");Directory.CreateDirectory(s);JsonStore.Write(Path.Combine(s,"session.json"),new SessionManifest { State="Complete" });
    var e=new[]{new IoEvent { Sequence=1,Path=@"C:\work\a.bin",Operation=IoOperation.Write,Size=4096,NtStatus=0xC0000022,EndUs=1 },new IoEvent { Sequence=2,Path=@"C:\work\a.bin",Operation=IoOperation.Rename,StartUs=2,NtStatus=0,EndUs=3 }};
    File.WriteAllLines(Path.Combine(s,"events.jsonl"),e.Select(x=>JsonSerializer.Serialize(x,JsonStore.Options)));
    string d=Sub("failed-dataset");var m=await DatasetBuilder.GenerateAsync(s,d);
    var result=await new ReplayEngine().RunAsync(s,d,new() { Speed=1000 });
    Check(result.Executed==0&&result.Skipped==2&&result.Failed==0,"skipped and not executed");
    Check(new FileInfo(PathPolicy.ResolveInside(d,m.Paths[e[0].Path])).Length==0,"no write");
});
await Test("replay: a handle used only for attributes is opened for attributes, so an exclusive open beside it works as recorded",async()=>
{
    const string file=@"C:\work\scene.aep";
    var query=Op(2,IoOperation.QueryInfo,file,"Q",150); query.InfoClass=34;
    // A size check on one handle while another opens the file without sharing and reads it, as the record shows succeeding.
    var events=new[] { Open(1,file,"Q",100),query,Open(3,file,"X",200,share:0),Op(4,IoOperation.Read,file,"X",300,4096),
        Op(5,IoOperation.Cleanup,file,"X",400),Op(6,IoOperation.Cleanup,file,"Q",500) };
    string s=Session("attributes-handle",events,[new SizeObservation { Path=file,ObservedUs=1,Size=4096 }]);
    string d=Sub("attributes-handle-data"); await DatasetBuilder.GenerateAsync(s,d);
    var r=await new ReplayEngine().RunAsync(s,d,new() { Speed=1000 });
    Check(r.Failed==0 && r.ReadBytes==4096 && r.TemporaryOpens==0,$"no sharing violation (failed {r.Failed})");
});
await Test("replay: recorded failures that reach the storage the same way are sent again and reported as they turn out",async()=>
{
    const string dir=@"C:\work",file=@"C:\work\a.bin",probe=@"C:\work\probe.tmp",missing=@"C:\work\missing\x.bin",under=@"C:\work\a.bin\x";
    static IoEvent Fail(IoEvent e,uint status) { e.NtStatus=status; e.ExtraInfo=null; if(e.Operation is IoOperation.Read or IoOperation.Write) e.Transferred=0; return e; }
    var folder=Open(1,dir,"D",100); folder.IsDirectory=true;
    var list=Op(2,IoOperation.DirectoryEnumerate,dir,"D",150,65536); list.IsDirectory=true; list.InfoClass=37; list.SearchPattern="*";
    var end=Fail(Op(3,IoOperation.DirectoryEnumerate,dir,"D",160,65536),0x80000006); end.IsDirectory=true; end.InfoClass=37;
    var query=Op(9,IoOperation.QueryInfo,file,"A",450); query.InfoClass=5;
    var streams=Fail(Op(10,IoOperation.QueryInfo,file,"A",460),0xC000000D); streams.InfoClass=22;
    var unopened=Op(16,IoOperation.QueryInfo,file,"Q",800); unopened.InfoClass=4;
    var events=new[] { folder,list,end,Op(4,IoOperation.Cleanup,dir,"D",170),
        Fail(Open(5,probe,"P",200),0xC0000034),Fail(Open(6,missing,"M",250),0xC000003A),Fail(Open(7,under,"U",300),0xC000003A),
        Open(8,file,"A",400),query,streams,Op(11,IoOperation.Read,file,"A",500,4096),Fail(Op(12,IoOperation.Read,file,"A",600,4096,4096),0xC0000011),
        // The fast I/O path refused, then the same write as an IRP; a write refused for lack of access is not sent again.
        Fail(Op(13,IoOperation.Write,file,"A",650,4096),IoEvent.FastIoRefusedStatus),Op(14,IoOperation.Write,file,"A",660,4096),Fail(Op(15,IoOperation.Write,file,"A",700,4096),0xC0000022),
        unopened,Op(17,IoOperation.Cleanup,file,"A",900) };
    string s=Session("failures-replayed",events,[new SizeObservation { Path=file,ObservedUs=1,Size=4096 }]);
    var a=SessionAnalyzer.Analyze(s);
    Check(a.FastIoRefusals==1 && a.Writes==2 && a.FailedOperations==7 && a.Operations["Write"]==2,$"a refused fast I/O is one request, not a failure (writes {a.Writes}, failed {a.FailedOperations})");
    string d=Sub("failures-replayed-data"); var m=await DatasetBuilder.GenerateAsync(s,d);
    Check(m.Paths.ContainsKey(probe) && m.Paths.ContainsKey(missing) && m.Paths.ContainsKey(under) && m.InitialSizes.ContainsKey(m.Paths[file]),"looked-up names get a dummy path; the file under a lookup stays a file");
    var r=await new ReplayEngine().RunAsync(s,d,new() { Speed=1000 });
    // The stream query fails in the record but works on the dummy: reported, not forced.
    Check(r.Failed==0 && r.ReproducedFailures==5 && r.FailuresNotReproduced==1 && r.Skipped==2 && r.Executed==15 && r.TemporaryOpens==1,
        $"sent {r.Executed}, failed again {r.ReproducedFailures}, not again {r.FailuresNotReproduced}, skipped {r.Skipped}, temporary opens {r.TemporaryOpens}, failed {r.Failed}");
    Check(!File.Exists(PathPolicy.ResolveInside(d,m.Paths[probe])) && !Directory.Exists(Path.GetDirectoryName(PathPolicy.ResolveInside(d,m.Paths[missing]))) && new FileInfo(PathPolicy.ResolveInside(d,m.Paths[file])).Length==4096,"lookups create nothing");
    var statuses=JsonStore.Lines<ReplayOutcome>(Path.Combine(s,r.OutcomeFile!)).ToDictionary(o=>o.Sequence,o=>o.Status);
    Check(statuses[13]=="SkippedFastIoRetry" && statuses[15]=="SkippedRecordedFailure" && statuses[3]=="ReproducedFailure" && statuses[10]=="FailureNotReproduced","statuses per request");
});
await Test("nonempty destinations and a missing marker are rejected; a tampered map entry is skipped",async()=>
{
    var d=Sub("occupied");Directory.CreateDirectory(d);File.WriteAllText(Path.Combine(d,"keep.txt"),"original");
    await Throws<IOException>(()=>DatasetBuilder.GenerateAsync(demo,d));
    Check(File.ReadAllText(Path.Combine(d,"keep.txt"))=="original","original preserved");
    await Throws<FileNotFoundException>(()=>new ReplayEngine().RunAsync(demo,d,new()));
    // A map entry pointing outside the dataset: the run goes on, skips that path, and writes nothing outside.
    var clean=Sub("tampered");var m=await DatasetBuilder.GenerateAsync(demo,clean);m.Paths[m.Paths.Keys.First()]=@"..\escape.bin";JsonStore.Write(Path.Combine(clean,DatasetBuilder.Marker),m);
    var r=await new ReplayEngine().RunAsync(demo,clean,new() { Speed=1000 });
    Check(!File.Exists(Path.Combine(testRoot,"escape.bin")),"nothing written outside the dataset");
    Check(r.Executed>0 && JsonStore.Lines<ReplayOutcome>(Path.Combine(demo,r.OutcomeFile!)).Any(o=>o.Status=="SkippedUnsafePath") && r.Notes.Any(n=>n.Contains("SkippedUnsafePath")),"the entry is skipped and reported, the rest runs");
});
await Test("cancellation finalizes results and makes dataset non-reusable",async()=>
{
    string d=Sub("cancelled");await DatasetBuilder.GenerateAsync(demo,d);
    using var c=new CancellationTokenSource();c.Cancel();
    var r=await new ReplayEngine().RunAsync(demo,d,new(),token:c.Token);
    Check(r.Cancelled,"cancel reported");Check(JsonStore.Read<DatasetManifest>(Path.Combine(d,DatasetBuilder.Marker)).State=="Consumed","state closed");
});
await Test("malformed trace produces an explicit error",async()=>
{
    string s=Sub("broken");Directory.CreateDirectory(s);JsonStore.Write(Path.Combine(s,"session.json"),new SessionManifest());File.WriteAllText(Path.Combine(s,"events.jsonl"),"{\"broken\"");
    await Throws<InvalidDataException>(()=>Task.Run(()=>SessionAnalyzer.Analyze(s)));
});

string Session(string name,IEnumerable<IoEvent> events,IEnumerable<SizeObservation>? sizes=null)
{
    string s=Sub(name);Directory.CreateDirectory(s);JsonStore.Write(Path.Combine(s,"session.json"),new SessionManifest { State="Complete" });
    File.WriteAllLines(Path.Combine(s,"events.jsonl"),events.Select(e=>JsonSerializer.Serialize(e,JsonStore.Options)));
    if(sizes is not null) File.WriteAllLines(Path.Combine(s,"sizes.jsonl"),sizes.Select(x=>JsonSerializer.Serialize(x,JsonStore.Options)));
    return s;
}
IoEvent Op(long seq,IoOperation op,string path,string handle,long start,long size=0,long offset=0,int pid=1,int tid=1,uint? flags=null) =>
    new() { Sequence=seq,StartUs=start,EndUs=start+10,ProcessId=pid,ThreadId=tid,Path=path,FileObject=handle,Operation=op,Size=size,Offset=offset,NtStatus=0,
            Transferred=op is IoOperation.Read or IoOperation.Write?size:null,IoFlags=op is IoOperation.Read or IoOperation.Write?flags??0:null };
IoEvent Open(long seq,string path,string handle,long start,int disposition=1,uint? info=1,uint share=7,int tid=1)
{ var e=Op(seq,IoOperation.Open,path,handle,start,tid:tid); e.CreateDisposition=disposition; e.ExtraInfo=info; e.ShareAccess=share; return e; }

await Test("replay clock starts after the external sort",async()=>
{
    const string path=@"C:\work\many.bin"; var events=new List<IoEvent>(); long seq=0;
    // The sort of 60,000 events takes a few hundred ms; the first I/O is due 10 ms into the timeline.
    for(int t=0;t<16;t++) events.Add(Open(++seq,path,"H"+t,10_000,tid:t));
    for(int i=0;i<60_000;i++) events.Add(Op(++seq,IoOperation.Read,path,"H"+(i%16),10_100+i*5L,1,0,tid:i%16));
    events.Reverse();
    string s=Session("clock-start",events),d=Sub("clock-start-data");await DatasetBuilder.GenerateAsync(s,d);
    // Before the fix the first events lagged by about the sort time. The bound follows the measured sort time on this machine,
    // with room for timer and thread-pool start-up noise on a busy machine.
    var sort=Stopwatch.StartNew(); using(var first=EventOrder.Read(s).GetEnumerator()) first.MoveNext(); double sortMs=sort.Elapsed.TotalMilliseconds;
    var result=await new ReplayEngine().RunAsync(s,d,new() { Mode=ReplayMode.Timeline,Workers=16 });
    Check(result.Failed==0,"no failures");
    var outcomes=JsonStore.Lines<ReplayOutcome>(Directory.GetFiles(s,"replay-*.jsonl").Single()).Where(o=>o.Status=="Executed").OrderBy(o=>o.PlannedUs).Take(100).ToArray();
    double lag=outcomes.Max(o=>o.ActualUs-o.PlannedUs)/1000.0,bound=Math.Max(100,Math.Min(sortMs*0.6,200));
    Check(lag<bound,$"first events must not wait for the sort (max lag {lag:N1} ms, bound {bound:N0} ms, sort {sortMs:N0} ms)");
});
await Test("a failed lookup before CREATE_NEW is not generated ahead of time",async()=>
{
    const string path=@"C:\work\render\out.mov";
    var lookup=Open(1,path,"A",100); lookup.NtStatus=0xC0000034; lookup.ExtraInfo=null;
    var events=new[] { lookup,Open(2,path,"B",200,disposition:2,info:2),Op(3,IoOperation.Write,path,"B",300,4096),Op(4,IoOperation.Cleanup,path,"B",400) };
    string s=Session("lookup-then-create",events,[new SizeObservation { Path=path,ObservedUs=500,Size=500L*1024*1024 }]);
    var entry=SessionAnalyzer.Analyze(s).FileManifest.Single(f=>f.Path==path);
    Check(!entry.InitiallyExists && entry.SizeConfidence=="NotFoundBeforeCreate",$"created during trace ({entry.InitiallyExists}, {entry.SizeConfidence})");
    string d=Sub("lookup-then-create-data");var m=await DatasetBuilder.GenerateAsync(s,d);
    Check(m.InitialSizes.Count==0,"no 500 MB dummy");
    var result=await new ReplayEngine().RunAsync(s,d,new() { Speed=1000 });
    Check(result.Failed==0 && new FileInfo(PathPolicy.ResolveInside(d,m.Paths[path])).Length==4096,"CREATE_NEW and write replay");
});
await Test("a directory opened without FILE_DIRECTORY_FILE stays a directory",async()=>
{
    const string dir=@"C:\work\shot",file=@"C:\work\shot\a.exr";
    var events=new[] { Open(1,dir,"D",100),Op(2,IoOperation.Cleanup,dir,"D",150),Open(3,file,"F",200),Op(4,IoOperation.Read,file,"F",300,4096),Op(5,IoOperation.Cleanup,file,"F",400) };
    string s=Session("directory-flag",events);
    Check(SessionAnalyzer.Analyze(s).FileManifest.Single(f=>f.Path==dir).IsDirectory,"parent inferred as directory");
    string d=Sub("directory-flag-data");var m=await DatasetBuilder.GenerateAsync(s,d);
    Check(m.Directories.Contains(m.Paths[dir]) && m.InitialSizes.Count==1,"directory not generated as a file");
    var result=await new ReplayEngine().RunAsync(s,d,new() { Speed=1000 });
    Check(result.Failed==0 && result.ReadBytes==4096,"directory open and file read replay");
});
await Test("paging I/O is reported apart and replayed once",async()=>
{
    const string clip=@"C:\work\clip.mov",mapped=@"C:\work\lib.dll";
    var events=new[]
    {
        Open(1,clip,"A",100,share:1),
        Op(2,IoOperation.Read,clip,"A",1000,65536),
        Op(3,IoOperation.Read,clip,"A",1100,65536,flags:0x43),        // cache fill of the read above
        Op(4,IoOperation.Write,clip,"A",1150,4096,65536),
        Op(5,IoOperation.Write,clip,"A",1200,4096,65536,pid:4,flags:0x43),  // lazy writer; the replay handle denies write sharing
        Op(6,IoOperation.Read,mapped,"M",1300,4096,flags:0x43),       // mapped view, no application read
        Op(7,IoOperation.Cleanup,clip,"A",2000)
    };
    string s=Session("paging-split",events);
    var a=SessionAnalyzer.Analyze(s);
    Check(a.IoFlagsAvailable && a.ReadBytes==65536 && a.WriteBytes==4096,$"application totals ({a.ReadBytes}/{a.WriteBytes})");
    Check(a.PagingReadBytes==65536+4096 && a.PagingWriteBytes==4096 && a.LatencySamples==2,"paging totals kept apart");
    string d=Sub("paging-split-data");await DatasetBuilder.GenerateAsync(s,d);
    var result=await new ReplayEngine().RunAsync(s,d,new() { Speed=1000 });
    Check(result.Failed==0 && result.ReadBytes==65536+4096 && result.WriteBytes==4096,$"replayed once (failed {result.Failed}, read {result.ReadBytes}, write {result.WriteBytes})");
    var statuses=JsonStore.Lines<ReplayOutcome>(Directory.GetFiles(s,"replay-*.jsonl").Single()).ToDictionary(o=>o.Sequence,o=>o.Status);
    Check(statuses[3]=="SkippedPagingCacheFill" && statuses[5]=="SkippedPagingWrite" && statuses[6]=="Executed","skip reasons");
});
await Test("renames wait for the destination and publish exactly once",()=>
{
    var published=new List<IoEvent>(); var renames=new RenameCorrelator(published.Add);
    IoEvent Rename(string path)=>new() { Operation=IoOperation.Rename,Path=path,NtStatus=0 };
    var late=Rename(@"C:\w\a.tmp"); renames.Started(7,late); renames.Completed(late,100);
    Check(published.Count==0,"held until resolved");
    renames.Observe(7,@"C:\w\a.tmp",150); renames.Observe(7,@"C:\w\a.aep",200);
    Check(published.Count==1 && late.DestinationPath==@"C:\w\a.aep","destination from a later name");
    var early=Rename(@"C:\w\b.tmp"); renames.Started(8,early); renames.Observe(8,@"C:\w\b.aep",300); renames.Completed(early,400);
    Check(published.Count==2 && early.DestinationPath==@"C:\w\b.aep","destination seen before completion");
    var lost=Rename(@"C:\w\c.tmp"); renames.Started(9,lost); renames.Completed(lost,500);
    renames.Observe(99,@"C:\w\other",3_000_000);
    Check(published.Count==3 && lost.DestinationPath is null && lost.Note!.Contains("not observed"),"timeout publishes unresolved");
    var stopped=Rename(@"C:\w\d.tmp"); renames.Started(10,stopped); renames.Flush();
    Check(published.Count==4 && published.Distinct().Count()==4 && renames.Count==0,"flush publishes the rest once");
    return Task.CompletedTask;
});
await Test("roots match UNC and raw device paths without crossing boundaries",()=>
{
    var matcher=new RootMatcher([new ResolvedRoot { UserPath=@"Z:\Production",UncPath=@"\\nas\share\Production" },new ResolvedRoot { UserPath=@"L:\Production",DevicePath=@"\Device\HarddiskVolume9\Production" }]);
    Check(matcher.Match(@"\Device\Mup\nas\share\Production\a.exr",@"\\nas\share\Production\a.exr",out var unc) && unc==@"Z:\Production\a.exr","UNC of a mapped drive");
    Check(matcher.Match(@"\Device\HarddiskVolume9\Production\b.exr",@"\Device\HarddiskVolume9\Production\b.exr",out var raw) && raw==@"L:\Production\b.exr","raw device path");
    Check(!matcher.Match(@"\Device\HarddiskVolume9\Production-other\c",@"\Device\HarddiskVolume9\Production-other\c",out _),"device boundary");
    Check(!matcher.Match(@"\Device\Mup\nas\share\Production2\c",@"\\nas\share\Production2\c",out _),"UNC boundary");
    Check(matcher.Match(@"z:\production\x",@"z:\production\x",out var same) && same==@"z:\production\x","user path kept as recorded");
    var options=new RecordingOptions { Mode=CaptureMode.Folder,Roots=[@"L:\Production\"],ResolvedRoots=[new ResolvedRoot { UserPath=@"L:\Production",DevicePath=@"\Device\X\Production" }] };
    options.Validate(Sub("roots-out"));
    Check(options.ResolvedRoots.Single().DevicePath==@"\Device\X\Production","resolution from the unelevated side is kept");
    Check(RootResolver.Resolve(@"C:\Production").DevicePath?.StartsWith(@"\Device\")==true,"local drive resolves to a device path");
    return Task.CompletedTask;
});

BenchmarkConfig SmallBench(int seed=42) => new()
{
    Label="test",Seed=seed.ToString(),Threads=4,TotalTransfer="48MiB",
    FileSets=[new() { Name="frames",Count=30,Size="256KiB",SizeJitter=0.3 },new() { Name="video",Count=2,Size="4MiB" },new() { Name="renders",Count=0,Size="256KiB" }],
    Workloads=
    [
        new() { Name="frames",Weight=40,FileSet="frames",Operation=BenchOperation.Read,Pattern=BenchPattern.WholeFile,BlockSize="64KiB",FileOrder=BenchFileOrder.Sequential },
        new() { Name="seq",Weight=20,FileSet="video",Operation=BenchOperation.Read,Pattern=BenchPattern.Sequential,BlockSize="128KiB",RunLength="1MiB" },
        new() { Name="rand",Weight=20,FileSet="video",Operation=BenchOperation.Read,Pattern=BenchPattern.Random,BlockSize="4KiB",RunLength="256KiB" },
        new() { Name="render",Weight=15,FileSet="renders",Operation=BenchOperation.Write,Pattern=BenchPattern.WholeFile,BlockSize="64KiB" },
        new() { Name="overwrite",Weight=5,FileSet="video",Operation=BenchOperation.Write,Pattern=BenchPattern.Random,BlockSize="4KiB",RunLength="64KiB" }
    ]
};
await Test("bench: sizes accept binary and decimal units",async()=>
{
    Check(BenchmarkPlanner.ParseSize("1GiB")==1L<<30 && BenchmarkPlanner.ParseSize("1GB")==1_000_000_000 && BenchmarkPlanner.ParseSize("512 KiB")==524288 && BenchmarkPlanner.ParseSize("1.5MiB")==1572864,"units");
    await Throws<InvalidDataException>(()=>Task.Run(()=>BenchmarkPlanner.ParseSize("10 MiBs")));
});
await Test("bench: the same configuration and seed give an identical plan",()=>
{
    BenchmarkPlanner.Create(SmallBench(),Sub("bench-a")); BenchmarkPlanner.Create(SmallBench(),Sub("bench-b")); BenchmarkPlanner.Create(SmallBench(43),Sub("bench-c"));
    byte[] Plan(string name)=>File.ReadAllBytes(Path.Combine(Sub(name),"events.jsonl"));
    Check(Plan("bench-a").SequenceEqual(Plan("bench-b")),"same seed, same bytes");
    Check(!Plan("bench-a").SequenceEqual(Plan("bench-c")),"another seed differs");
    string sln=AppContext.BaseDirectory; while(!File.Exists(Path.Combine(sln,"IORecorder.sln"))) sln=Path.GetDirectoryName(sln)!;
    var sample=BenchmarkPlanner.Load(Path.Combine(sln,"samples","benchmark-anime.json")); sample.TotalTransfer="2GiB";
    var plan=BenchmarkPlanner.Create(sample,Sub("bench-sample"));
    Check(sample.Workloads.Count==6 && plan.Operations>0 && plan.RecommendedTiming==ReplayMode.ThinkTime,"commented sample configuration loads and plans");
    return Task.CompletedTask;
});
await Test("bench: weights are byte shares and patterns keep their shape",()=>
{
    var plan=BenchmarkPlanner.Create(SmallBench(),Sub("bench-shape"));
    long total=plan.PlannedReadBytes+plan.PlannedWriteBytes;
    foreach(var w in SmallBench().Workloads) { double share=plan.WorkloadBytes[w.Name]*100.0/total; Check(Math.Abs(share-w.Weight)<5,$"{w.Name} share {share:N1}% vs {w.Weight}%"); }
    var events=JsonStore.Lines<IoEvent>(Path.Combine(Sub("bench-shape"),"events.jsonl")).ToArray();
    Check(events.Select(e=>e.Sequence).SequenceEqual(Enumerable.Range(1,events.Length).Select(i=>(long)i)),"sequence follows issue order");
    foreach(var burst in events.Where(e=>e.Operation is IoOperation.Read or IoOperation.Write).GroupBy(e=>e.FileObject))
    {
        var ops=burst.OrderBy(e=>e.Sequence).ToArray(); string kind=ops[0].Note!;
        if(kind is "bench:seq" or "bench:frames" or "bench:render")
            for(int i=1;i<ops.Length;i++) Check(ops[i].Offset==ops[i-1].Offset+ops[i-1].Size,$"{kind} is contiguous");
        if(kind=="bench:frames") Check(ops[0].Offset==0,"whole file starts at 0");
        if(kind is "bench:rand" or "bench:overwrite") Check(ops.All(e=>e.Offset%4096==0 && e.Size==4096),"random requests are block aligned");
    }
    var creates=events.Where(e=>e.Operation==IoOperation.Open && e.CreateDisposition==2).ToArray();
    Check(creates.Length>0 && creates.Select(e=>e.Path).Distinct().Count()==creates.Length,"render output goes to new, distinct files");
    return Task.CompletedTask;
});
await Test("bench: a plan runs through generate and replay with a per-workload report",async()=>
{
    string s=Sub("bench-run"),d=Sub("bench-run-dataset");
    var plan=BenchmarkPlanner.Create(SmallBench(),s);
    var m=await DatasetBuilder.GenerateAsync(s,d);
    Check(m.InitialSizes.Values.Sum()<=30*256*1024*1.3+2*4*1048576,"only existing files are generated");
    var result=await new ReplayEngine().RunAsync(s,d,new() { Mode=plan.RecommendedTiming,Workers=plan.Config.Threads });
    Check(result.Failed==0 && result.ReadBytes==plan.PlannedReadBytes && result.WriteBytes==plan.PlannedWriteBytes,$"all planned I/O executed (failed {result.Failed}, read {result.ReadBytes}/{plan.PlannedReadBytes}, write {result.WriteBytes}/{plan.PlannedWriteBytes})");
    var rows=BenchmarkReport.Write(s,result);
    Check(rows.Select(r=>r.Name).OrderBy(n=>n).SequenceEqual(SmallBench().Workloads.Select(w=>w.Name).OrderBy(n=>n)) && rows.All(r=>r.P95Ms is not null && r.MiBps>0),"one row per workload");
    Check(File.Exists(Path.Combine(s,Path.GetFileNameWithoutExtension(result.OutcomeFile)+"-bench.json")),"report written");
    var html=File.ReadAllText(BenchmarkReport.ReportPath(s,result)!);
    Check(html.Contains("ベンチマーク結果") && SmallBench().Workloads.All(w=>html.Contains(w.Name)),"HTML report lists every workload");
});

await Test("storage: one root, dated folder names without spaces or invalid characters",()=>
{
    Check(AppPaths.Root==Path.Combine(testRoot,"home") && AppPaths.Recordings.StartsWith(AppPaths.Root) && AppPaths.SettingsFile.StartsWith(AppPaths.Root),"single root");
    var at=new DateTime(2026,10,17,15,30,12);
    Check(AppPaths.FolderName("LucidLink / warm: test?",at)=="20261017-153012_LucidLink-warm-test","sanitized: "+AppPaths.FolderName("LucidLink / warm: test?",at));
    Check(AppPaths.FolderName("",at)=="20261017-153012" && AppPaths.FolderName("AfterFX・Photoshop",at)=="20261017-153012_AfterFX・Photoshop","plain names");
    Directory.CreateDirectory(AppPaths.Recordings);
    string first=AppPaths.NewFolder(AppPaths.Recordings,"same"); Directory.CreateDirectory(first);
    string second=AppPaths.NewFolder(AppPaths.Recordings,"same");
    Check(second!=first && !Directory.Exists(second),"a second run in the same second gets its own folder");
    return Task.CompletedTask;
});

await Test("analysis: saved summary is reused and the heat map needs no extra pass",()=>
{
    string s=Session("cached-analysis",[Open(1,@"C:\w\a.bin","A",100),Op(2,IoOperation.Read,@"C:\w\a.bin","A",200,4096,8192),Op(3,IoOperation.Cleanup,@"C:\w\a.bin","A",300)]);
    var exported=SessionAnalyzer.Export(s);
    Check(exported.HeatMaps is { Count:1 } maps && maps[0].MaxOffset==12288 && maps[0].Cells.Sum()==4096,"heat map from the analysis passes");
    File.SetLastWriteTimeUtc(Path.Combine(s,"analysis.json"),DateTime.UtcNow.AddMinutes(1));
    var json=File.ReadAllText(Path.Combine(s,"analysis.json")).Replace("\"reads\": 1","\"reads\": 99");
    File.WriteAllText(Path.Combine(s,"analysis.json"),json); File.SetLastWriteTimeUtc(Path.Combine(s,"analysis.json"),DateTime.UtcNow.AddMinutes(1));
    Check(SessionAnalyzer.Load(s).Reads==99,"newer analysis.json is used as is");
    File.SetLastWriteTimeUtc(Path.Combine(s,"events.jsonl"),DateTime.UtcNow.AddMinutes(2));
    Check(SessionAnalyzer.Load(s).Reads==1,"a record newer than its summary is analyzed again");
    return Task.CompletedTask;
});

await Test("analysis: latency per second keeps Read and Write apart; an older summary is analyzed again",()=>
{
    const string path=@"C:\w\a.bin";
    var events=new List<IoEvent> { Open(1,path,"A",100) }; long seq=1;
    IoEvent Timed(IoOperation op,long start,long micros,long offset=0,uint flags=0) { var e=Op(++seq,op,path,"A",start,4096,offset,flags:flags); e.EndUs=start+micros; return e; }
    // Second 0: Reads of 1 ms. Second 1: no I/O. Second 2: Reads of 20 ms, one Write of 5 ms and a slow paging read that never counts.
    for(int i=0;i<10;i++) events.Add(Timed(IoOperation.Read,1_000+i*1_000,1_000,i*4096L));
    for(int i=0;i<10;i++) events.Add(Timed(IoOperation.Read,2_000_000+i*1_000,20_000,i*4096L));
    events.Add(Timed(IoOperation.Write,2_500_000,5_000));
    events.Add(Timed(IoOperation.Read,2_600_000,900_000,0,0x2));
    string s=Session("latency-per-second",events);
    var t=SessionAnalyzer.Export(s).Timeline.ToDictionary(x=>x.Second);
    // Percentiles are histogram bucket tops: at most 5% above the measured value.
    bool Near(double? value,double ms)=>value is { } v && v>=ms && v<=ms*1.051;
    Check(Near(t[0].ReadP95Ms,1) && t[0].WriteP95Ms is null,"second 0");
    Check(!t.ContainsKey(1),"a second without I/O has no latency");
    Check(Near(t[2].ReadP50Ms,20) && Near(t[2].ReadP95Ms,20) && Near(t[2].WriteP95Ms,5),"second 2: Read and Write apart, paging excluded");
    Check(File.ReadAllLines(Path.Combine(s,"timeline.csv"))[3].Split(',').Length==15,"timeline.csv has the latency columns");
    string saved=Path.Combine(s,"analysis.json");
    string current=$"\"version\": {AnalysisResult.CurrentVersion}";
    File.WriteAllText(saved,File.ReadAllText(saved).Replace(current,"\"version\": 1")); File.SetLastWriteTimeUtc(saved,DateTime.UtcNow.AddMinutes(1));
    Check(SessionAnalyzer.Load(s).Timeline.Any(x=>x.ReadP95Ms is not null) && File.ReadAllText(saved).Contains(current),"an older summary is analyzed again and saved");
    return Task.CompletedTask;
});

await Test("bench: a seed may be text; blanks and stray symbols mean no seed",()=>
{
    Check(Seeds.Normalize(" `")==null && Seeds.Normalize("  ")==null && Seeds.Normalize(" 42 ")=="42" && Seeds.Normalize("LucidLink試験A")=="LucidLink試験A","normalize");
    Check(Seeds.Value("42")==42 && Seeds.Value("LucidLink試験A")==Seeds.Value("LucidLink試験A") && Seeds.Value("LucidLink試験A")!=Seeds.Value("LucidLink試験B"),"text seeds are stable and distinct");
    Check(Seeds.Tag("42")=="seed42" && Seeds.Tag("試験A")=="seed-試験A","folder tag");
    var text=SmallBench(); text.Seed="LucidLink試験A";
    BenchmarkPlanner.Create(text,Sub("seed-text-a")); BenchmarkPlanner.Create(new BenchmarkConfig { Label=text.Label,Seed="LucidLink試験A",Threads=text.Threads,TotalTransfer=text.TotalTransfer,FileSets=text.FileSets,Workloads=text.Workloads },Sub("seed-text-b"));
    Check(File.ReadAllBytes(Path.Combine(Sub("seed-text-a"),"events.jsonl")).SequenceEqual(File.ReadAllBytes(Path.Combine(Sub("seed-text-b"),"events.jsonl"))),"same text, same I/O list");
    string json=Path.Combine(testRoot,"seed-config.json");
    File.WriteAllText(json,"{ \"seed\": \"試験A\", \"workloads\": [] }"); Check(BenchmarkPlanner.Load(json).Seed=="試験A","text seed in a config file");
    File.WriteAllText(json,"{ \"seed\": 7, \"workloads\": [] }"); Check(BenchmarkPlanner.Load(json).Seed=="7","numeric seed in a config file");
    return Task.CompletedTask;
});

await Test("dummy content can be computed from any offset",()=>
{
    var stream=new byte[3*1048576+5]; var data=new DeterministicData(42,@"B\bench\a.bin");
    for(int at=0;at<stream.Length;at+=1048576) data.Fill(stream.AsSpan(at,Math.Min(1048576,stream.Length-at)));
    foreach(var (offset,length) in new[]{(0L,16),(3L,13),(1048575L,9),(2097150L,70000),(3145723L,10)})
    {
        var part=new byte[length]; DeterministicData.FillAt(42,@"B\bench\a.bin",offset,part);
        Check(part.AsSpan().SequenceEqual(stream.AsSpan((int)offset,length)),$"offset {offset}");
    }
    return Task.CompletedTask;
});
await Test("reset brings a used dataset back to the generated state",async()=>
{
    string s=Sub("reset-plan"),d=Sub("reset-data");
    var plan=BenchmarkPlanner.Create(SmallBench(),s);
    await DatasetBuilder.GenerateAsync(s,d,Seeds.Value(plan.Config.Seed));
    Dictionary<string,string> Snapshot()=>Directory.EnumerateFiles(d,"*",SearchOption.AllDirectories).Where(f=>!f.EndsWith(DatasetBuilder.Marker))
        .ToDictionary(f=>Path.GetRelativePath(d,f),f=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f))),StringComparer.OrdinalIgnoreCase);
    var generated=Snapshot();
    var first=await new ReplayEngine().RunAsync(s,d,new() { Mode=plan.RecommendedTiming,Workers=plan.Config.Threads });
    var used=Snapshot();
    Check(first.Failed==0 && (used.Count!=generated.Count || used.Any(p=>generated.GetValueOrDefault(p.Key)!=p.Value)),"the run changed the dataset");
    await Throws<InvalidOperationException>(()=>new ReplayEngine().RunAsync(s,d,new() { Mode=plan.RecommendedTiming }));
    var summary=await DatasetBuilder.ResetAsync(d);
    var restored=Snapshot();
    Check(restored.Count==generated.Count && generated.All(p=>restored.GetValueOrDefault(p.Key)==p.Value),$"identical to the generated dataset (deleted {summary.DeletedFiles}, rewritten {summary.RewrittenBytes})");
    Check(summary.DeletedFiles>0 && summary.RewrittenBytes>0 && summary.RewrittenBytes<generated.Count*262144L,"only what changed was rewritten");
    var second=await new ReplayEngine().RunAsync(s,d,new() { Mode=plan.RecommendedTiming,Workers=plan.Config.Threads });
    Check(second.Failed==0 && second.ReadBytes==first.ReadBytes && second.WriteBytes==first.WriteBytes,"the same run works again");
    var again=await DatasetBuilder.ResetAsync(d);
    Check(Snapshot().All(p=>generated.GetValueOrDefault(p.Key)==p.Value),"a second reset also restores it");
    // A run cut off by a crash leaves no (or only part of its) results: every write the record could have replayed is rewritten.
    var third=await new ReplayEngine().RunAsync(s,d,new() { Mode=plan.RecommendedTiming,Workers=plan.Config.Threads });
    await LogCompressor.WhenIdleAsync();
    string outcome=Path.Combine(s,third.OutcomeFile!);
    Check(File.Exists(outcome+".gz") && !File.Exists(outcome) && JsonStore.Lines<ReplayOutcome>(outcome).Count()==third.Executed+third.Failed+third.Skipped,"the results are compressed after the run and still read by their name");
    File.Delete(outcome+".gz");
    string marker=Path.Combine(d,DatasetBuilder.Marker); var cut=JsonStore.Read<DatasetManifest>(marker); cut.State="Replaying"; JsonStore.Write(marker,cut);
    await DatasetBuilder.ResetAsync(d);
    var afterCrash=Snapshot();
    Check(afterCrash.Count==generated.Count && generated.All(p=>afterCrash.GetValueOrDefault(p.Key)==p.Value),"a reset after a run cut off by a crash also restores it");
});

await Test("log: segments rotate, are compressed once closed, and read back in order (older records have one file)",async()=>
{
    string dir=Sub("log-segments"); Directory.CreateDirectory(dir);
    string Line(int i)=>JsonSerializer.Serialize(new IoEvent { Sequence=i,StartUs=i,EndUs=i+1,Operation=IoOperation.Read,Path=@"C:\x\a.bin",FileObject="A",Size=4096,Transferred=4096,NtStatus=0,IoFlags=0 },JsonStore.Options);
    using(var writer=new RecordLogWriter(dir,RecordLog.Events,maxBytes:2000)) for(int i=0;i<300;i++) writer.Write(Line(i));
    await LogCompressor.WhenIdleAsync();
    var files=RecordLog.Files(dir,RecordLog.Events);
    Check(files.Count>5 && files.All(f=>f.EndsWith(".jsonl.gz")) && !Directory.EnumerateFiles(dir,"*.tmp").Any() && !Directory.EnumerateFiles(dir,"*.jsonl").Any(),$"closed segments are compressed ({files.Count})");
    Check(RecordLog.Read<IoEvent>(dir,RecordLog.Events).Select(e=>e.Sequence).SequenceEqual(Enumerable.Range(0,300).Select(i=>(long)i)),"read back in order");
    Check(RecordLog.Tail<IoEvent>(dir,RecordLog.Events,50).Select(e=>e.Sequence).SequenceEqual(Enumerable.Range(250,50).Select(i=>(long)i)),"the last events");
    // Plain segments (still being compressed) read the same way, before the compressed ones of later numbers.
    string mixed=Sub("log-mixed"); Directory.CreateDirectory(mixed);
    using(var writer=new RecordLogWriter(mixed,RecordLog.Events,maxBytes:2000,compress:false)) for(int i=0;i<100;i++) writer.Write(Line(i));
    var plain=RecordLog.Files(mixed,RecordLog.Events); LogCompressor.Compress(plain[^1]);
    Check(RecordLog.Read<IoEvent>(mixed,RecordLog.Events).Count()==100 && RecordLog.Files(mixed,RecordLog.Events)[^1].EndsWith(".gz"),"plain and compressed segments together");
    string single=Sub("log-single"); Directory.CreateDirectory(single);
    File.WriteAllLines(Path.Combine(single,"events.jsonl"),Enumerable.Range(0,10).Select(Line));
    Check(RecordLog.Read<IoEvent>(single,RecordLog.Events).Count()==10 && RecordLog.Exists(single,RecordLog.Events),"an older record's single file");
});

await Test("recovery: a record cut off by a crash is finished as Interrupted from what was written",async()=>
{
    string s=Sub("crashed"); Directory.CreateDirectory(s);
    var start=DateTimeOffset.UtcNow.AddMinutes(-5);
    JsonStore.Write(Path.Combine(s,"session.json"),new SessionManifest { State="Recording",StartedUtc=start,RecorderProcessId=int.MaxValue,RecorderStartedUtc=start,CheckpointUtc=start.AddSeconds(30) });
    using(var writer=new RecordLogWriter(s,RecordLog.Events,maxBytes:3000,compress:false))
        for(int i=0;i<40;i++) writer.Write(JsonSerializer.Serialize(Op(i+1,IoOperation.Read,@"C:\work\a.bin","A",1_000_000+i*1000,4096,i*4096L),JsonStore.Options));
    // The power went off while the last segment grew: a cut-off line, then zeros where data never reached the disk.
    string last=RecordLog.Files(s,RecordLog.Events)[^1];
    File.AppendAllText(last,"{\"sequence\":41,\"startUs\":10500");
    using(var f=new FileStream(last,FileMode.Append)) f.Write(new byte[512]);
    File.WriteAllText(Path.Combine(s,"sizes.jsonl"),"{\"path\":\"C:\\\\work\\\\a.bin\",\"observedUs\":1,\"size\":163840}\n{\"path\":\"C:\\\\wo");
    File.WriteAllText(Path.Combine(s,"events-000001.jsonl.gz.tmp"),"partial");
    var a=SessionAnalyzer.Load(s);
    var m=JsonStore.Read<SessionManifest>(Path.Combine(s,"session.json"));
    Check(m.State==RecordRecovery.Interrupted && m.EventsWritten==40 && a.Reads==40 && m.EndedUtc>start && m.Warnings.Any(w=>w.Contains("途中で終わって")),$"finished as Interrupted ({m.State}, {m.EventsWritten} events, {a.Reads} reads)");
    await LogCompressor.WhenIdleAsync();
    Check(!Directory.EnumerateFiles(s,"*.tmp").Any() && RecordLog.Files(s,RecordLog.Events).All(f=>f.EndsWith(".gz")) && SessionAnalyzer.Load(s).Reads==40,"leftovers tidied and the segments compressed");
    // A record whose recorder is still running is left alone.
    string live=Sub("live"); Directory.CreateDirectory(live);
    using var self=System.Diagnostics.Process.GetCurrentProcess();
    JsonStore.Write(Path.Combine(live,"session.json"),new SessionManifest { State="Recording",RecorderProcessId=Environment.ProcessId,RecorderStartedUtc=self.StartTime.ToUniversalTime() });
    Check(!RecordRecovery.Prepare(live) && JsonStore.Read<SessionManifest>(Path.Combine(live,"session.json")).State=="Recording","a record being recorded is not touched");
});

await Test("analysis and replay: another process's I/O on the target's files is kept apart and not replayed",async()=>
{
    const string file=@"C:\work\comp.aep",folder=@"C:\work";
    IoEvent Other(IoEvent e,int pid,string name) { e.ProcessId=pid; e.ProcessName=name; e.MatchesProcess=false; e.MatchesFile=true; return e; }
    IoEvent Target(IoEvent e) { e.ProcessName="AfterFX"; e.MatchesProcess=true; return e; }
    var notify=Target(Op(5,IoOperation.DirectoryNotify,folder,"N",450)); notify.EndUs=null; notify.IsDirectory=true;
    var events=new[] { Target(Open(1,file,"A",100)),Target(Op(2,IoOperation.Read,file,"A",200,4096)),
        Other(Op(3,IoOperation.Read,file,"V",300,8192),77,"MsMpEng"),Other(Op(4,IoOperation.Write,file,"A",400,65536,0,flags:0x2),4,"System"),notify,
        Target(Op(6,IoOperation.Cleanup,file,"A",500)) };
    string s=Session("other-process",events,[new SizeObservation { Path=file,ObservedUs=1,Size=65536 }]);
    var a=SessionAnalyzer.Analyze(s);
    Check(a.Reads==1 && a.ReadBytes==4096 && a.PagingWriteBytes==65536 && a.OtherProcessEvents==2 && a.MissingCompletions==0,
        $"the application's totals only; the OS writing the file out counts as paging (reads {a.Reads}, paging write {a.PagingWriteBytes}, other {a.OtherProcessEvents}, missing {a.MissingCompletions})");
    string d=Sub("other-process-data"); await DatasetBuilder.GenerateAsync(s,d);
    var r=await new ReplayEngine().RunAsync(s,d,new() { Speed=1000 });
    var statuses=JsonStore.Lines<ReplayOutcome>(Path.Combine(s,r.OutcomeFile!)).ToDictionary(o=>o.Sequence,o=>o.Status);
    Check(r.Failed==0 && r.ReadBytes==4096 && statuses[3]=="SkippedOtherProcess" && statuses[4]=="SkippedOtherProcess" && statuses[5]=="SkippedUnsupportedOperation",$"not replayed ({string.Join(", ",statuses.Values)})");
});

await Test("machine: Windows 11 is named by its build, and network filter layers are not counted as adapters",()=>
{
    Check(WindowsName.Of("Windows 10 Pro","26300")=="Windows 11 Pro" && WindowsName.Of("Windows 10 Pro","19045")=="Windows 10 Pro" && WindowsName.Of("Windows Server 2025 Datacenter","26100")=="Windows Server 2025 Datacenter","the product name");
    string[] names=["イーサネット 3","イーサネット 3-WFP Native MAC Layer LightWeight Filter-0000","イーサネット 3-QoS Packet Scheduler-0000","Wi-Fi","Wi-Fi 2"];
    Check(names.Where(n=>!NetworkAdapters.IsFilterLayer(n,names)).SequenceEqual(["イーサネット 3","Wi-Fi","Wi-Fi 2"]),"filter layers on top of an adapter are left out, an adapter named like another one is kept");
    return Task.CompletedTask;
});

await Test("version, indexed test locations, the indexer note and the write-back wait",async()=>
{
    Check(ToolVersion.Describe("0.2.1+8d005b1eda1f5e8a6ff4ff0846556406bc3660d5")=="0.2.1 (8d005b1)" && ToolVersion.Describe("0.2.1")=="0.2.1" && ToolVersion.Describe(null)=="?" && ToolVersion.Text!="?","version text");
    string profile=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    Check(TestLocation.IndexWarning(Path.Combine(profile,"Desktop","IOReplay")) is not null && TestLocation.IndexWarning(Path.Combine(profile,"AppData","Local","Temp","x")) is null && TestLocation.IndexWarning(@"C:\IOReplay") is null,
        "the user's folders warn, AppData and folders outside the profile do not");
    // Without administrator rights the modified list cannot be read: no waiting.
    var clock=System.Diagnostics.Stopwatch.StartNew(); var settled=await WriteBack.SettleAsync(null,CancellationToken.None,TimeSpan.FromSeconds(3));
    Check(clock.Elapsed<TimeSpan.FromSeconds(5),"the write-back wait ends within its limit");
    // The indexer reading the test target during a replay is pointed out on the comparison.
    string session=DemoSession.Create(Sub("indexer-demo")),dataset=Sub("indexer-dataset"),trace=DemoSession.CreateReplayTrace(session,dataset);
    Check(ReplayComparison.Build(session,trace).IndexerNote is null,"no note without the indexer");
    var link=JsonStore.Read<ReplayLink>(Path.Combine(trace,ReplayCapture.LinkFile));
    var indexer=new IoEvent { Sequence=900001,StartUs=5_000_000,EndUs=5_000_100,Operation=IoOperation.Read,ProcessId=6060,ProcessName="SearchProtocolHost",Path=link.Paths.Keys.First(),FileObject="S",Size=4096,Transferred=4096,NtStatus=0,IoFlags=0 };
    File.AppendAllLines(Path.Combine(trace,"events.jsonl"),[JsonSerializer.Serialize(indexer,JsonStore.Options)]); SessionAnalyzer.Export(trace);
    Check(ReplayComparison.Build(session,trace).IndexerNote?.Contains("1 件")==true,"the indexer's I/O on the test target is noted");
});

await Test("analysis: the operation counts keep the OS's paging reads and writes apart from the application's",()=>
{
    const string file=@"C:\work\clip.mov";
    var events=new[] { Open(1,file,"A",100),Op(2,IoOperation.Read,file,"A",200,65536),Op(3,IoOperation.Read,file,"A",300,262144,0,flags:0x43),Op(4,IoOperation.Write,file,"A",400,4096),Op(5,IoOperation.Write,file,"A",500,65536,0,pid:4,flags:0x43),Op(6,IoOperation.Cleanup,file,"A",600) };
    var a=SessionAnalyzer.Analyze(Session("operation-names",events,[new SizeObservation { Path=file,ObservedUs=1,Size=262144 }]));
    Check(a.Operations["Read"]==1 && a.Operations["Read"+SessionAnalyzer.PagingSuffix]==1 && a.Operations["Write"]==1 && a.Operations["Write"+SessionAnalyzer.PagingSuffix]==1,
        string.Join(", ",a.Operations.Select(p=>$"{p.Key}={p.Value}")));
    return Task.CompletedTask;
});

await Test("machine: per-second samples and the environment are collected without administrator rights",()=>
{
    var sampler=new SystemSampler();
    sampler.Sample(0,[Environment.ProcessId]); Thread.Sleep(200);
    var m=sampler.Sample(200_000,[Environment.ProcessId]);
    Check(m.CpuPercent is >= 0 and <= 100 && m.Processes.Single().CpuPercent is >= 0,"CPU use of the machine and of the process");
    string root=Sub("env-root"); Directory.CreateDirectory(root);
    var options=new RecordingOptions { Mode=CaptureMode.Folder,Roots=[root] }; options.Validate(Sub("env-out"));
    var info=EnvironmentInfo.Collect(options,Sub("env-out"));
    Check(!string.IsNullOrEmpty(info.CpuModel) && info.Volumes.Count==2 && info.Volumes.All(v=>v.FileSystem is { Length: > 0 } && v.FreeBytes>0),"CPU model and the drives of the target and of the record");
    var (files,_)=OpenFiles.Take(_=>false,TimeSpan.FromSeconds(5));
    Check(files.Count==0,"the open-file lookup runs without failing");
    return Task.CompletedTask;
});

await Test("dataset: spellings of one folder are one entry; unsafe names are left out, not fatal",async()=>
{
    IoEvent Dir(long seq,string path,long start) { var e=Open(seq,path,"D"+seq,start); e.IsDirectory=true; return e; }
    // ETW reports a folder both with and without a trailing separator; a \\?\ path with ".." cannot be made safe.
    string s=Session("spellings",[Dir(1,@"C:\w\dir",100),Dir(2,@"C:\w\dir\",200),Open(3,@"C:\w\dir\a.bin","A",300),Op(4,IoOperation.Read,@"C:\w\dir\a.bin","A",400,4096),
        Open(5,@"\\?\C:\w\..\odd.bin","B",500),Op(6,IoOperation.Read,@"\\?\C:\w\..\odd.bin","B",600,4096)]);
    string d=Sub("spellings-data");
    var m=await DatasetBuilder.GenerateAsync(s,d);
    Check(m.Directories.Contains(@"C\w\dir") && m.Paths[@"C:\w\dir"]==m.Paths[@"C:\w\dir\"] && m.InitialSizes.ContainsKey(@"C\w\dir\a.bin"),"both spellings are the same folder");
    Check(m.ExcludedPaths==1 && !m.Paths.ContainsKey(@"\\?\C:\w\..\odd.bin") && m.Notes.Any(n=>n.Contains("odd.bin")),"the unsafe name is left out and noted");
    var r=await new ReplayEngine().RunAsync(s,d,new() { Speed=1000 });
    Check(r.Failed==0 && r.ReadBytes==4096 && JsonStore.Lines<ReplayOutcome>(Path.Combine(s,r.OutcomeFile!)).Count(o=>o.Status=="SkippedUnresolvedPath")==2,"the replay runs the rest and skips the left-out path");
});

await Test("dataset: a scope makes only the files that scope replays",async()=>
{
    IoEvent Root(IoEvent e) { e.MatchesRoot=true; return e; }
    string s=Session("scoped",[Root(Open(1,@"L:\Production\a.exr","A",100)),Root(Op(2,IoOperation.Read,@"L:\Production\a.exr","A",200,8192)),
        Open(3,@"C:\Users\x\AppData\prefs.txt","B",300),Op(4,IoOperation.Read,@"C:\Users\x\AppData\prefs.txt","B",400,100)]);
    var all=await DatasetBuilder.GenerateAsync(s,Sub("scoped-all"));
    var rooted=await DatasetBuilder.GenerateAsync(s,Sub("scoped-root"),rootOnly:true);
    Check(all.InitialSizes.Count==2 && rooted.InitialSizes.Keys.SequenceEqual([@"L\Production\a.exr"]) && rooted.Scope=="RootOnly","only the folder's file for a folder-only replay");
    var r=await new ReplayEngine().RunAsync(s,Sub("scoped-root"),new() { Speed=1000,RootOnly=true });
    Check(r.Failed==0 && r.ReadBytes==8192,"the folder-only replay runs on it");
});

await Test("replay and reset skip a path through a link inside the dummy and never touch the link's target",async()=>
{
    string s=Session("linked",[Open(1,@"C:\w\a.bin","A",100),Op(2,IoOperation.Write,@"C:\w\a.bin","A",200,4096),
        Open(3,@"C:\w\sub\b.bin","B",300),Op(4,IoOperation.Write,@"C:\w\sub\b.bin","B",400,4096)],
        [new SizeObservation { Path=@"C:\w\a.bin",Size=8192 },new SizeObservation { Path=@"C:\w\sub\b.bin",Size=8192 }]);
    string d=Sub("linked-data"),outside=Sub("linked-outside");
    await DatasetBuilder.GenerateAsync(s,d);
    // Someone replaces a folder of the dummy with a junction to another folder.
    Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(outside,"b.bin"),"outside"); File.WriteAllText(Path.Combine(outside,"keep.txt"),"outside");
    string sub=Path.Combine(d,@"C\w\sub"); Directory.Delete(sub,true);
    using(var mklink=Process.Start(new ProcessStartInfo("cmd.exe",$"/c mklink /J \"{sub}\" \"{outside}\"") { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true })!) mklink.WaitForExit();
    Check((File.GetAttributes(sub)&FileAttributes.ReparsePoint)!=0,"junction created");
    var r=await new ReplayEngine().RunAsync(s,d,new() { Speed=1000 });
    Check(r.Failed==0 && r.WriteBytes==4096 && JsonStore.Lines<ReplayOutcome>(Path.Combine(s,r.OutcomeFile!)).Count(o=>o.Status=="SkippedUnsafePath")==2,"the path through the link is skipped, the other runs");
    var reset=await DatasetBuilder.ResetAsync(d);
    Check(reset.SkippedPaths==1 && File.ReadAllText(Path.Combine(outside,"b.bin"))=="outside" && File.Exists(Path.Combine(outside,"keep.txt")),"the reset neither writes nor deletes through the link");
});

await Test("replay keeps the recorded pacing to well under a millisecond (ThinkTime and Timeline)",async()=>
{
    // 4 threads: 400 reads 2 ms apart (0.8 s), a 0.2 s pause, then 1,000 reads 0.3 ms apart (0.3 s). Each read lasted 0.1 ms.
    var events=new List<IoEvent>();
    for(int t=0;t<4;t++)
    {
        string path=$@"C:\pace\f{t}.bin";
        events.Add(Open(0,path,"P"+t,1000+t,tid:t));
        IoEvent Read(long start,int i) { var e=Op(0,IoOperation.Read,path,"P"+t,start+t,65536,(i%16)*65536L,tid:t); e.EndUs=e.StartUs+100; return e; }
        for(int i=0;i<400;i++) events.Add(Read(2000+i*2000L,i));
        for(int i=0;i<1000;i++) events.Add(Read(1_000_000+i*300L,i));
    }
    events.Sort((a,b)=>a.StartUs.CompareTo(b.StartUs)); for(int i=0;i<events.Count;i++) events[i].Sequence=i+1;
    string s=Session("pacing",events,Enumerable.Range(0,4).Select(t=>new SizeObservation { Path=$@"C:\pace\f{t}.bin",Size=1048576 }));
    // The recording lasts 1.3 s. With Task.Delay's 15.6 ms tick, ThinkTime took about 6 s and the 0.3 ms gaps were dropped.
    // ThinkTime may end a little early: reads from the cache finish faster than the recorded 0.1 ms.
    // Shared CI machines (CI=true) are checked for the same behavior with room for their scheduling noise; the sub-millisecond
    // limits hold on a dedicated machine.
    bool shared=Environment.GetEnvironmentVariable("CI")=="true";
    double p50Limit=shared?2:0.2,p95Limit=shared?20:1,slack=shared?1.0:0;
    foreach(var (mode,shortest,longest) in new[]{(ReplayMode.ThinkTime,0.9,1.6),(ReplayMode.Timeline,1.25,1.5)})
    {
        string d=Sub("pacing-"+mode); await DatasetBuilder.GenerateAsync(s,d);
        var r=await new ReplayEngine().RunAsync(s,d,new() { Mode=mode,Workers=8 });
        Check(r.Failed==0 && r.ElapsedSeconds>shortest && r.ElapsedSeconds<longest+slack && r.PacingErrorP50Ms<p50Limit && r.PacingErrorP95Ms<p95Limit,
            $"{mode}: {r.ElapsedSeconds:N2} s, issued late by P50 {r.PacingErrorP50Ms:N3} / P95 {r.PacingErrorP95Ms:N3} ms");
    }
});

await Test("analysis: read patterns per file and the chunk-size estimate",()=>
{
    const string sparse=@"C:\w\project.psd",whole=@"C:\w\frame.exr",twice=@"C:\w\twice.bin";
    var events=new List<IoEvent> { Open(1,sparse,"S",100),Open(2,whole,"W",100),Open(3,twice,"T",100) }; long seq=3,time=1000;
    // A 100 MiB file: 64 reads of 4 KiB, 1.5 MiB apart, plus one paging read that does not count.
    for(int i=0;i<64;i++) events.Add(Op(++seq,IoOperation.Read,sparse,"S",time+=10,4096,i*1572864L));
    events.Add(Op(++seq,IoOperation.Read,sparse,"S",time+=10,65536,0,flags:0x2));
    // An 8 MiB file read from start to end in 1 MiB requests; a 2 MiB file read twice in 512 KiB requests.
    for(int i=0;i<8;i++) events.Add(Op(++seq,IoOperation.Read,whole,"W",time+=10,1048576,i*1048576L));
    for(int round=0;round<2;round++) for(int i=0;i<4;i++) events.Add(Op(++seq,IoOperation.Read,twice,"T",time+=10,524288,i*524288L));
    // 8 KiB overwritten in the middle of the 100 MiB file.
    events.Add(Op(++seq,IoOperation.Write,sparse,"S",time+=10,8192,10L<<20));
    string s=Session("patterns",events,[new SizeObservation { Path=sparse,Size=100L<<20 },new SizeObservation { Path=whole,Size=8L<<20 },new SizeObservation { Path=twice,Size=2L<<20 }]);
    var a=SessionAnalyzer.Analyze(s); var p=a.AccessPatterns.ToDictionary(x=>x.Path);
    Check(p[sparse].Shape=="散らばる（疎）" && p[sparse].UniqueReadBytes==64*4096 && p[sparse].ReadSizeP50Bytes==4096 && p[sparse].ReadCoverage<0.01 && p[sparse].UniqueWriteBytes==8192,"scattered small reads of a large file");
    Check(p[whole].Shape=="全体を順に" && p[whole].ReadCoverage==1 && p[whole].ReadSizeP50Bytes==1048576,"a whole file in order");
    Check(Math.Abs(p[twice].RereadFactor!.Value-2)<1e-9,"read twice");
    // Each scattered 4 KiB read pulls its own chunk: 64 × 64 KiB at the smallest size, 64 × 256 KiB at the next.
    Check(p[sparse].FetchBytes[0]==64*65536 && p[sparse].FetchBytes[1]==64*262144 && p[whole].FetchBytes.All(b=>b==8L<<20),"fetched per file");
    var at256=a.ChunkEstimates.Single(c=>c.ChunkBytes==262144);
    Check(at256.ReadChunks==64+32+8 && at256.WriteChunks==1 && at256.WriteUploadBytes==262144 && Math.Abs(at256.ReadAmplification!.Value-(double)at256.ReadFetchBytes/(64*4096+(8L<<20)+(2L<<20)))<1e-9,"totals at 256 KiB");
    return Task.CompletedTask;
});

await Test("analysis: write patterns per file and the upload bounds",()=>
{
    const string render=@"C:\w\render.exr",psd=@"C:\w\layers.psd",log=@"C:\w\app.log";
    long seq=0,time=100;
    var events=new List<IoEvent> { Open(++seq,render,"R",time+=10,disposition:2,info:2),Open(++seq,psd,"P",time+=10),Open(++seq,log,"L",time+=10) };
    // A new 8 MiB file written from start to end in 1 MiB writes.
    for(int i=0;i<8;i++) events.Add(Op(++seq,IoOperation.Write,render,"R",time+=10,1048576,i*1048576L));
    // Four 8 KiB writes over the existing data of a 100 MiB file, 10 MiB apart.
    for(int i=0;i<4;i++) events.Add(Op(++seq,IoOperation.Write,psd,"P",time+=10,8192,(i+1)*10L<<20));
    // An existing 1 MiB log: four 4 KiB appends, then the last 4 KiB written again through another handle.
    for(int i=0;i<4;i++) events.Add(Op(++seq,IoOperation.Write,log,"L",time+=10,4096,(1L<<20)+i*4096L));
    events.Add(Op(++seq,IoOperation.Write,log,"L2",time+=10,4096,(1L<<20)+3*4096L));
    string s=Session("write-patterns",events,[new SizeObservation { Path=psd,Size=100L<<20 },new SizeObservation { Path=log,Size=1L<<20 }]);
    var a=SessionAnalyzer.Analyze(s); var p=a.AccessPatterns.ToDictionary(x=>x.Path);
    Check(p[render].WriteShape=="新規に順に書く" && !p[render].ExistedAtStart && p[render].ExistingOverwriteBytes==0 && p[render].WriteSizeP50Bytes==1048576,"a new file written in order");
    Check(p[psd].WriteShape=="既存の一部を上書き" && p[psd].InitialSizeBytes==100L<<20 && p[psd].ExistingOverwriteBytes==4*8192,"scattered overwrites of existing data");
    Check(p[log].WriteShape=="末尾に追記" && p[log].ExistingOverwriteBytes==0 && p[log].WriteBytes-p[log].UniqueWriteBytes==4096,"appends, one of them written twice");
    var at256=a.ChunkEstimates.Single(c=>c.ChunkBytes==262144);
    // Merged: 32 chunks of the new file, 4 of the PSD, 1 of the log. Only the PSD's chunks hold old data that is written in part.
    Check(at256.WriteChunks==32+4+1 && at256.WritePartialChunks==4,"chunks updated and written in part at 256 KiB");
    // At 4 MiB the new file is uploaded as it grows (1+2+3+4 MiB per chunk) when nothing is merged, but 8 MiB when merged.
    var at4m=a.ChunkEstimates.Single(c=>c.ChunkBytes==4L<<20);
    long each=(20L<<20)+(16L<<20)+(5L<<20)+(4+8+12+16+16)*1024L,merged=(8L<<20)+(16L<<20)+(1L<<20)+16384;
    Check(at4m.WriteEachBytes==each && at4m.WriteUploadBytes==merged,$"upload bounds at 4 MiB: each {at4m.WriteEachBytes:N0} (expected {each:N0}), merged {at4m.WriteUploadBytes:N0} (expected {merged:N0})");
    return Task.CompletedTask;
});

await Test("compare: a replay's recording maps back to the original paths and compares on the same terms",()=>
{
    string session=DemoSession.Create(Sub("compare-demo")),dataset=Sub("compare-dataset");
    string trace=DemoSession.CreateReplayTrace(session,dataset,2.0);
    // The dummy's marker written by the replay is the tool's own; a file the original never touched shows as replay-only.
    var extra=new[]{ new IoEvent { Sequence=900001,StartUs=5_000_000,EndUs=5_000_100,Operation=IoOperation.Write,Path=Path.Combine(dataset,DatasetBuilder.Marker),FileObject="M",Size=512,Transferred=512,NtStatus=0,IoFlags=0 },
                     new IoEvent { Sequence=900002,StartUs=5_100_000,EndUs=5_100_100,Operation=IoOperation.Read,Path=Path.Combine(dataset,@"C\stray.tmp"),FileObject="X",Size=4096,Transferred=4096,NtStatus=0,IoFlags=0 } };
    File.AppendAllLines(Path.Combine(trace,"events.jsonl"),extra.Select(e=>JsonSerializer.Serialize(e,JsonStore.Options)));
    SessionAnalyzer.Export(trace);
    var runs=ReplayCapture.List(session); var c=ReplayComparison.Build(session,trace);
    Check(runs.Count==1 && runs[0].Folder==trace,"the run is listed under the record");
    Check(c.Files.Count(f=>f.Presence=="両方")==18 && c.Files.Single(f=>f.Presence=="再生のみ").Path.EndsWith("stray.tmp") && !c.Files.Any(f=>f.Path.EndsWith(DatasetBuilder.Marker)),"files matched through the path map");
    var psd=c.Files.Single(f=>f.Path.EndsWith("background.psd"));
    Check(psd.OriginalReadBytes==psd.ReplayReadBytes && psd.ReplayP95Ms/psd.OriginalP95Ms is > 1.8 and < 2.2,$"same requests, twice the latency ({psd.OriginalP95Ms:N2} → {psd.ReplayP95Ms:N2} ms)");
    var p95=c.Values.Single(v=>v.Item=="レイテンシ P95");
    Check(p95.Replay/p95.Original is > 1.8 and < 2.2 && c.OriginalTimeline[0].Second==0 && c.ReplayTimeline[0].Second==0,"totals compare and both timelines start at their first I/O");
    Check(c.Operations.Single(o=>o.Operation=="Read").Replay==c.Operations.Single(o=>o.Operation=="Read").Original+1,"operation counts");
    return Task.CompletedTask;
});
await Test("compare: 'replayed requests only' leaves out the test target's own I/O and the dummy's marker",()=>
{
    string session=DemoSession.Create(Sub("compare-scope-demo")),dataset=Sub("compare-scope-dataset");
    string trace=DemoSession.CreateReplayTrace(session,dataset),linkFile=Path.Combine(trace,ReplayCapture.LinkFile);
    var link=JsonStore.Read<ReplayLink>(linkFile);
    string psd=link.Paths.First(p=>p.Value.EndsWith("background.psd")).Key;
    // An antivirus scan on the test target during the replay, and the replay's own marker update.
    var extra=new[]{ new IoEvent { Sequence=900001,StartUs=5_000_000,EndUs=5_000_100,Operation=IoOperation.Read,ProcessId=5555,ProcessName="MsMpEng",Path=psd,FileObject="V",Size=65536,Transferred=65536,NtStatus=0,IoFlags=0 },
                     new IoEvent { Sequence=900002,StartUs=5_100_000,EndUs=5_100_100,Operation=IoOperation.Write,ProcessId=Environment.ProcessId,ProcessName="IORecorder",Path=Path.Combine(dataset,DatasetBuilder.Marker),FileObject="M",Size=512,Transferred=512,NtStatus=0,IoFlags=0 } };
    File.AppendAllLines(Path.Combine(trace,"events.jsonl"),extra.Select(e=>JsonSerializer.Serialize(e,JsonStore.Options)));
    SessionAnalyzer.Export(trace);
    var all=ReplayComparison.Build(session,trace); var replayed=ReplayComparison.Build(session,trace,ComparisonScope.Replayed);
    // The marker is the tool's own file in both scopes.
    Check(all.Replay.ReadBytes==all.Original.ReadBytes+65536 && all.Replay.WriteBytes==all.Original.WriteBytes,"'all' keeps every process");
    Check(replayed.Replay.ReadBytes==replayed.Original.ReadBytes && replayed.Replay.WriteBytes==replayed.Original.WriteBytes && replayed.Replay.Reads==replayed.Original.Reads
        && replayed.Operations.All(o=>o.Original==o.Replay),"only the replayed requests, which match the original here");
    var scan=replayed.Processes.Single(p=>p.Process=="MsMpEng"); var me=replayed.Processes.Single(p=>p.Process=="IORecorder"+ReplayComparison.ReplayProcessSuffix);
    Check(scan.OriginalOperations==0 && scan.ReplayOperations==1 && scan.ReplayReadBytes==65536 && me.ReplayOperations+scan.ReplayOperations==all.Replay.Operations.Values.Sum()
        && replayed.Processes.Single(p=>p.Process=="AfterFX").ReplayOperations==0,"who sent what on each side");
    // The time with I/O is exact: the synthetic replay takes the same span (first start to last end) as the original.
    var span=replayed.Values.Single(v=>v.Item=="I/Oがあった時間");
    double expected=(replayed.Original.LastIoEndUs!.Value-replayed.Original.FirstIoUs!.Value)/1e6;
    Check(Math.Abs(span.Original!.Value-expected)<1e-9 && span.Replay>span.Original && span.Replay<span.Original+1,$"exact span ({span.Original:N3} s → {span.Replay:N3} s)");
    // A link written before the process id was kept: the IO Recorder process with the most I/O.
    link.ProcessId=null; JsonStore.Write(linkFile,link);
    Check(ReplayComparison.Build(session,trace,ComparisonScope.Replayed).ReplayProcessId==Environment.ProcessId,"older links find the replay process");
    return Task.CompletedTask;
});

await Test("replay: a recorded rename onto an existing name replaces it",async()=>
{
    const string config=@"C:\w\cfg.json",temporary=@"C:\w\cfg.json.tmp";
    var rename=Op(7,IoOperation.Rename,temporary,"T",400); rename.DestinationPath=config;
    // Write a temporary file, then swap it in over the existing one (how settings and markers are saved).
    string s=Session("rename-replace",[Open(1,config,"C",100),Op(2,IoOperation.Read,config,"C",150,100),Op(3,IoOperation.Cleanup,config,"C",160),Op(4,IoOperation.Close,config,"C",170),
        Open(5,temporary,"T",200,disposition:2,info:2),Op(6,IoOperation.Write,temporary,"T",300,4096),rename,Op(8,IoOperation.Cleanup,temporary,"T",500),Op(9,IoOperation.Close,temporary,"T",600)],
        [new SizeObservation { Path=config,Size=100 }]);
    string d=Sub("rename-replace-data"); var m=await DatasetBuilder.GenerateAsync(s,d);
    var r=await new ReplayEngine().RunAsync(s,d,new() { Speed=1000 });
    Check(r.Failed==0 && new FileInfo(PathPolicy.ResolveInside(d,m.Paths[config])).Length==4096 && !File.Exists(PathPolicy.ResolveInside(d,m.Paths[temporary])),"the existing file is replaced");
});

await Test("bench: a duration with a rate sets the amount; full speed cannot take a duration",async()=>
{
    Check(BenchmarkPlanner.ParseDuration("1h30m").TotalSeconds==5400 && BenchmarkPlanner.ParseDuration("01:30:00").TotalSeconds==5400 && BenchmarkPlanner.ParseDuration("90s").TotalSeconds==90 && BenchmarkPlanner.ParseDuration("30m").TotalMinutes==30,"duration formats");
    var timed=SmallBench(); timed.TotalTransfer=null; timed.RateMiBps=8; timed.Duration="3s";
    var plan=BenchmarkPlanner.Create(timed,Sub("bench-duration"));
    long total=plan.PlannedReadBytes+plan.PlannedWriteBytes;
    Check(Math.Abs(total-24L*1048576)<1048576 && Math.Abs(plan.PlannedSeconds!.Value-3)<.01 && plan.NewFileBytes>0,$"8 MiB/s × 3 s ≈ 24 MiB (planned {total:N0}, new files {plan.NewFileBytes:N0})");
    Check(BenchmarkPlanner.Describe(timed).StartsWith("3秒 × 8 MiB/s"),"described by time");
    var both=SmallBench(); both.RateMiBps=8; both.Duration="3s";
    await Throws<InvalidDataException>(()=>Task.Run(()=>BenchmarkPlanner.TotalBytes(both)));
    var fullSpeed=SmallBench(); fullSpeed.TotalTransfer=null; fullSpeed.Duration="3s";
    await Throws<InvalidDataException>(()=>Task.Run(()=>BenchmarkPlanner.TotalBytes(fullSpeed)));
    await Throws<InvalidDataException>(()=>Task.Run(()=>BenchmarkPlanner.ParseDuration("30")));
});

await Test("process tracking: children stay recorded after their parent ends; a reused parent PID makes nothing a target",()=>
{
    // Running at the start: the application (10), its helper (11) and the helper's child (12). 30 names 10 as its parent but
    // started before it, so that PID belonged to another process then.
    var running=new ProcessTracker(new RecordingOptions { ProcessNames=["AfterFX"] },[(10,"AfterFX.exe",1,100,null),(11,"helper.exe",10,200,null),(12,"render.exe",11,250,null),(30,"old.exe",10,50,null)]);
    Check(running.Matches(10)&&running.Matches(11)&&running.Matches(12),"the application, its child and grandchild");
    Check(!running.Matches(30),"a process whose parent PID was reused");
    // A launcher (a target by name) starts the main program and ends; the program then starts a renderer.
    var started=new ProcessTracker(new RecordingOptions { ProcessNames=["Launcher"] },[]);
    started.Started(40,"Launcher.exe",1); started.Started(41,"Main.exe",40); started.Ended(40); started.Started(42,"Render.exe",41);
    // The closing of an ended process's files and the unmapping of its memory are reported after its end.
    Check(started.Matches(40) && started.Name(40)=="Launcher" && !started.TargetPids().Contains(40),"an ended process keeps its name and decision, and is not sampled");
    Check(started.Matches(41)&&started.Matches(42),"the program and its renderer after the launcher ended");
    // The ended launcher's PID is reused by an unrelated program: neither it nor its child is a target.
    started.Started(40,"Unrelated.exe",1); started.Started(43,"Child.exe",40);
    Check(!started.Matches(40)&&!started.Matches(43),"a reused PID");
    Check(started.TargetPids().Order().SequenceEqual(new[] { 41,42 }),"targets: "+string.Join(",",started.TargetPids()));
    var alone=new ProcessTracker(new RecordingOptions { ProcessNames=["AfterFX"],IncludeChildren=false },[(10,"AfterFX.exe",1,100,null),(11,"helper.exe",10,200,null)]);
    Check(alone.Matches(10)&&!alone.Matches(11),"without children");
    return Task.CompletedTask;
});

await Test("recording: other processes are followed on the files whose data the target used; a path is expected of file requests only",()=>
{
    Check(EtwRecorder.UsesFileData(IoOperation.Read,0) && EtwRecorder.UsesFileData(IoOperation.Write,0) && EtwRecorder.UsesFileData(IoOperation.SetEndOfFile,0) && EtwRecorder.UsesFileData(IoOperation.Flush,0),"reading, writing, sizing, flushing");
    Check(!EtwRecorder.UsesFileData(IoOperation.Read,0x2) && !EtwRecorder.UsesFileData(IoOperation.Read,0x40) && !EtwRecorder.UsesFileData(IoOperation.Open,0)
        && !EtwRecorder.UsesFileData(IoOperation.DirectoryEnumerate,0) && !EtwRecorder.UsesFileData(IoOperation.QueryInfo,0) && !EtwRecorder.UsesFileData(IoOperation.MapView,0),"loading a library (paging), opening, listing, querying, mapping");
    Check(EtwRecorder.NeedsPath(IoOperation.Read) && EtwRecorder.NeedsPath(IoOperation.Close) && !EtwRecorder.NeedsPath(IoOperation.MapView) && !EtwRecorder.NeedsPath(IoOperation.UnmapView)
        && !EtwRecorder.NeedsPath(IoOperation.HardFault) && !EtwRecorder.NeedsPath(IoOperation.DiskRead),"memory views, page faults and disk I/O are not unresolved paths");
    Check(EtwRecorder.CountsUnresolved(IoOperation.Read,CaptureMode.Process,true) && !EtwRecorder.CountsUnresolved(IoOperation.Read,CaptureMode.Process,false)
        && EtwRecorder.CountsUnresolved(IoOperation.Read,CaptureMode.Folder,false) && EtwRecorder.CountsUnresolved(IoOperation.Read,CaptureMode.Hybrid,false) && !EtwRecorder.CountsUnresolved(IoOperation.MapView,CaptureMode.Process,true),
        "recording processes only, the unknown paths of other processes do not count");
    return Task.CompletedTask;
});

await Test("process tracking: the programs installed with the application are followed whoever starts them; shared folders are not used",()=>
{
    const string folder=@"C:\Program Files\Adobe\Adobe After Effects 2025\Support Files";
    var options=new RecordingOptions { ProcessNames=["AfterFX"],ProgramFolders=[folder] };
    // Running at the start: a renderer of the application started by another program (not a child of the application).
    var tracker=new ProcessTracker(options,[(5,"svchost.exe",1,10,@"C:\Windows\System32\svchost.exe"),(50,"aerender.exe",5,300,folder+@"\aerender.exe"),(60,"other.exe",5,300,@"C:\Program Files\Other\other.exe")]);
    Check(tracker.FollowsFolders && tracker.Matches(50) && !tracker.Matches(60) && !tracker.Matches(5),"a program in the folder, whoever started it");
    tracker.Started(70,"AEGPUSniffer.exe",5,folder+@"\AEGPUSniffer.exe"); tracker.Started(71,"Support Files Helper.exe",5,folder+@" Helper\x.exe");
    Check(tracker.Matches(70) && !tracker.Matches(71),"a program started later in the folder; a folder that only starts with the same name is another folder");
    Check(AppLauncher.ProgramOf("\"C:\\Program Files\\Adobe\\AfterFX.exe\" -re")==@"C:\Program Files\Adobe\AfterFX.exe" && AppLauncher.ProgramOf(@"C:\Program Files\Adobe\AfterFX.exe -re x.aep")==@"C:\Program Files\Adobe\AfterFX.exe"
        && AppLauncher.ProgramOf("cmd.exe /c x")==null && AppLauncher.ProgramOf((string?)null)==null,"the program at the start of a command line");
    Check(AppLauncher.InstallFolder(folder+@"\AfterFX.exe")==folder,"an application's own folder");
    Check(AppLauncher.InstallFolder(Path.Combine(Environment.SystemDirectory,"notepad.exe"))==null && AppLauncher.InstallFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"x.exe"))==null
        && AppLauncher.InstallFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Downloads","x.exe"))==null && AppLauncher.InstallFolder(@"D:\x.exe")==null
        && AppLauncher.InstallFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Downloads","CrystalDiskMark","DiskMark64.exe"))!=null,"Windows, Program Files, Downloads and a drive are shared; a folder of its own is not");
    var running=AppLauncher.Running();
    Check(running.Count>0 && running.All(r=>File.Exists(r.App.Path) && r.Count>=1) && running.Select(r=>r.App.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()==running.Count && running.All(r=>r.Pid!=Environment.ProcessId),"running programs, one entry each, without this one");
    return Task.CompletedTask;
});

await Test("left-out processes: counted per process and drive without names; the summary names those on the record's drives",()=>
{
    var counter=new OtherProcessCounter();
    counter.Add("SearchIndexer",@"Z:\project\a.exr",IoOperation.Read,8L<<20);
    counter.Add("SearchIndexer",@"z:\project\b.exr",IoOperation.Read,8L<<20);
    counter.Add("chrome",@"C:\Users\x\cache\data",IoOperation.Write,4L<<20);
    counter.Add("lucid",@"\\nas\share\x",IoOperation.Write,1);
    counter.Add("System","",IoOperation.Open,0);
    var top=counter.Top();
    Check(counter.Events==5 && top[0].Process=="SearchIndexer" && top[0].Volume=="Z:" && top[0].ReadBytes==16L<<20 && top[0].Operations==2,"per process and drive");
    Check(top.Any(r=>r.Volume==@"\\nas\share") && top.Any(r=>r.Volume==""),"shares and unknown paths");
    var summary=OtherProcessIo.Summary(new SessionManifest { OtherProcessIo=top },new AnalysisResult { Files=[new() { Path=@"Z:\project\shot.aep" }] });
    Check(summary is not null && summary.Contains("SearchIndexer（Z:）") && !summary.Contains("chrome"),"only the record's drives: "+summary);
    Check(PathPolicy.VolumeOf(@"\\?\UNC\nas\share\x")==@"\\nas\share" && PathPolicy.VolumeOf(@"\\?\c:\x")=="C:" && PathPolicy.VolumeOf(@"\Device\HarddiskVolume3\x")==@"\Device\HarddiskVolume3","volume forms");
    return Task.CompletedTask;
});

await Test("locations: a record narrowed to a folder is summarized, generated, replayed and compared for it only",async()=>
{
    var frames=LocationFilter.Of([@"D:\Production\scene_001\frames\"]);
    Check(frames.Folders.Single()==@"D:\Production\scene_001\frames" && frames.Includes(@"D:\Production\scene_001\frames\frame_0001.exr") && frames.Includes(@"d:\production\scene_001\frames")
        && !frames.Includes(@"D:\Production\scene_001\frames2\x") && !frames.Includes(""),"folder matching");
    Check(LocationFilter.Of([@"D:\a",@"D:\a\b",@"d:\A\"]).Folders.Count==1 && new LocationFilter().Includes(""),"nested and repeated folders are one; no folders is everything");
    var own=LocationFilter.Of([@"D:\Production\scene_001\*"]);
    Check(own.Includes(@"D:\Production\scene_001\background.psd") && !own.Includes(@"D:\Production\scene_001\frames\frame_0001.exr") && !own.Includes(@"D:\Production\scene_001")
        && own.Text.Contains("直下のファイルだけ") && LocationFilter.Of([@"D:\a\*",@"D:\a"]).Folders.Single()==@"D:\a","a folder's own files without its folders");
    string session=DemoSession.Create(Sub("locations-demo"));
    LocationFilter.Save(session,frames); Check(LocationFilter.Load(session).Key==frames.Key,"kept in the record");
    var part=SessionAnalyzer.Load(session,null,frames);
    Check(part.Reads==128 && part.ReadBytes==128*32768L && part.Writes==0 && part.Files.All(f=>f.Path.Contains(@"\frames\")) && part.Locations.Count==1,"the folder's summary");
    Check(File.Exists(Path.Combine(session,NarrowedAnalysis.ReportName)) && SessionAnalyzer.Load(session).Reads==200,"its own report; the whole record's summary unchanged");
    string data=Sub("locations-data"); var m=await DatasetBuilder.GenerateAsync(session,data,locations:frames);
    Check(m.InitialSizes.Count==16 && m.Locations.SequenceEqual(frames.Folders),"only the folder's files in the dummy");
    var r=await new ReplayEngine().RunAsync(session,data,new() { Speed=1000 });
    Check(r.Failed==0 && r.ReadBytes==128*32768L && r.WriteBytes==0 && r.Options.Locations.Count==1,"only the folder's I/O replayed");
    DemoSession.CreateReplayTrace(session,Sub("locations-compare"));
    var c=ReplayComparison.Build(session,ReplayCapture.List(session).First().Folder,ComparisonScope.All,frames);
    Check(c.Original.ReadBytes==128*32768L && c.Replay.ReadBytes==128*32768L && c.Files.All(f=>f.Path.Contains(@"\frames\")) && c.Locations.Count==1,"both sides narrowed");
    LocationFilter.Save(session,new()); Check(!File.Exists(Path.Combine(session,LocationFilter.FileName)),"no narrowing leaves no file");
});

await Test("archive: a record goes into one zip without its summaries and comes back; a zip pointing outside is refused",async()=>
{
    string record=DemoSession.Create(Sub("archive-demo"));
    File.WriteAllText(Path.Combine(record,"events.csv"),"made on request");
    var zip=await RecordArchive.ExportAsync(record,Sub("archive-out"));
    Check(Path.GetFileName(zip)=="archive-demo.zip" && !File.Exists(zip+".part"),"named after the record");
    using(var z=System.IO.Compression.ZipFile.OpenRead(zip))
    {
        var names=z.Entries.Select(e=>e.FullName).ToList();
        Check(names.Contains("archive-demo/session.json") && names.Contains("archive-demo/events.jsonl") && names.Contains("archive-demo/sizes.jsonl")
            && !names.Any(n=>n.EndsWith("/analysis.json") || n.EndsWith("/report.html") || n.EndsWith("/events.csv") || n.EndsWith("/timeline.csv")),"what was measured only: "+string.Join(", ",names));
    }
    Check(Path.GetFileName(await RecordArchive.ExportAsync(record,Sub("archive-out")))=="archive-demo (2).zip","a second export does not overwrite");
    var back=RecordArchive.Import(zip,Sub("archive-in"));
    Check(Path.GetFileName(back)=="archive-demo" && SessionAnalyzer.Load(back).Reads==200 && !Directory.Exists(back+".importing"),"opens again with the same numbers");
    Check(Path.GetFileName(RecordArchive.Import(zip,Sub("archive-in")))=="archive-demo (2)","a second import gets its own folder");
    string evil=Path.Combine(Sub("archive-out"),"evil.zip");
    using(var z=System.IO.Compression.ZipFile.Open(evil,System.IO.Compression.ZipArchiveMode.Create)) { z.CreateEntry("rec/session.json"); z.CreateEntry("rec/../../escape.txt"); }
    await Throws<InvalidDataException>(()=>Task.Run(()=>RecordArchive.Import(evil,Sub("archive-evil"))));
    Check(!File.Exists(Path.Combine(testRoot,"escape.txt")) && !Directory.EnumerateFileSystemEntries(Sub("archive-evil")).Any(),"refused without leftovers");
});

await Test("launch: a shortcut gives its program, and the application started for a recording is one of its targets",async()=>
{
    string folder=Sub("launch"); Directory.CreateDirectory(folder);
    string exe=Path.Combine(Environment.SystemDirectory,"cmd.exe"),link=Path.Combine(folder,"コマンド.lnk");
    // Written through the shell's Unicode interface (WScript.Shell turns a Japanese name into "????" on an English Windows).
    AppLauncher.SaveShortcut(link,new() { Path=exe,Arguments="/c exit 0",WorkingDirectory=folder });
    var app=AppLauncher.FromFile(link)!;
    Check(app.Name=="コマンド" && app.Path.Equals(exe,StringComparison.OrdinalIgnoreCase) && app.Arguments=="/c exit 0" && app.ProcessName=="cmd","the shortcut's program, arguments and name");
    var options=new RecordingOptions { Mode=CaptureMode.Folder,Roots=[@"C:\Production"],Launch=app }; options.Validate(Sub("launch-record"));
    Check(options.Mode==CaptureMode.Hybrid && options.ProcessNames.Contains("cmd"),"the application is a target (its folder mode becomes hybrid)");
    await Throws<ArgumentException>(()=>Task.Run(()=>new RecordingOptions { Launch=new() { Name="x",Path=Path.Combine(folder,"missing.exe") } }.Validate(Sub("launch-record"))));
    // A build machine (CI) runs as administrator without a desktop, so there is no Explorer whose rights an application could get.
    try { Check(AppLauncher.Start(app)>0,"started"); }
    catch(Exception ex) when(ex is InvalidOperationException or System.ComponentModel.Win32Exception && Environment.GetEnvironmentVariable("CI")=="true") { Console.WriteLine("  (CI: no desktop to start an application from: "+ex.Message+")"); }
    Check(AppLauncher.StartMenu().All(a=>File.Exists(a.Path) && a.Path.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)),"the Start menu lists programs that exist");
});

int failures=results.Count(r=>!r.Passed);
Console.WriteLine($"\n{results.Count-failures}/{results.Count} passed. Artifacts: {testRoot}");
JsonStore.Write(Path.Combine(testRoot,"verification.json"),results.Select(r=>new { r.Name,r.Passed,r.Error }).ToArray());
return failures==0?0:1;
