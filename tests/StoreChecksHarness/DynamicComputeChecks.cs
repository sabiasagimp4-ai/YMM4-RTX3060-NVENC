using NVEncVideoWriterPlugin;

internal static class DynamicComputeChecks
{
    internal static void Run()
    {
        using var cache = new DynamicComputeCache(16, 4);
        int calls = 0, deletes = 0;
        using var started = new ManualResetEventSlim();
        using var complete = new ManualResetEventSlim();
        using var type = cache.Register<int, byte[]>("test/v1", value => value.ToString(), value =>
        {
            Interlocked.Increment(ref calls);
            if (value == 1) { started.Set(); if (!complete.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); }
            return Enumerable.Repeat((byte)value, 8).ToArray();
        }, value => value.Length, _ => Interlocked.Increment(ref deletes), backgroundThreadSafe: true);
        Check(type.CheckoutCached(1, out _) == ComputeCacheStatus.Missing && calls == 0, "cached-only must never compute");
        Task<ComputeReceipt<byte[]>?> owner = type.ComputeAsync(1);
        Check(started.Wait(TimeSpan.FromSeconds(10)), "owner started");
        Check(type.ComputeIfNeededAndCheckout(1, false, out _) == ComputeCacheStatus.Computing, "no-wait on existing owner");
        var cancelled = new CancellationTokenSource();
        var waiter = type.ComputeAsync(1, cancelled.Token);
        cancelled.Cancel();
        try { waiter.GetAwaiter().GetResult(); throw new Exception("waiter ignored cancellation"); } catch (OperationCanceledException) { }
        var consumers = Enumerable.Range(0, 16).Select(_ => type.ComputeAsync(1)).ToArray();
        complete.Set();
        using var original = owner.GetAwaiter().GetResult()!;
        Task.WaitAll(consumers);
        foreach (var consumer in consumers) { using var borrowed = consumer.Result!; Check(ReferenceEquals(original.Value, borrowed.Value), "shared immutable result"); }
        Check(calls == 1, "one computation for concurrent consumers");
        for (int i = 2; i <= 4; i++)
        { Check(type.ComputeIfNeededAndCheckout(i, false, out var receipt) == ComputeCacheStatus.Ready, "no-wait computes missing value"); receipt!.Dispose(); }
        Check(cache.ResidentBytes <= 16 && original.Value[0] == 1, "eviction retains borrowed values");
        type.Dispose();
        Check(type.CheckoutCached(4, out _) == ComputeCacheStatus.Missing && original.Value[0] == 1, "unregister keeps active borrow");
        original.Dispose(); original.Dispose();
        Check(deletes == 4, "each value deleted once including repeated dispose");

        bool current = true;
        using var stale = cache.Register<int, string>("stale", x => x.ToString(), _ => { current = false; return "stale"; }, _ => 1, _ => { }, isCurrent: _ => current);
        Check(stale.ComputeIfNeededAndCheckout(1, true, out _) == ComputeCacheStatus.Missing && cache.ResidentBytes == 0, "dependencies validated again after compute");
        int retries = 0;
        using var failure = cache.Register<int, string>("retry", x => x.ToString(), _ => ++retries == 1 ? throw new InvalidDataException() : "ok", _ => 1, _ => { });
        try { failure.ComputeIfNeededAndCheckout(0, true, out _); throw new Exception("failure hidden"); } catch (InvalidDataException) { }
        Check(failure.ComputeIfNeededAndCheckout(0, true, out var retried) == ComputeCacheStatus.Ready, "failed compute retry"); retried!.Dispose();
        try { failure.ComputeAsync(2); throw new Exception("unsafe background allowed"); } catch (InvalidOperationException) { }
        int purgeDelete = 0;
        using var purgeStart = new ManualResetEventSlim(); using var purgeDone = new ManualResetEventSlim();
        using var purge = cache.Register<int, string>("purge", x => x.ToString(), _ => { purgeStart.Set(); purgeDone.Wait(); return "old"; }, _ => 1, _ => purgeDelete++, true);
        var inFlight = purge.ComputeAsync(0); Check(purgeStart.Wait(TimeSpan.FromSeconds(10)), "purge started"); cache.Clear(); purgeDone.Set();
        using var old = inFlight.GetAwaiter().GetResult()!;
        Check(purge.CheckoutCached(0, out _) == ComputeCacheStatus.Missing && old.Value == "old", "purged computation cannot repopulate cache");
        old.Dispose(); Check(purgeDelete == 1, "purged result lifetime");

        using var tiny = new DynamicComputeCache(0, 1);
        using var boundedStart = new ManualResetEventSlim(); using var boundedEnd = new ManualResetEventSlim();
        using var bounded = tiny.Register<int, string>("bounded", x => x.ToString(), _ => { boundedStart.Set(); if (!boundedEnd.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); return "ok"; }, _ => 1, _ => { }, true);
        var blocked = bounded.ComputeAsync(1); Check(boundedStart.Wait(TimeSpan.FromSeconds(10)), "bounded job started");
        tiny.Clear();
        Check(bounded.ComputeAsync(2).GetAwaiter().GetResult() is null, "purge cannot bypass active job bound");
        boundedEnd.Set(); using var delivered = blocked.GetAwaiter().GetResult()!;
        Check(delivered.Value == "ok" && tiny.ResidentBytes == 0, "over-budget result still delivered");
        int cleanup = 0;
        using var brokenSize = tiny.Register<int, byte[]>("bad-size", _ => "key", _ => new byte[1], _ => throw new InvalidDataException(), _ => { cleanup++; throw new IOException(); });
        try { brokenSize.ComputeIfNeededAndCheckout(1, true, out _); throw new Exception("size error hidden"); } catch (InvalidDataException) { }
        Check(cleanup == 1, "throwing deleter does not strand failed computation");
        DynamicComputeCache.Computation<int, string>? recursive = null;
        using (recursive = tiny.Register<int, string>("recursive", _ => "key", options => { recursive!.ComputeIfNeededAndCheckout(0, true, out _); return "bad"; }, _ => 1, _ => { }))
            try { recursive.ComputeIfNeededAndCheckout(0, true, out _); throw new Exception("recursive deadlock allowed"); } catch (InvalidOperationException) { }
        Console.WriteLine("Dynamic compute: shared jobs, no-wait semantics, cancellation, borrows, purge, failure/retry and safety declarations passed.");
        Dependencies(); Economics();
    }
    private static void Dependencies()
    {
        CacheDependencySnapshot Snapshot(string token, string context = "cpu", long end = 10) => new("blur", "v1", "settings", context, [new("layer", token, 0, end)]);
        Check(Snapshot("a").Key == Snapshot("a").Key && Snapshot("a").Key != Snapshot("b").Key
            && Snapshot("a").Key != Snapshot("a", "gpu").Key && Snapshot("a").Key != Snapshot("a", end: 11).Key, "time/input/context tokens affect identity");
        var request = new CacheImageRequest(0, 0, 10, 10, 16, 3, true, "linear-rgba", "device1");
        Check(request.IsSatisfiedBy(request with { Left = -1, Right = 20, ChannelMask = 15 }), "larger region and channels satisfy request");
        Check(!request.IsSatisfiedBy(request with { BitDepth = 8 }) && !request.IsSatisfiedBy(request with { Right = 9 })
            && !request.IsSatisfiedBy(request with { PreserveRgbOfZeroAlpha = false }) && !request.IsSatisfiedBy(request with { ContextToken = "device2" }), "incomplete/incompatible results rejected");
    }
    private static void Economics()
    {
        var costs = new FrameCacheEconomics();
        Check(costs.ShouldAdmit("cheap", 1024), "unknown cost admits");
        costs.ObserveRender("cheap", 10); costs.ObserveRender("cheap", 12);
        for (int i = 0; i < 8; i++) costs.ObserveRestore(1024, 100);
        Check(!costs.ShouldAdmit("cheap", 1024), "measured expensive restore rejected");
        Check(costs.ShouldAdmit("cheap", 1024, gpuEnabled: true), "unknown GPU benefit preserves admission");
        for (int i = 0; i < 8; i++) costs.ObserveRestore(1024, 5, gpu: true);
        Check(costs.ShouldAdmit("cheap", 1024, gpuEnabled: true), "fast GPU benefit preserves admission");
        costs.ObserveRender("heavy", 300); costs.ObserveRender("heavy", 300);
        Check(costs.ShouldAdmit("heavy", 1024) && costs.ShouldAdmit("cheap", 4096), "heavy and unknown format bucket admit");
        costs.Clear(); Check(costs.ShouldAdmit("cheap", 1024), "purge costs");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("Compute cache: " + message); }
}
