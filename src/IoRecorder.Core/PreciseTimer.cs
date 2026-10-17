using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace IoRecorder.Core;

// Waits for a time on the replay clock with microsecond precision. Task.Delay wakes on the system tick (15.6 ms by
// default), which ThinkTime added to every request. One dedicated thread sleeps on a high-resolution waitable timer until
// about 1 ms before the earliest target, spins the rest and hands each waiter back to the thread pool (about 10 µs late
// in the median on the test VM). Spinning uses at most this one core, however many requests are waiting.
public sealed class PreciseTimer : IDisposable
{
    private const double SpinUs = 1000;
    private readonly Stopwatch _clock;
    private readonly PriorityQueue<TaskCompletionSource,double> _queue = new();
    private readonly List<TaskCompletionSource> _ready = [];
    private readonly object _gate = new();
    private readonly AutoResetEvent _added = new(false);
    private readonly TimerHandle _timer = new();
    private readonly Thread _thread;
    private readonly CancellationTokenRegistration _cancel;
    private volatile bool _stopped;
    private int _version;

    public PreciseTimer(Stopwatch clock, CancellationToken token = default)
    {
        _clock = clock;
        _thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "IO Recorder replay timer" };
        _thread.Start();
        // Waits can be long (an idle stretch of the recording); a cancelled run must not sit in them.
        _cancel = token.Register(Stop);
    }

    private double Now => _clock.Elapsed.TotalMicroseconds;

    public Task WaitUntil(double targetUs)
    {
        if (Now >= targetUs) return Task.CompletedTask;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_stopped) { done.TrySetCanceled(); return done.Task; }
            _queue.Enqueue(done, targetUs); _version++;
        }
        _added.Set();
        return done.Task;
    }

    private void Run()
    {
        while (!_stopped)
        {
            TaskCompletionSource? next; double due; int version;
            lock (_gate) { version = _version; if (!_queue.TryPeek(out next, out due)) next = null; }
            if (next is null) { _added.WaitOne(); continue; }
            double remaining = due - Now;
            if (remaining <= 0)
            {
                // Everything due by now, earliest first; completed outside the lock.
                lock (_gate) { while (_queue.TryPeek(out var item, out var at) && at <= Now) _ready.Add(_queue.Dequeue()); }
                foreach (var ready in _ready) ready.TrySetResult();
                _ready.Clear();
                continue;
            }
            if (remaining > SpinUs * 1.5)
            {
                // Sleep until shortly before the target; a newly added (possibly earlier) target wakes the thread.
                _timer.Set(remaining - SpinUs);
                WaitHandle.WaitAny([_timer, _added]);
                continue;
            }
            // The last stretch is spun; a newly added target is looked at again on the next pass.
            while (Now < due && Volatile.Read(ref _version) == version && !_stopped) Thread.SpinWait(20);
        }
    }

    private void Stop()
    {
        lock (_gate)
        {
            _stopped = true;
            while (_queue.TryDequeue(out var waiting, out _)) waiting.TrySetCanceled();
        }
        _added.Set();
    }

    public void Dispose()
    {
        _cancel.Dispose(); Stop();
        _thread.Join(1000);
        _timer.Dispose(); _added.Dispose();
    }

    private sealed class TimerHandle : WaitHandle
    {
        public TimerHandle()
        {
            // CREATE_WAITABLE_TIMER_HIGH_RESOLUTION (Windows 10 1803 and later); an ordinary timer otherwise.
            var handle = CreateWaitableTimerExW(IntPtr.Zero, null, 0x2, 0x1F0003);
            if (handle == IntPtr.Zero) handle = CreateWaitableTimerExW(IntPtr.Zero, null, 0, 0x1F0003);
            if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            SafeWaitHandle = new SafeWaitHandle(handle, true);
        }
        public void Set(double microseconds)
        {
            long due = -(long)(microseconds * 10); // relative, in 100 ns units
            if (!SetWaitableTimer(SafeWaitHandle, ref due, 0, IntPtr.Zero, IntPtr.Zero, false)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long due, int period, IntPtr routine, IntPtr argument, bool resume);
    }
}
