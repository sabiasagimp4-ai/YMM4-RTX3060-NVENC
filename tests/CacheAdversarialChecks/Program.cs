using System.Security.Cryptography;
using NVEncVideoWriterPlugin;

string root = Path.Combine(Path.GetTempPath(), "ymm-cache-adversarial-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    if (args.Length == 0 || args.Contains("disk")) { DiskIdentity(raw: true); DiskIdentity(raw: false); }
    if (args.Length == 0 || args.Contains("dependencies")) DependencyBinding();
    if (args.Length == 0 || args.Contains("publication")) PublicationBarrier();
    if (args.Length == 0 || args.Contains("cycles")) { ComputeCycles(); ComputeAsyncCycles(); ComputeValidDiamond(); }
    if (args.Length == 0 || args.Contains("order")) DrawOrderSafety();
    Console.WriteLine("Adversarial cache checks passed.");
}
finally { Directory.Delete(root, recursive: true); }

void DiskIdentity(bool raw)
{
    string path = Path.Combine(root, raw ? "raw-swap" : "compressed-swap");
    string first = Key(1), second = Key(2);
    byte[] a = new byte[8192], b = new byte[8192];
    if (raw) { RandomNumberGenerator.Fill(a); RandomNumberGenerator.Fill(b); }
    else { Array.Fill(a, (byte)17); Array.Fill(b, (byte)89); }
    using (var cache = new FrameCacheStore(path, 32768, 65536))
    {
        cache.Put(first, a); cache.Put(second, b);
        Wait(() => cache.DiskWrites == 2, "two records persisted");
    }
    string Record(string key) => Directory.EnumerateFiles(path, key + ".ymmframe", SearchOption.AllDirectories).Single();
    // Both records are individually valid, same length, and have intact payload checksums.
    // A storage/restore mistake must never turn A's pixels into a successful lookup of B.
    File.Copy(Record(first), Record(second), overwrite: true);
    using (var cache = new FrameCacheStore(path, 32768, 65536))
    {
        Wait(() => cache.DiskBytes > 0, "restart index");
        Check(!cache.TryGet(second, TimeSpan.FromSeconds(2), out _, out _),
            $"{(raw ? "raw" : "compressed")} record substitution returned the wrong frame");
        Check(cache.TryGet(first, TimeSpan.FromSeconds(2), out var valid, out _) && valid.Span.SequenceEqual(a),
            "unaffected record remains reusable");
    }
    Console.WriteLine($"Disk identity: {(raw ? "raw" : "compressed")} record substitution rejected.");
}

static string Key(int value) => Convert.ToHexStringLower(SHA256.HashData(BitConverter.GetBytes(value)));
static void DependencyBinding()
{
    // Two identical visible effect models, with independently changing hidden state.
    // Red in the foreground and blue in the background differs from the reverse.
    // A multiset of valid tokens loses their attachment to those semantic slots.
    CacheDependencySnapshot State(string token) => new("same-effect-class", "v1", token, "cpu", []);
    var red = State("red"); var blue = State("blue");
    string frontRed = FrameDependencyIdentity.WithDynamicState(Key(0), [red, blue]);
    string frontBlue = FrameDependencyIdentity.WithDynamicState(Key(0), [blue, red]);
    Check(frontRed != frontBlue, "swapping hidden state between provider slots aliases frame identity");
    Check(frontRed == FrameDependencyIdentity.WithDynamicState(Key(0), [State("red"), State("blue")]),
        "equal provider states must reuse without object identity");
    Check(frontRed != FrameDependencyIdentity.WithDynamicState(Key(1), [red, blue]), "model state remains part of identity");
    Console.WriteLine("Dynamic dependencies: slot binding and content-equivalent reuse passed.");
}
void PublicationBarrier()
{
    foreach (long disk in new long[] { 0, 65536 })
    {
        using var cache = new FrameCacheStore(Path.Combine(root, "publication-" + disk), 32768, disk);
        var before = cache.BeginPublication();
        using var finish = new ManualResetEventSlim();
        var producer = Task.Run(() => { finish.Wait(); return cache.PutOwned(Key(1), [1, 2, 3], before); });
        cache.Clear(); finish.Set();
        Check(!producer.GetAwaiter().GetResult() && !cache.TryGet(Key(1), out _), "pre-purge producer published after purge");
        var after = cache.BeginPublication();
        Check(cache.PutOwned(Key(1), [4, 5, 6], after), "post-purge producer must publish");
        Check(cache.TryGet(Key(1), out var borrowed) && borrowed.Span.SequenceEqual(new byte[] { 4, 5, 6 }), "current pixels delivered");
        cache.Clear();
        Check(borrowed.Span.SequenceEqual(new byte[] { 4, 5, 6 }), "purge cannot mutate an existing immutable borrow");
        using var other = new FrameCacheStore(Path.Combine(root, "other-" + disk), 32768, 0);
        Check(!other.PutOwned(Key(1), [1], after), "publication permit must bind store ownership");
        cache.Dispose();
        Check(!cache.PutOwned(Key(1), [1], cache.BeginPublication()), "disposed producer must fail open");
    }
    Console.WriteLine("Publication: RAM/disk purge, held producers, borrowed lifetime, owner mismatch and disposal passed.");
}
static void ComputeCycles()
{
    using var cache = new DynamicComputeCache(1024, 8);
    using var start = new Barrier(2);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    DynamicComputeCache.Computation<int, string>? a = null, b = null;
    int calls = 0;
    a = cache.Register<int, string>("cycle-a", x => x.ToString(), x =>
    {
        if (Interlocked.Increment(ref calls) <= 2)
        {
            Check(start.SignalAndWait(TimeSpan.FromSeconds(5)), "both roots start");
            b!.ComputeIfNeededAndCheckout(x, true, out var receipt, timeout.Token); receipt?.Dispose();
        }
        return "a";
    }, _ => 1, _ => { }, backgroundThreadSafe: true);
    b = cache.Register<int, string>("cycle-b", x => x.ToString(), x =>
    {
        if (Interlocked.Increment(ref calls) <= 2)
        {
            Check(start.SignalAndWait(TimeSpan.FromSeconds(5)), "both roots start");
            a!.ComputeIfNeededAndCheckout(x, true, out var receipt, timeout.Token); receipt?.Dispose();
        }
        return "b";
    }, _ => 1, _ => { }, backgroundThreadSafe: true);
    using (a) using (b)
    {
        var roots = new[] { a.ComputeAsync(0), b.ComputeAsync(0) };
        try { Task.WhenAll(roots).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); } catch (Exception) { }
        Check(roots.All(t => t.IsCompleted), "cyclic wait stranded cache owners");
        Check(roots.Any(t => t.Exception?.Flatten().InnerExceptions.Any(e => e is InvalidOperationException) == true),
            "cross-thread cache cycle was only released by timeout cancellation");
        Check(roots.All(t => t.Exception?.Flatten().InnerExceptions.All(e => e is not OperationCanceledException) == true),
            "cycle detection must not wait for cancellation");
        Check(a.ComputeIfNeededAndCheckout(0, true, out var retry) == ComputeCacheStatus.Ready, "cycle failure must permit retry");
        retry!.Dispose();
    }
    Console.WriteLine("Compute dependencies: independent-root wait cycle fails promptly and permits retry.");
}
static void ComputeAsyncCycles()
{
    using var cache = new DynamicComputeCache(0, 8);
    DynamicComputeCache.Computation<int, string>? a = null, b = null;
    bool recurse = true;
    int deletes = 0;
    a = cache.Register<int, string>("async-a", x => x.ToString(), x =>
    {
        if (recurse) { using var child = b!.ComputeAsync(x).GetAwaiter().GetResult(); }
        return "a";
    }, _ => 1, _ => Interlocked.Increment(ref deletes), backgroundThreadSafe: true);
    b = cache.Register<int, string>("async-b", x => x.ToString(), x =>
    {
        using var child = a!.ComputeAsync(x).GetAwaiter().GetResult();
        return "b";
    }, _ => 1, _ => Interlocked.Increment(ref deletes), backgroundThreadSafe: true);
    using (a) using (b)
    {
        try
        {
            using var unexpected = a.ComputeAsync(0).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            throw new Exception("async descendant cycle accepted");
        }
        catch (InvalidOperationException) { }
        recurse = false;
        using var retry = a.ComputeAsync(0).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        Check(retry?.Value == "a", "async descendant cycle must release job capacity for retry");
    }
    Check(deletes == 1, "cycle failure must not delete uncreated values or leak the retry borrow");
    Console.WriteLine("Compute dependencies: async descendant cycle rejects promptly and releases capacity/ownership.");
}
static void ComputeValidDiamond()
{
    using var cache = new DynamicComputeCache(0, 8);
    using var rootsStarted = new CountdownEvent(2);
    using var finishLeaf = new ManualResetEventSlim();
    int calls = 0, deletes = 0;
    using var leaf = cache.Register<int, string>("diamond-leaf", x => x.ToString(), _ =>
    {
        Interlocked.Increment(ref calls);
        Check(finishLeaf.Wait(TimeSpan.FromSeconds(5)), "valid diamond leaf released");
        return "leaf";
    }, _ => 1, _ => Interlocked.Increment(ref deletes), backgroundThreadSafe: true);
    string Root(int x)
    {
        using var cancellation = new CancellationTokenSource();
        var cancelled = leaf.ComputeAsync(0, cancellation.Token);
        var surviving = leaf.ComputeAsync(0);
        cancellation.Cancel();
        try { using var receipt = cancelled.GetAwaiter().GetResult(); throw new Exception("nested cancellation ignored"); }
        catch (OperationCanceledException) { }
        rootsStarted.Signal();
        using var child = surviving.GetAwaiter().GetResult();
        Check(child?.Value == "leaf", "cancelling one edge must preserve the other edge/consumer");
        return "root-" + x;
    }
    using var roots = cache.Register<int, string>("diamond-root", x => x.ToString(), Root,
        _ => 1, _ => Interlocked.Increment(ref deletes), backgroundThreadSafe: true);
    var tasks = new[] { roots.ComputeAsync(1), roots.ComputeAsync(2) };
    try { Check(rootsStarted.Wait(TimeSpan.FromSeconds(5)), "independent diamond roots started"); }
    finally { finishLeaf.Set(); }
    Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    foreach (var task in tasks) task.Result?.Dispose();
    Check(calls == 1 && deletes == 3 && cache.ResidentBytes == 0,
        "valid diamond must share leaf once, remove cancelled edges, and delete three values exactly once");
    Console.WriteLine("Compute dependencies: valid diamond, repeated edge cancellation, shared completion and final borrow cleanup passed.");
}
static void DrawOrderSafety()
{
    var random = new Random(846371);
    int compared = 0;
    for (int example = 0; example < 500; example++)
    {
        var entries = Enumerable.Range(0, random.Next(1, 25)).Select(i => new FrameDependencyIndex.Entry(
            random.Next(-20, 80), random.Next(1, 21), false, false, "item-" + i, [],
            Layer: random.Next(0, 5), AlwaysOnTop: random.Next(0, 2) == 0)).ToArray();
        var index = new FrameDependencyIndex("global", [], "nested", [], entries);
        bool any = false;
        for (int frame = -25; frame <= 105; frame++)
        {
            // Independent pairwise interval oracle, including exact start/end frames.
            bool ambiguous = entries.SelectMany((a, i) => entries.Skip(i + 1).Select(b => (a, b))).Any(pair =>
                pair.a.Layer == pair.b.Layer && pair.a.AlwaysOnTop == pair.b.AlwaysOnTop
                && pair.a.Frame <= frame && frame < (long)pair.a.Frame + pair.a.Length
                && pair.b.Frame <= frame && frame < (long)pair.b.Frame + pair.b.Length);
            Check(index.For(frame).Cacheable == !ambiguous, $"Order certificate mismatch: seed 846371, example {example}, frame {frame}");
            any |= ambiguous; compared++;
        }
        Check(FrameDependencyIndex.HasPotentialOrderAmbiguity(entries) == any, "Whole-timeline ambiguity oracle mismatch");
    }
    var a = new FrameDependencyIndex.Entry(0, 10, false, false, "a", [], Layer: 1);
    var b = a with { Frame = 5, Hash = "b" };
    var transition = new FrameDependencyIndex.Entry(10, 5, true, false, "transition", [], Layer: 2);
    var after = new FrameDependencyIndex("global", [], "nested", [], [a, b, transition]);
    Check(!after.For(12).Cacheable && after.For(15).Cacheable, "Transition must inherit ambiguity at its before frame only");
    var nested = new FrameDependencyIndex("global", [], "nested", [], [a, transition with { IsWide = true }], nestedUncacheable: true);
    Check(nested.For(2).Cacheable && !nested.For(12).Cacheable, "Uncertified nested rendering must only reject wide frames");
    Console.WriteLine($"Order certificates: {compared:N0} generated interval states, boundary/transition/nested propagation passed.");
}
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Wait(Func<bool> predicate, string message)
{
    var end = DateTime.UtcNow + TimeSpan.FromSeconds(10);
    while (!predicate()) { if (DateTime.UtcNow >= end) throw new TimeoutException(message); Thread.Sleep(5); }
}
