using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace NVEncVideoWriterPlugin;

// Some of YMM4's randomness is seeded with the renderer's own objects, which every renderer (the preview, an export,
// the idle pre-renderer) and every source created again has its own of, so each draws other values:
//  - the Random*Effect family before 4.52.0.2: RandomEffectBase<T>.GetRandomValue seeds with the effect processor
//    (GetHashCode(), Animation.GetRandomMoveRate(this, ...)); 4.52.0.2 changed both to the effect (item);
//  - text and subtitles revealed or hidden in random order, in every version: TextSource and JimakuSource seed with
//    themselves (GetHashCode() + the text's length), and are created again when the item comes back into the frame.
// The plugin makes those renderers draw from the model object they render instead: the effect, the text item or the
// voice item (as YMM4 4.52.0.2 does for the effects). Every renderer of the same project then draws the same values
// in this session, and the key names that object (FrameCacheKey.IdentitySeeds). The values stay as random as before
// (an object's identity hash is arbitrary), but text in random order no longer changes when it comes back into the
// frame, and an export draws what the preview showed.
//
// The effects: the calls of GetRandomValue in the processors that derive from RandomEffectBase<T> are replaced with
// RandomValue below, GetRandomValue as 4.52.0.2 has it, made of the members the host's GetRandomValue calls. (Harmony
// cannot keep a patch of the method itself: its code is shared by all T, and the patch is lost once .NET recompiles
// it.) Only the reviewed shape is replaced (HostContracts identity-random; ReadinessChecks, IdleRandomChecks).
internal static class RandomSeedAlignment
{
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    internal const string EffectBaseName = "YukkuriMovieMaker.Player.Video.Effects.RandomEffectBase`1";
    internal static readonly string[] TextSourceNames = ["YukkuriMovieMaker.Player.Video.Items.TextSource", "YukkuriMovieMaker.Player.Video.Items.JimakuSource"];

    private enum Seeding { None, Model, Processor, Unknown }

    // How YMM4's GetRandomValue computes its value, from the members its code calls.
    private sealed record EffectShape(Func<object, int> Position, Func<object, int> Duration, Func<object, int> Fps,
        Func<object, long, long, int, double> SpanValue, Func<object, int, int, int, double, double> MoveRate, Func<int, double> Twister);

    private static readonly object gate = new();
    private static readonly ConcurrentDictionary<Type, Func<object, object?>> models = new();
    private static readonly MethodInfo randomValue = typeof(RandomSeedAlignment).GetMethod(nameof(RandomValue), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo textSeed = typeof(RandomSeedAlignment).GetMethod(nameof(TextSeed), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static Assembly? analyzedHost;
    private static Seeding analyzed;
    private static string analysisProblem = string.Empty;
    private static EffectShape? shape;
    private static Type? effectBase;
    private static Type[] textSources = [];
    private static (MethodBase Target, MethodInfo Patch)[] patches = [];
    private static volatile bool effectsAligned, textAligned;
    private static string[] coverage = [];

    // The Random*Effect processors draw from their effects: YMM4's own code (4.52.0.2 and later, or no such effects) or
    // the alignment installed on this host.
    internal static bool EffectsByModel(Assembly host)
    {
        lock (gate) return Analyze(host) switch { Seeding.None or Seeding.Model => true, Seeding.Processor => effectsAligned, _ => false };
    }

    // Text and subtitles revealed or hidden in random order draw from their items (installed on this host).
    internal static bool TextOrderByItem => textAligned;

    internal static IReadOnlyList<string> Coverage { get { lock (gate) return coverage; } }

    // Installs what this host needs. Never throws: what cannot be aligned stays as YMM4 draws it (and is not keyed).
    internal static void TryInstall(Assembly host, Harmony harmony)
    {
        lock (gate)
        {
            Uninstall(harmony);
            var lines = new List<string>();
            var added = new List<(MethodBase, MethodInfo)>();
            var seeding = Analyze(host);
            try
            {
                if (seeding == Seeding.Processor)
                {
                    var transpiler = new HarmonyMethod(typeof(RandomSeedAlignment).GetMethod(nameof(RewriteEffectCallers), BindingFlags.NonPublic | BindingFlags.Static)!);
                    var callers = FindEffectCallers(host).ToArray();
                    foreach (var caller in callers)
                    {
                        harmony.Patch(caller, transpiler: transpiler);
                        added.Add((caller, transpiler.method));
                    }
                    effectsAligned = callers.Length != 0;
                    lines.Add($"Random effects: seeded by their effects in {callers.Length} processor methods (YMM4's GetRandomValue seeds with the processor)");
                }
                else lines.Add("Random effects: " + seeding switch
                {
                    Seeding.None => "none",
                    Seeding.Model => "seeded by their effects (YMM4's own code)",
                    _ => "GetRandomValue is not the reviewed shape; not keyed" + (analysisProblem.Length == 0 ? "" : $" ({analysisProblem})"),
                });
            }
            catch (Exception error)
            {
                Unpatch(harmony, added);
                added.Clear();
                effectsAligned = false;
                lines.Add("Random effects: not aligned: " + error.GetBaseException().Message);
            }
            int effectPatches = added.Count;
            try
            {
                var transpiler = new HarmonyMethod(typeof(RandomSeedAlignment).GetMethod(nameof(RewriteTextSeeds), BindingFlags.NonPublic | BindingFlags.Static)!);
                var types = TextSourceNames.Select(name => host.GetType(name)).OfType<Type>().ToArray();
                if (types.FirstOrDefault(type => ItemField(type) is null) is { } itemless) throw new NotSupportedException(itemless.FullName + " has no item field");
                textSources = types;
                int seeded = 0;
                foreach (var type in types)
                {
                    var methods = WithNested(type).SelectMany(t => t.GetMethods(Declared).Cast<MethodBase>().Concat(t.GetConstructors(Declared)))
                        .Where(method => method.GetMethodBody() is not null && CountTextSeeds(method) != 0).ToArray();
                    foreach (var method in methods)
                    {
                        seeded += CountTextSeeds(method);
                        harmony.Patch(method, transpiler: transpiler);
                        added.Add((method, transpiler.method));
                    }
                }
                textAligned = seeded != 0;
                lines.Add(seeded == 0 ? "Random text order: no seed found; not keyed" : $"Random text order: seeded by the item at {seeded} places");
            }
            catch (Exception error)
            {
                Unpatch(harmony, added.Skip(effectPatches).ToList());
                added.RemoveRange(effectPatches, added.Count - effectPatches);
                textAligned = false;
                lines.Add("Random text order: not aligned: " + error.GetBaseException().Message);
            }
            patches = [.. added];
            coverage = [.. lines];
        }
    }

    internal static void Uninstall(Harmony harmony)
    {
        lock (gate)
        {
            Unpatch(harmony, patches);
            patches = [];
            effectsAligned = textAligned = false;
        }
    }

    private static void Unpatch(Harmony harmony, IEnumerable<(MethodBase Target, MethodInfo Patch)> list)
    {
        foreach (var (target, patch) in list)
            try { harmony.Unpatch(target, patch); } catch (Exception error) when (error is not OutOfMemoryException) { }
    }

    // Under gate.
    private static Seeding Analyze(Assembly host)
    {
        if (ReferenceEquals(analyzedHost, host)) return analyzed;
        analyzedHost = host;
        shape = null;
        effectBase = host.GetType(EffectBaseName);
        analysisProblem = string.Empty;
        try { analyzed = effectBase is null ? Seeding.None : AnalyzeEffects(effectBase); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            analyzed = Seeding.Unknown;
            analysisProblem = error.GetBaseException().Message;
        }
        return analyzed;
    }

    private static Seeding AnalyzeEffects(Type generic)
    {
        var method = generic.GetMethod("GetRandomValue", Declared) ?? throw new MissingMethodException(EffectBaseName, "GetRandomValue");
        var code = PatchProcessor.GetOriginalInstructions(method).Where(i => i.opcode != OpCodes.Nop).ToList();
        // Every use of the processor itself only reads its fields (the effect): seeded by the effect.
        var bare = code.Select((instruction, index) => (instruction, index))
            .Where(x => IsLoadThis(x.instruction) && !(x.index + 1 < code.Count && (code[x.index + 1].opcode == OpCodes.Ldfld || code[x.index + 1].opcode == OpCodes.Ldflda)))
            .Select(x => x.index).ToArray();
        if (bare.Length == 0) return Seeding.Model;
        // The reviewed shape (4.47.0.0 to 4.52.0.1): these calls in this order, the processor itself passed to
        // GetRandomMoveRate and hashed, and nothing else.
        var calls = code.Where(i => i.opcode == OpCodes.Call || i.opcode == OpCodes.Callvirt || i.opcode == OpCodes.Newobj).Select(i => (MethodBase)i.operand).ToArray();
        string[] expected = ["TimelineItemSourceDescription::get_ItemPosition", "FrameTime::get_Frame", "TimelineItemSourceDescription::get_ItemDuration",
            "FrameTime::get_Frame", "TimelineSourceDescription::get_FPS", "RandomEffectBase::get_Span", "Animation::GetValue",
            "Animation::GetRandomMoveRate", "Object::GetHashCode", "MersenneTwister::.ctor", "Random::NextDouble"];
        if (!calls.Select(m => m.DeclaringType?.Name + "::" + m.Name).SequenceEqual(expected) || bare.Length != 2
            || !IsCall(code[bare[1] + 1], calls[8]))
            return Seeding.Unknown;
        var spanValue = (MethodInfo)calls[6];
        if (!spanValue.GetParameters().Select(p => p.ParameterType).SequenceEqual([typeof(long), typeof(long), typeof(int)]) || spanValue.ReturnType != typeof(double))
            return Seeding.Unknown;
        var moveRate = (MethodInfo)calls[7];
        var twister = (ConstructorInfo)calls[9];
        if (!twister.GetParameters().Select(p => p.ParameterType).SequenceEqual([typeof(int)]) || calls[10] is not MethodInfo { ReturnType: var next } || next != typeof(double))
            return Seeding.Unknown;
        shape = new(Getter<int>(calls[0], calls[1]), Getter<int>(calls[2], calls[3]), Getter<int>(calls[4]),
            SpanValue((MethodInfo)calls[5], spanValue), moveRate.CreateDelegate<Func<object, int, int, int, double, double>>(),
            Twister(twister, (MethodInfo)calls[10]));
        return Seeding.Processor;
    }

    private static bool IsLoadThis(CodeInstruction instruction) =>
        instruction.opcode == OpCodes.Ldarg_0 || instruction.opcode == OpCodes.Ldarg && Convert.ToInt32(instruction.operand) == 0;

    private static bool IsCall(CodeInstruction instruction, MethodBase method) =>
        (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && Equals(instruction.operand, method);

    // (object) d => d.first.second..., for the getters of the effect description.
    private static Func<object, T> Getter<T>(params MethodBase[] chain)
    {
        var parameter = Expression.Parameter(typeof(object));
        Expression value = parameter;
        foreach (MethodInfo getter in chain) value = Expression.Call(Expression.Convert(value, getter.DeclaringType!), getter);
        return Expression.Lambda<Func<object, T>>(Expression.Convert(value, typeof(T)), parameter).Compile();
    }

    // item.Span.GetValue(frame, length, fps)
    private static Func<object, long, long, int, double> SpanValue(MethodInfo span, MethodInfo value)
    {
        var item = Expression.Parameter(typeof(object));
        var frame = Expression.Parameter(typeof(long));
        var length = Expression.Parameter(typeof(long));
        var fps = Expression.Parameter(typeof(int));
        var call = Expression.Call(Expression.Call(Expression.Convert(item, span.DeclaringType!), span), value, frame, length, fps);
        return Expression.Lambda<Func<object, long, long, int, double>>(call, item, frame, length, fps).Compile();
    }

    // ((Random)new MersenneTwister(seed)).NextDouble()
    private static Func<int, double> Twister(ConstructorInfo constructor, MethodInfo next)
    {
        var seed = Expression.Parameter(typeof(int));
        var call = Expression.Call(Expression.Convert(Expression.New(constructor, seed), next.DeclaringType!), next);
        return Expression.Lambda<Func<int, double>>(call, seed).Compile();
    }

    private static IEnumerable<Type> Processors(Assembly host, Type generic) =>
        HostTypes(host).Where(type => type is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false } && ClosedBase(type, generic) is not null);

    private static IEnumerable<Type> HostTypes(Assembly host)
    {
        try { return host.GetTypes(); }
        catch (ReflectionTypeLoadException partial) { return partial.Types.OfType<Type>(); }
    }

    private static Type? ClosedBase(Type type, Type generic)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
            if (current.IsGenericType && current.GetGenericTypeDefinition() == generic) return current;
        return null;
    }

    private static IEnumerable<Type> WithNested(Type type) => new[] { type }.Concat(type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).SelectMany(WithNested));

    // The methods of the processors (and their nested types) that call GetRandomValue.
    private static IEnumerable<MethodBase> FindEffectCallers(Assembly host) =>
        Processors(host, effectBase!).SelectMany(WithNested).Distinct()
            .SelectMany(type => type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)))
            .Where(method => method.GetMethodBody() is not null && PatchProcessor.GetOriginalInstructions(method).Any(IsGetRandomValueCall));

    private static bool IsGetRandomValueCall(CodeInstruction instruction) =>
        (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && instruction.operand is MethodInfo { Name: "GetRandomValue" } method
        && method.DeclaringType is { IsGenericType: true } declaring && declaring.GetGenericTypeDefinition() == effectBase;

    private static IEnumerable<CodeInstruction> RewriteEffectCallers(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (IsGetRandomValueCall(instruction))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = randomValue;
            }
            yield return instruction;
        }
    }

    // GetRandomValue of 4.52.0.2: the effect (item) where 4.52.0.1 and older use the processor.
    private static double RandomValue(object processor, object effectDescription, int parameterID)
    {
        var s = shape!;
        int frame = s.Position(effectDescription);
        int length = s.Duration(effectDescription);
        int fps = s.Fps(effectDescription);
        object item = Model(processor) ?? processor;
        double span = s.SpanValue(item, frame, length, fps);
        if (span != 0.0) return s.MoveRate(item, parameterID, frame, fps, span);
        return s.Twister(item.GetHashCode() / (parameterID + 1) + frame);
    }

    // The model a renderer draws: RandomEffectBase<T>.item, TextSource.item (TextItem), JimakuSource.item (VoiceItem).
    private static object? Model(object renderer) => models.GetOrAdd(renderer.GetType(), static type =>
    {
        FieldInfo? field = null;
        for (var current = type; current is not null && field is null; current = current.BaseType)
            if (current.IsGenericType && current.GetGenericTypeDefinition() == effectBase || textSources.Contains(current))
                field = ItemField(current);
        if (field is null) return static _ => null;
        var parameter = Expression.Parameter(typeof(object));
        return Expression.Lambda<Func<object, object?>>(
            Expression.Convert(Expression.Field(Expression.Convert(parameter, field.DeclaringType!), field), typeof(object)), parameter).Compile();
    })(renderer);

    private static FieldInfo? ItemField(Type type) => type.GetField("item", Instance | BindingFlags.DeclaredOnly) is { FieldType.IsValueType: false } field ? field : null;

    // A text source's hash where it seeds its random order: GetHashCode() called on the source itself.
    private static int CountTextSeeds(MethodBase method)
    {
        var code = PatchProcessor.GetOriginalInstructions(method);
        int count = 0;
        for (int i = 1; i < code.Count; i++)
            if (IsObjectHash(code[i]) && PushesSource(code[i - 1], method)) count++;
        return count;
    }

    private static bool IsObjectHash(CodeInstruction instruction) =>
        (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
        && instruction.operand is MethodInfo { Name: nameof(GetHashCode) } method && method.DeclaringType == typeof(object) && method.GetParameters().Length == 0;

    // The source itself: this in its own methods, or a field holding it (a lambda's closure).
    private static bool PushesSource(CodeInstruction instruction, MethodBase method) =>
        IsLoadThis(instruction) && !method.IsStatic && method.DeclaringType is { } declaring && textSources.Contains(declaring)
        || instruction.opcode == OpCodes.Ldfld && instruction.operand is FieldInfo field && textSources.Contains(field.FieldType);

    private static IEnumerable<CodeInstruction> RewriteTextSeeds(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        CodeInstruction? previous = null;
        foreach (var instruction in instructions)
        {
            if (previous is not null && IsObjectHash(instruction) && PushesSource(previous, __originalMethod))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = textSeed;
            }
            previous = instruction;
            yield return instruction;
        }
    }

    // The identity hash of the item the source draws (FrameCacheKey keys the item by it).
    private static int TextSeed(object source) => RuntimeHelpers.GetHashCode(Model(source) ?? source);
}
