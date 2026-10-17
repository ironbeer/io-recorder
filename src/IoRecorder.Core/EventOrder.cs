using System.Text.Json;

namespace IoRecorder.Core;

// Completed ETW operations arrive out of issue order. External merge sort bounds replay RAM.
public static class EventOrder
{
    public static IEnumerable<IoEvent> Read(string session,CancellationToken token=default)
    {
        const int chunkSize=25_000;
        string scratch=Path.Combine(AppPaths.Temp,"IORecorder-sort-"+Guid.NewGuid().ToString("N"));
        var chunks=new List<string>(); var buffer=new List<IoEvent>(chunkSize);
        var readers=new List<StreamReader>();
        void Flush()
        {
            Directory.CreateDirectory(scratch); buffer.Sort(Compare);
            var file=Path.Combine(scratch,chunks.Count+".jsonl");
            using(var w=new StreamWriter(file)) foreach(var e in buffer) w.WriteLine(JsonSerializer.Serialize(e,JsonStore.Options));
            chunks.Add(file); buffer.Clear();
        }
        try
        {
            foreach(var e in RecordLog.Read<IoEvent>(session, RecordLog.Events))
            { token.ThrowIfCancellationRequested();buffer.Add(e); if(buffer.Count==chunkSize) Flush(); }
            if(chunks.Count==0)
            { buffer.Sort(Compare); foreach(var e in buffer) yield return e; yield break; }
            if(buffer.Count>0) Flush();
            var queue=new PriorityQueue<(IoEvent Event,int Reader),(long Time,long Sequence)>();
            for(int i=0;i<chunks.Count;i++)
            {
                var r=new StreamReader(chunks[i]); readers.Add(r);
                if(r.ReadLine() is { } line) { var e=JsonSerializer.Deserialize<IoEvent>(line,JsonStore.Options)!; queue.Enqueue((e,i),(e.StartUs,e.Sequence)); }
            }
            while(queue.TryDequeue(out var next,out _))
            {
                token.ThrowIfCancellationRequested();
                yield return next.Event;
                if(readers[next.Reader].ReadLine() is { } line)
                { var e=JsonSerializer.Deserialize<IoEvent>(line,JsonStore.Options)!; queue.Enqueue((e,next.Reader),(e.StartUs,e.Sequence)); }
            }
        }
        finally
        {
            foreach(var r in readers) r.Dispose();
            if(Directory.Exists(scratch) && PathPolicy.IsUnder(scratch,AppPaths.Temp) && Path.GetFileName(scratch).StartsWith("IORecorder-sort-",StringComparison.Ordinal))
                Directory.Delete(scratch,true);
        }
    }
    private static int Compare(IoEvent a,IoEvent b) { int c=a.StartUs.CompareTo(b.StartUs); return c!=0?c:a.Sequence.CompareTo(b.Sequence); }
}
