using System.Diagnostics;

namespace NVEncVideoWriterPlugin;

public enum ComputeCacheStatus { Missing, Computing, Ready, Failed }

/// <summary>Immutable result borrow. Eviction and class unregistration do not destroy active borrows.</summary>
public sealed class ComputeReceipt<T> : IDisposable
{
    private Action? release;
    private readonly T value;
    internal ComputeReceipt(T value, long computeTicks, long bytes, Action release)
    { this.value = value; ComputeTicks = computeTicks; ApproximateBytes = bytes; this.release = release; }
    public T Value => Volatile.Read(ref release) is null ? throw new ObjectDisposedException(nameof(ComputeReceipt<T>)) : value;
    public long ComputeTicks { get; }
    public long ApproximateBytes { get; }
    public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
}

/// <summary>Process-local arbitrary immutable computations. Keys must include every input, time dependency,
/// hidden state, schema and context identity. Callbacks never execute under the cache lock.
/// Delete callbacks must support any releasing thread or marshal destruction to the resource's owning context.</summary>
public sealed class DynamicComputeCache : IDisposable
{
    public static DynamicComputeCache Shared { get; } = new(128L * 1024 * 1024);
    private readonly object gate = new();
    private readonly Dictionary<string, object> classes = new(StringComparer.Ordinal);
    private readonly Dictionary<(object Class, string Key), Entry> entries = [];
    private readonly LinkedList<Entry> lru = [];
    // Flows through async callbacks/Task.Run; thread identity alone misses A -> B -> A
    // when the computations have independent owners on different worker threads.
    private readonly AsyncLocal<Entry?> executing = new();
    private readonly long budget;
    private readonly int maximumEntries;
    private long bytes;
    private int activeComputations;
    private bool disposed;
    private sealed class Entry(object owner, string key, Action<object> delete)
    {
        internal readonly object Owner = owner;
        internal readonly string Key = key;
        internal readonly Action<object> Delete = delete;
        internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int OwnerThread = Environment.CurrentManagedThreadId;
        internal object? Value;
        internal long Bytes, Ticks;
        internal int References = 1; // computation; cache ownership added only on admission
        internal bool Ready;
        internal LinkedListNode<Entry>? Node;
        internal Dictionary<Entry, int>? Dependencies;
    }
    public DynamicComputeCache(long budgetBytes, int maximumEntries = 256)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(budgetBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEntries, 1);
        budget = budgetBytes; this.maximumEntries = maximumEntries;
    }
    public long ResidentBytes { get { lock (gate) return bytes; } }

    public Computation<TOptions, TValue> Register<TOptions, TValue>(string id,
        Func<TOptions, string> generateKey, Func<TOptions, TValue> compute,
        Func<TValue, long> approximateBytes, Action<TValue> delete,
        bool backgroundThreadSafe = false, Func<TOptions, bool>? isCurrent = null) where TValue : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(generateKey); ArgumentNullException.ThrowIfNull(compute);
        ArgumentNullException.ThrowIfNull(approximateBytes); ArgumentNullException.ThrowIfNull(delete);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (classes.ContainsKey(id)) throw new ArgumentException("Compute class already registered", nameof(id));
            var registration = new Computation<TOptions, TValue>(this, id, generateKey, compute, approximateBytes, delete, backgroundThreadSafe, isCurrent);
            classes.Add(id, registration);
            return registration;
        }
    }

    public sealed class Computation<TOptions, TValue> : IDisposable where TValue : notnull
    {
        private readonly DynamicComputeCache cache;
        private readonly string id;
        private readonly Func<TOptions, string> key;
        private readonly Func<TOptions, TValue> compute;
        private readonly Func<TValue, long> size;
        private readonly Action<TValue> delete;
        private readonly Func<TOptions, bool>? current;
        private readonly bool background;
        internal Computation(DynamicComputeCache cache, string id, Func<TOptions, string> key,
            Func<TOptions, TValue> compute, Func<TValue, long> size, Action<TValue> delete, bool background, Func<TOptions, bool>? current)
        { this.cache = cache; this.id = id; this.key = key; this.compute = compute; this.size = size; this.delete = delete; this.background = background; this.current = current; }
        private string Key(TOptions options)
        {
            string result = key(options);
            if (string.IsNullOrEmpty(result) || result.Length > 4096) throw new ArgumentException("Invalid compute key");
            return result;
        }
        // Cached-only: no compute, I/O, waiting, or scheduling. A stale dependency snapshot is never returned.
        public ComputeCacheStatus CheckoutCached(TOptions options, out ComputeReceipt<TValue>? receipt)
        {
            receipt = null;
            if (current is not null && !current(options)) return ComputeCacheStatus.Missing;
            string inputKey = Key(options);
            Entry? entry;
            lock (cache.gate)
            {
                if (!cache.classes.TryGetValue(id, out var registered) || !ReferenceEquals(registered, this)) return ComputeCacheStatus.Missing;
                if (!cache.entries.TryGetValue((this, inputKey), out entry)) return ComputeCacheStatus.Missing;
                if (!entry.Ready) return ComputeCacheStatus.Computing;
                entry.References++; cache.Touch(entry);
            }
            try
            {
                if (current is not null && !current(options)) return ComputeCacheStatus.Missing;
                receipt = Receipt(entry);
                return ComputeCacheStatus.Ready;
            }
            finally { if (receipt is null) cache.Release(entry); }
        }
        // AE no-wait semantics: absent values compute on the caller; only another owner's computation returns Computing.
        // Cancellation stops this consumer's wait. It never cancels work used by other consumers.
        public ComputeCacheStatus ComputeIfNeededAndCheckout(TOptions options, bool waitForOtherThread,
            out ComputeReceipt<TValue>? receipt, CancellationToken cancellation = default)
        {
            receipt = null;
            cancellation.ThrowIfCancellationRequested();
            if (current is not null && !current(options)) return ComputeCacheStatus.Missing;
            string inputKey = Key(options);
            Entry entry; bool owner; Entry? retired = null, dependencyOwner = null;
            lock (cache.gate)
            {
                ObjectDisposedException.ThrowIf(cache.disposed || !cache.classes.TryGetValue(id, out var registered) || !ReferenceEquals(registered, this), this);
                owner = !cache.entries.TryGetValue((this, inputKey), out entry!);
                if (owner)
                {
                    // Bound in-flight jobs as well as retained values; no unbounded pending dictionary.
                    if (cache.activeComputations >= cache.maximumEntries) return ComputeCacheStatus.Missing;
                    if (cache.entries.Count >= cache.maximumEntries)
                    {
                        if (cache.lru.First is not { } oldest) return ComputeCacheStatus.Missing;
                        retired = oldest.Value; cache.Remove(retired);
                    }
                    entry = new Entry(this, inputKey, value => delete((TValue)value));
                    cache.entries.Add((this, inputKey), entry);
                    cache.activeComputations++;
                }
                else if (!entry.Ready && !waitForOtherThread) return ComputeCacheStatus.Computing;
                else if (!entry.Ready && entry.OwnerThread == Environment.CurrentManagedThreadId)
                    throw new InvalidOperationException("Recursive computation of the same cache key");
                // Waiting would close a cycle: answer as without waiting.
                if (!cache.TryBeginDependency(entry, out dependencyOwner)) return ComputeCacheStatus.Computing;
                entry.References++; // consumer reservation survives completion, eviction, purge and unregistration
                cache.Touch(entry);
            }
            if (retired is not null) cache.Release(retired);
            try
            {
                if (owner)
                {
                    Execute(options, inputKey, entry);
                }
                entry.Completion.Task.WaitAsync(cancellation).GetAwaiter().GetResult();
                if (current is not null && !current(options)) return ComputeCacheStatus.Missing;
                receipt = Receipt(entry);
                return ComputeCacheStatus.Ready;
            }
            finally { cache.EndDependency(dependencyOwner, entry); if (receipt is null) cache.Release(entry); }
        }
        private void Execute(TOptions options, string inputKey, Entry entry)
        {
            lock (cache.gate) entry.OwnerThread = Environment.CurrentManagedThreadId;
            var previous = cache.executing.Value;
            cache.executing.Value = entry;
            long started = Stopwatch.GetTimestamp();
            TValue? result = default;
            bool created = false;
            try
            {
                using var trace = CacheTrace.Measure("compute-cache", usage: id);
                result = compute(options); ArgumentNullException.ThrowIfNull(result); created = true;
                long measuredBytes = size(result);
                ArgumentOutOfRangeException.ThrowIfNegative(measuredBytes);
                bool valid = current is null || current(options);
                var evicted = new List<Entry>();
                lock (cache.gate)
                {
                    entry.Value = result; entry.Bytes = measuredBytes; entry.Ticks = Stopwatch.GetTimestamp() - started; entry.Ready = true;
                    bool stillRegistered = cache.classes.TryGetValue(id, out var registered) && ReferenceEquals(registered, this);
                    if (valid && !cache.disposed && stillRegistered && cache.entries.TryGetValue((this, inputKey), out var active)
                        && ReferenceEquals(active, entry) && measuredBytes <= cache.budget)
                    {
                        while (cache.lru.First is { } oldest && (cache.bytes > cache.budget - measuredBytes || cache.entries.Count > cache.maximumEntries))
                        { cache.Remove(oldest.Value); evicted.Add(oldest.Value); }
                        entry.Node = cache.lru.AddLast(entry); cache.bytes += measuredBytes; entry.References++;
                    }
                    else if (cache.entries.TryGetValue((this, inputKey), out var activeEntry) && ReferenceEquals(activeEntry, entry))
                        cache.entries.Remove((this, inputKey));
                }
                entry.Completion.TrySetResult();
                foreach (var old in evicted) cache.Release(old);
            }
            catch (Exception error)
            {
                if (created && entry.Value is null)
                {
                    try { delete(result!); }
                    catch (Exception cleanupError) { using var trace = CacheTrace.Measure("compute-delete-error", component: cleanupError.GetType().Name); }
                }
                lock (cache.gate)
                    if (cache.entries.TryGetValue((this, inputKey), out var active) && ReferenceEquals(active, entry)) cache.entries.Remove((this, inputKey));
                entry.Completion.TrySetException(error);
            }
            finally
            {
                cache.executing.Value = previous;
                lock (cache.gate) cache.activeComputations--;
                cache.Release(entry); // computation ownership
            }
        }
        public Task<ComputeReceipt<TValue>?> ComputeAsync(TOptions options, CancellationToken cancellation = default)
        {
            if (!background) throw new InvalidOperationException("This class has not declared background thread safety; compute on its owning context");
            cancellation.ThrowIfCancellationRequested();
            if (current is not null && !current(options)) return Task.FromResult<ComputeReceipt<TValue>?>(null);
            string inputKey = Key(options);
            Entry entry; bool owner; Entry? retired = null, dependencyOwner = null;
            lock (cache.gate)
            {
                ObjectDisposedException.ThrowIf(cache.disposed || !cache.classes.TryGetValue(id, out var registered) || !ReferenceEquals(registered, this), this);
                owner = !cache.entries.TryGetValue((this, inputKey), out entry!);
                if (owner)
                {
                    if (cache.activeComputations >= cache.maximumEntries) return Task.FromResult<ComputeReceipt<TValue>?>(null);
                    if (cache.entries.Count >= cache.maximumEntries)
                    {
                        if (cache.lru.First is not { } oldest) return Task.FromResult<ComputeReceipt<TValue>?>(null);
                        retired = oldest.Value; cache.Remove(retired);
                    }
                    entry = new Entry(this, inputKey, value => delete((TValue)value)) { OwnerThread = 0 };
                    cache.entries.Add((this, inputKey), entry);
                    cache.activeComputations++;
                }
                else if (!entry.Ready && entry.OwnerThread == Environment.CurrentManagedThreadId)
                    throw new InvalidOperationException("Recursive computation of the same cache key");
                // Waiting would close a cycle: no receipt, as for a value that cannot be computed now.
                if (!cache.TryBeginDependency(entry, out dependencyOwner)) return Task.FromResult<ComputeReceipt<TValue>?>(null);
                entry.References++;
                cache.Touch(entry);
            }
            if (retired is not null) cache.Release(retired);
            if (owner) _ = Task.Run(() => Execute(options, inputKey, entry));
            return AwaitReceipt(options, entry, dependencyOwner, cancellation);
        }
        private async Task<ComputeReceipt<TValue>?> AwaitReceipt(TOptions options, Entry entry, Entry? dependencyOwner, CancellationToken cancellation)
        {
            ComputeReceipt<TValue>? receipt = null;
            try
            {
                // Subscribers await one completion task; they never occupy a blocked worker thread.
                await entry.Completion.Task.WaitAsync(cancellation).ConfigureAwait(false);
                if (current is not null && !current(options)) return null;
                receipt = Receipt(entry);
                return receipt;
            }
            finally { cache.EndDependency(dependencyOwner, entry); if (receipt is null) cache.Release(entry); }
        }
        private ComputeReceipt<TValue> Receipt(Entry entry) => new((TValue)entry.Value!, entry.Ticks, entry.Bytes, () => cache.Release(entry));
        public void Dispose() => cache.Unregister(id, this);
    }
    // Under gate, before reserving a consumer reference (a refused wait must not strand a reservation or a job slot).
    // Cached/no-wait hits have no dependency edge; multiple consumers of an edge are counted separately.
    // An edge is recorded when the computation asks, which is not always when it waits: a request it never awaits (or
    // one made from work it started and left running) also counts. So a refused wait is not an error: the caller gets
    // what it gets for a value that is not ready (Computing, or no receipt), and nothing blocks.
    private bool TryBeginDependency(Entry target, out Entry? owner)
    {
        owner = null;
        var caller = executing.Value;
        if (caller is null || caller.Ready || caller.Completion.Task.IsCompleted || target.Ready) return true;
        var pending = new Stack<Entry>();
        var visited = new HashSet<Entry>();
        pending.Push(target);
        while (pending.TryPop(out var next))
        {
            if (ReferenceEquals(next, caller))
            {
                using var trace = CacheTrace.Measure("compute-cache-cycle");
                return false;
            }
            if (!visited.Add(next) || next.Completion.Task.IsCompleted) continue;
            if (next.Dependencies is { } dependencies)
                foreach (var dependency in dependencies.Keys) pending.Push(dependency);
        }
        var edges = caller.Dependencies ??= [];
        edges[target] = edges.GetValueOrDefault(target) + 1;
        owner = caller;
        return true;
    }
    private void EndDependency(Entry? caller, Entry target)
    {
        if (caller is null) return;
        lock (gate)
        {
            var edges = caller.Dependencies!;
            if (--edges[target] == 0) edges.Remove(target);
            if (edges.Count == 0) caller.Dependencies = null;
        }
    }
    private void Touch(Entry entry)
    { if (entry.Node is { } node) { lru.Remove(node); lru.AddLast(node); } }
    private void Remove(Entry entry)
    {
        entries.Remove((entry.Owner, entry.Key));
        if (entry.Node is { } node) { lru.Remove(node); entry.Node = null; bytes -= entry.Bytes; }
    }
    private void Release(Entry entry)
    {
        object? value = null;
        lock (gate) if (--entry.References == 0) { value = entry.Value; entry.Value = null; }
        if (value is not null)
        {
            // A throwing plugin disposer must not strand other entries or completed waiters.
            try { entry.Delete(value); }
            catch (Exception error) { using var trace = CacheTrace.Measure("compute-delete-error", component: error.GetType().Name); }
        }
    }
    private void Unregister(string id, object registration)
    {
        var removed = new List<Entry>();
        lock (gate)
        {
            if (!classes.TryGetValue(id, out var active) || !ReferenceEquals(active, registration)) return;
            classes.Remove(id);
            foreach (var entry in entries.Values.Where(e => ReferenceEquals(e.Owner, registration)).ToArray())
            { if (entry.Node is not null) removed.Add(entry); Remove(entry); }
        }
        foreach (var entry in removed) Release(entry);
    }
    public void Clear()
    {
        Entry[] removed;
        lock (gate) { removed = lru.ToArray(); entries.Clear(); lru.Clear(); bytes = 0; foreach (var entry in removed) entry.Node = null; }
        foreach (var entry in removed) Release(entry);
    }
    public void Dispose()
    { lock (gate) { disposed = true; classes.Clear(); } Clear(); }
}
