using System.Runtime.CompilerServices;
using HarmonyLib;
using NVEncVideoWriterPlugin;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Player.Video.Items;
using YukkuriMovieMaker.Project.Items;
using Processor = YukkuriMovieMaker.Player.Video.Effects.RandomMoveEffect;
using Model = YukkuriMovieMaker.Project.Effects.RandomMoveEffect;

// RandomSeedAlignment on fakes with the host's names and the shapes of YMM4 4.52.0.1 (RandomEffectBase<T> seeded by the
// processor, text sources seeded by themselves): two renderers of the same model draw the same values once installed,
// the values are those YMM4 4.52.0.2 draws (seeded by the model), and uninstalling restores the host's own.
internal static class RandomSeedChecks
{
    private const string Owner = "ymm.tests.random-seeds";

    internal static void Run()
    {
        var harmony = new Harmony(Owner);
        var host = typeof(RandomSeedChecks).Assembly;
        var model = new Model();
        var item = new TextItem();
        var voice = new VoiceItem();
        var description = new EffectDescription { ItemPosition = new(5) };
        try
        {
            Check(!RandomSeedAlignment.EffectsByModel(host), "Processor-seeded effects were taken as seeded by their effects before the alignment");
            Check(!Agree(model, description), "Two processors of one effect drew the same values without the alignment (the fake does not seed by the processor)");
            Check(new TextSource(item).Seed("abc") != new TextSource(item).Seed("abc"), "Two text sources drew the same seed without the alignment");

            RandomSeedAlignment.TryInstall(host, harmony);
            Check(RandomSeedAlignment.EffectsByModel(host) && RandomSeedAlignment.TextOrderByItem,
                "Alignment not installed: " + string.Join("; ", RandomSeedAlignment.Coverage));
            Check(RandomSeedAlignment.Coverage.Any(line => line.Contains("in 2 processor methods", StringComparison.Ordinal))
                && RandomSeedAlignment.Coverage.Any(line => line.Contains("at 3 places", StringComparison.Ordinal)),
                "Unexpected alignment coverage: " + string.Join("; ", RandomSeedAlignment.Coverage));
            foreach (double span in new[] { 0.0, 0.13 })
            {
                model.Span.Value = span;
                Check(Agree(model, description), $"Two processors of one effect drew other values with the alignment (span {span})");
                // The values of YMM4 4.52.0.2: seeded by the effect.
                double expected = Expected(model, description, 0) + 2 * Expected(model, description, 1);
                Check(new Processor(model).Update(description) == expected, $"The aligned value is not GetRandomValue seeded by the effect (span {span})");
                Check(new Processor(model).Later()(description) == Expected(model, description, 2), "A lambda's GetRandomValue was not aligned");
            }
            int seed = RuntimeHelpers.GetHashCode(item) + 3;
            Check(new TextSource(item).Seed("abc") == seed && new TextSource(item).LaterSeed()("abc") == seed + 1,
                "Text sources of one item did not seed with the item");
            Check(new JimakuSource(voice).Seed("abc") == RuntimeHelpers.GetHashCode(voice) + 3, "Subtitle sources of one voice item did not seed with it");
            Check(new TextSource(new TextItem()).Seed("abc") != seed, "Another item drew the same seed");

            RandomSeedAlignment.Uninstall(harmony);
            model.Span.Value = 0;
            Check(!RandomSeedAlignment.EffectsByModel(host) && !RandomSeedAlignment.TextOrderByItem, "Uninstall kept the alignment state");
            Check(!Agree(model, description) && new TextSource(item).Seed("abc") != new TextSource(item).Seed("abc"), "Uninstall kept the aligned seeds");
            Check(Harmony.GetAllPatchedMethods().All(method => Harmony.GetPatchInfo(method)?.Owners.Contains(Owner) != true), "Uninstall left patches");
            Console.WriteLine("Random seed alignment: renderer-seeded effects and text order seeded by their models, values as YMM4 4.52.0.2, uninstall OK");
        }
        finally
        {
            RandomSeedAlignment.Uninstall(harmony);
            harmony.UnpatchAll(Owner);
        }
    }

    private static bool Agree(Model model, EffectDescription description) =>
        new Processor(model).Update(description) == new Processor(model).Update(description);

    private static double Expected(Model model, EffectDescription description, int id)
    {
        int frame = description.ItemPosition.Frame, length = description.ItemDuration.Frame;
        double span = model.Span.GetValue(frame, length, description.FPS);
        return span != 0 ? YukkuriMovieMaker.Commons.Animation.GetRandomMoveRate(model, id, frame, description.FPS, span)
            : new Random(model.GetHashCode() / (id + 1) + frame).NextDouble();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

namespace YukkuriMovieMaker.Player.Video
{
    internal sealed class FrameTime(int frame)
    {
        public int Frame { get; } = frame;
    }

    internal class TimelineSourceDescription
    {
        public int FPS { get; init; } = 30;
    }

    internal class TimelineItemSourceDescription : TimelineSourceDescription
    {
        public FrameTime ItemPosition { get; init; } = new(0);
        public FrameTime ItemDuration { get; init; } = new(60);
    }

    internal sealed class EffectDescription : TimelineItemSourceDescription;
}

namespace YukkuriMovieMaker.Commons
{
    internal sealed class Animation
    {
        public double Value;

        public double GetValue(long frame, long length, int fps) => Value;

        public static double GetRandomMoveRate(object key, int id, int frame, int fps, double span) =>
            new MathNet.Numerics.Random.MersenneTwister(key.GetHashCode() / (id + 1) + frame / Math.Max(1, (int)(span * fps))).NextDouble();
    }
}

namespace MathNet.Numerics.Random
{
    internal sealed class MersenneTwister(int seed) : System.Random(seed);
}

namespace YukkuriMovieMaker.Project.Effects
{
    internal abstract class RandomEffectBase
    {
        public YukkuriMovieMaker.Commons.Animation Span { get; } = new();
    }

    internal sealed class RandomMoveEffect : RandomEffectBase;
}

namespace YukkuriMovieMaker.Player.Video.Effects
{
    using MathNet.Numerics.Random;
    using YukkuriMovieMaker.Commons;

    // GetRandomValue as YMM4 4.47.0.0 to 4.52.0.1 have it.
    internal abstract class RandomEffectBase<T> where T : YukkuriMovieMaker.Project.Effects.RandomEffectBase
    {
        protected T item;

        protected RandomEffectBase(T item)
        {
            this.item = item;
        }

        protected double GetRandomValue(EffectDescription effectDescription, int parameterID)
        {
            int frame = effectDescription.ItemPosition.Frame;
            int frame2 = effectDescription.ItemDuration.Frame;
            int fPS = effectDescription.FPS;
            double value = item.Span.GetValue(frame, frame2, fPS);
            if (value != 0.0) return Animation.GetRandomMoveRate(this, parameterID, frame, fPS, value);
            return ((Random)new MersenneTwister(GetHashCode() / (parameterID + 1) + frame)).NextDouble();
        }
    }

    internal sealed class RandomMoveEffect(YukkuriMovieMaker.Project.Effects.RandomMoveEffect effect)
        : RandomEffectBase<YukkuriMovieMaker.Project.Effects.RandomMoveEffect>(effect)
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public double Update(EffectDescription effectDescription) => GetRandomValue(effectDescription, 0) + 2 * GetRandomValue(effectDescription, 1);

        public Func<EffectDescription, double> Later() => description => GetRandomValue(description, 2);
    }
}

namespace YukkuriMovieMaker.Project.Items
{
    internal sealed class TextItem;

    internal sealed class VoiceItem;
}

namespace YukkuriMovieMaker.Player.Video.Items
{
    // Seeded by the source itself, as YMM4's text and subtitle sources are.
    internal sealed class TextSource
    {
        private readonly TextItem item;

        public TextSource(TextItem item)
        {
            this.item = item;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int Seed(string text) => GetHashCode() + text.Length;

        public Func<string, int> LaterSeed()
        {
            int extra = item is null ? 0 : 1;
            return text => GetHashCode() + text.Length + extra;
        }
    }

    internal sealed class JimakuSource
    {
        private readonly VoiceItem item;

        public JimakuSource(VoiceItem item)
        {
            this.item = item;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int Seed(string text) => GetHashCode() + (item is null ? 0 : text.Length);
    }
}
