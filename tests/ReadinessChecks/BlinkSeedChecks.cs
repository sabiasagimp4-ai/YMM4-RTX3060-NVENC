using HarmonyLib;
using NVEncVideoWriterPlugin;

// BlinkSeedAlignment on fakes shaped like the bundled tachie sources (YMM4 4.56.1.0): the one hash of the parts folder
// becomes the stable seed, nothing else changes, and a body that hashes twice is refused.
internal static class BlinkSeedChecks
{
    internal static void Run()
    {
        var harmony = new Harmony("ymm.tests.blink-seeds");
        var assembly = typeof(BlinkSeedChecks).Assembly;
        const string folder = @"C:\tachie\れいむ";
        try
        {
            // FNV-1a over UTF-16 code units of "ab": a constant, so that the seed is the same in every process.
            Check(BlinkSeedAlignment.Seed("ab") == unchecked((int)((((2166136261u ^ 'a') * 16777619u) ^ 'b') * 16777619u)), "The seed is not FNV-1a");
            Check(BlinkSeedAlignment.Seed(null) == 0, "A missing folder must seed 0, as the host does");
            Check(!BlinkSeedAlignment.Stable(assembly, typeof(FakeTachieSource).FullName!, "Update"), "Patched before the cache was installed");
            BlinkSeedAlignment.Use(harmony);
            var source = new FakeTachieSource { Directory = folder };
            Check(source.Update(7) == (folder.GetHashCode(), 7), "The fake does not hash with string.GetHashCode before the patch");
            Check(BlinkSeedAlignment.Stable(assembly, typeof(FakeTachieSource).FullName!, "Update"), "The single-hash body was not patched");
            Check(source.Update(7) == (BlinkSeedAlignment.Seed(folder), 7) && new FakeTachieSource().Update(3) == (0, 3),
                "The patched body did not seed with the stable hash, or changed anything else");
            Check(BlinkSeedAlignment.Stable(assembly, typeof(FakeTachieSource).FullName!, "Update"), "A second request did not report the patch");
            Check(!BlinkSeedAlignment.Stable(assembly, typeof(TwiceHashed).FullName!, "Update"), "A body hashing twice was patched");
            harmony.UnpatchAll(harmony.Id);
            Check(source.Update(7).Seed == folder.GetHashCode(), "Unpatching did not restore the host's hash");
            Check(BlinkSeedAlignment.Stable(assembly, typeof(FakeTachieSource).FullName!, "Update") && source.Update(7).Seed == BlinkSeedAlignment.Seed(folder),
                "A patch someone removed was not applied again");
            Console.WriteLine("Blink seeds: stable FNV-1a seed, one hash replaced, other bodies refused, reapplied after removal.");
        }
        finally
        {
            BlinkSeedAlignment.Uninstall(harmony);
            harmony.UnpatchAll(harmony.Id);
        }
    }

    private sealed class FakeTachieSource
    {
        internal string? Directory;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal (int Seed, int Other) Update(int other)
        {
            int seed = Directory?.GetHashCode() ?? 0;
            return (seed, other);
        }
    }

    private sealed class TwiceHashed
    {
        internal string? A = "a", B = "b";

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal int Update() => (A?.GetHashCode() ?? 0) ^ (B?.GetHashCode() ?? 0);
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException("Blink seeds: " + message); }
}
