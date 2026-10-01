using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Channels;

namespace NVEncVideoWriterPlugin;

// Detailed diagnostic tracing is opt-in. Producers never perform file I/O or wait for the writer.
internal static class CacheTrace
{
    [ThreadStatic] private static Span? current;
    private static Session? active;
    private static long nextId;
    internal static bool Enabled => Volatile.Read(ref active) is not null;
    internal static string? OutputPath => Volatile.Read(ref active)?.Path;
    internal static long Dropped => Volatile.Read(ref active)?.Dropped ?? 0;
    internal static long OperationId => current?.OperationId ?? 0;
    internal static long? FrameTimeTicks => current?.FrameTimeTicks;
    internal static string? Usage => current?.Usage;

    internal static Session Start(string path, string scenario, int capacity = 8192)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenario);
        var session = new Session(System.IO.Path.GetFullPath(path), scenario, capacity);
        if (Interlocked.CompareExchange(ref active, session, null) is not null)
        {
            session.CancelUnused();
            throw new InvalidOperationException("A diagnostic trace is already active.");
        }
        session.StartWriter();
        return session;
    }

    internal static async Task StopAsync()
    {
        var session = Interlocked.Exchange(ref active, null);
        if (session is not null) await session.StopAsync().ConfigureAwait(false);
    }

    internal static Span? Measure(string stage, string category = "cpu-wall", string? component = null,
        long? frameTimeTicks = null, string? usage = null, long operation = 0)
    {
        var session = Volatile.Read(ref active);
        if (session is null) return null;
        if (!session.TryBegin()) return null;
        return new Span(session, stage, category, component, frameTimeTicks, usage, operation);
    }

    internal static void Timing(string stage, long start, long end, string category = "cpu-wall", bool nested = true)
    {
        var session = Volatile.Read(ref active);
        if (session is null) return;
        var parent = current;
        session.Write(new Record("span", Interlocked.Increment(ref nextId), nested ? parent?.Id ?? 0 : 0,
            parent?.OperationId ?? 0, stage, category, null, parent?.FrameTimeTicks, parent?.Usage,
            Environment.CurrentManagedThreadId, start, Math.Max(start, end), "ok", null));
    }

    internal static void Mark(string scenario)
    {
        using var marker = Measure("scenario", "marker", scenario);
    }

    internal sealed record Record(string Kind, long Id, long ParentId, long OperationId, string Stage,
        string Category, string? Component, long? FrameTimeTicks, string? Usage, int ThreadId,
        long StartTicks, long EndTicks, string Outcome, string? Detail);

    internal sealed class Span : IDisposable
    {
        private readonly Session session;
        private readonly Span? previous;
        private readonly string stage, category;
        private readonly string? component;
        private readonly long started = Stopwatch.GetTimestamp();
        private readonly int thread = Environment.CurrentManagedThreadId;
        private int ended;
        internal long Id { get; } = Interlocked.Increment(ref nextId);
        internal long OperationId { get; private set; }
        internal long? FrameTimeTicks { get; private set; }
        internal string? Usage { get; private set; }
        internal string Outcome { get; set; } = "ok";
        internal string? Detail { get; set; }
        internal Span(Session session, string stage, string category, string? component,
            long? frameTimeTicks, string? usage, long operation)
        {
            this.session = session; this.stage = stage; this.category = category; this.component = component;
            previous = current?.session == session ? current : null;
            OperationId = operation != 0 ? operation : frameTimeTicks is not null ? Id : previous?.OperationId ?? Id;
            FrameTimeTicks = frameTimeTicks ?? previous?.FrameTimeTicks; Usage = usage ?? previous?.Usage;
            current = this;
        }
        internal void Relate(Span earlier)
        {
            OperationId = earlier.OperationId; FrameTimeTicks = earlier.FrameTimeTicks; Usage = earlier.Usage;
        }
        public void Dispose()
        {
            long end = Stopwatch.GetTimestamp(); // End before logging/locking/serialization.
            if (Interlocked.Exchange(ref ended, 1) != 0) return;
            while (current is { } top && Volatile.Read(ref top.ended) != 0) current = top.previous;
            session.Write(new Record("span", Id, previous?.Id ?? 0, OperationId, stage, category, component,
                FrameTimeTicks, Usage, thread, started, end, Outcome, Detail));
            session.EndSpan();
        }
    }

    internal sealed class Session
    {
        private readonly Channel<Record> queue;
        private readonly string scenario;
        private Task writer = Task.CompletedTask;
        private readonly object counterGate = new();
        private long dropped, accepted, written, openSpans;
        private int stopped;
        private readonly DateTimeOffset utc = DateTimeOffset.UtcNow;
        private readonly long origin = Stopwatch.GetTimestamp();
        internal string Path { get; }
        internal long Dropped => Interlocked.Read(ref dropped);
        internal Session(string path, string scenario, int capacity)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
            Path = path; this.scenario = scenario;
            queue = Channel.CreateBounded<Record>(new BoundedChannelOptions(capacity)
                { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
        }
        internal void StartWriter() => writer = Task.Run(WriteAsync);
        internal void CancelUnused() => queue.Writer.TryComplete();
        internal bool TryBegin()
        {
            lock (counterGate) { if (stopped != 0) return false; openSpans++; return true; }
        }
        internal void EndSpan() { lock (counterGate) openSpans--; }
        internal void Write(Record record)
        {
            lock (counterGate)
            {
                if (queue.Writer.TryWrite(record)) accepted++;
                else Interlocked.Increment(ref dropped);
            }
        }
        internal async Task StopAsync()
        {
            lock (counterGate) { if (stopped == 0) { stopped = 1; queue.Writer.TryComplete(); } }
            await writer.ConfigureAwait(false);
        }
        private async Task WriteAsync()
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                // Never overwrite an earlier trace. A failure is surfaced by StopAsync.
                await using var file = new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, true);
                await using var output = new StreamWriter(file, new System.Text.UTF8Encoding(false));
                await output.WriteLineAsync(JsonSerializer.Serialize(new { Kind = "session", Format = 1, Scenario = scenario,
                    UtcOrigin = utc, StopwatchOrigin = origin, StopwatchFrequency = Stopwatch.Frequency,
                    Stopwatch.IsHighResolution, ProcessId = Environment.ProcessId, Runtime = Environment.Version.ToString(), OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                    LogicalProcessors = Environment.ProcessorCount,
                    Timing = "inclusive CPU wall time; submission and Map waits are not GPU execution duration",
                    Percentiles = "compute from retained records; dropped records make the trace incomplete" }));
                await foreach (var record in queue.Reader.ReadAllAsync())
                {
                    await output.WriteLineAsync(JsonSerializer.Serialize(record));
                    Interlocked.Increment(ref written);
                }
                object summary;
                lock (counterGate) summary = new { Kind = "summary", Accepted = accepted,
                    Written = Interlocked.Read(ref written), Dropped, OpenSpans = openSpans, EndTicks = Stopwatch.GetTimestamp() };
                await output.WriteLineAsync(JsonSerializer.Serialize(summary));
                await output.FlushAsync();
            }
            catch
            {
                queue.Writer.TryComplete(); // Stop accumulating if the destination becomes unavailable.
                throw;
            }
        }
    }
}
