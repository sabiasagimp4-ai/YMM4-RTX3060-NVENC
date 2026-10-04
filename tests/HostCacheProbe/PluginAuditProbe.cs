using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json.Nodes;
using NVEncVideoWriterPlugin;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

// Temporary feasibility probe: can a plugin's code be judged automatically, as the Community plugin was by reading it?
// 1. A static scan of the IL of each Community namespace for the hidden inputs the manual audit looked for (clock,
//    random numbers, identity hashes, files, network, native code, threads, static state, reflection).
// 2. Trial renders of each Community video effect on a moving shape: the same frames again in another order, after a
//    pause, from a fresh renderer of the same scene, and from a copy of the project.
// Both are compared with the manual verdicts (KnownCode.VerifiedCommunity and the exclusions). Never fails the run.
internal static class PluginAuditProbe
{
    private const string Prefix = KnownCode.CommunityAssembly + ".";
    private static readonly string[] IdentitySeeded = ["CameraShake", "RectangleGlitchNoise", "StripeGlitchNoise", "WaveClipping"];
    private static readonly Dictionary<string, string> Excluded = new(StringComparer.Ordinal)
    {
        ["AfterImage"] = "history", ["MotionBlur"] = "history", ["AudioVolume"] = "audio",
        ["ArrangeGroupItems"] = "other-items", ["RadialArrangeGroupItems"] = "other-items", ["TilingGroupItems"] = "other-items",
        ["Container"] = "other-items", ["Scene"] = "other-items", ["OpenFx"] = "native", ["Lut"] = "files", ["GradientMap"] = "files",
        ["DirectionalColorKey"] = "unread", ["FillSameground"] = "unread", ["FillSametype"] = "unread", ["ParticleOutput"] = "unread",
        ["Particlize"] = "unread", ["PuppetDeformation"] = "unread", ["VectorFieldWarp"] = "unread", ["Pen"] = "unread",
    };

    private static string Area(string? ns)
    {
        if (ns is null || !ns.StartsWith(Prefix, StringComparison.Ordinal)) return "(other)";
        var parts = ns[Prefix.Length..].Split('.');
        int take = parts[0] == "Effect" ? 3 : 2;
        return string.Join(".", parts.Take(Math.Min(take, parts.Length)));
    }

    private static string Manual(string area)
    {
        string last = area.Split('.').Last();
        if (KnownCode.VerifiedCommunity.Contains(area)) return IdentitySeeded.Contains(last) ? "identity" : "verified";
        return Excluded.TryGetValue(last, out var reason) ? "excluded:" + reason : "not-listed";
    }

    internal static void Run(Assembly host, IGraphicsDevicesAndContext context)
    {
        string file = Path.Combine(Path.GetDirectoryName(host.Location)!, KnownCode.CommunityAssembly + ".dll");
        if (!File.Exists(file)) { Console.WriteLine("AUDIT|skipped: no Community plugin"); return; }
        var community = Assembly.LoadFrom(file);
        Console.WriteLine($"AUDIT|community MVID {community.ManifestModule.ModuleVersionId}");
        try { RunStatic(community); }
        catch (Exception error) { Console.WriteLine("AUDIT|static failed: " + error); }
        try { RunTrials(host, context, community); }
        catch (Exception error) { Console.WriteLine("TRIAL|failed: " + error); }
    }

    // ---- 1. Static scan ----

    private static readonly Dictionary<short, OpCode> Codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(field => (OpCode)field.GetValue(null)!).GroupBy(code => code.Value).ToDictionary(group => group.Key, group => group.First());

    private static IEnumerable<(OpCode Code, int Operand)> Decode(byte[] il)
    {
        int i = 0;
        while (i < il.Length)
        {
            short value = il[i++];
            if (value == 0xFE && i < il.Length) value = unchecked((short)(0xFE00 | il[i++]));
            if (!Codes.TryGetValue(value, out var code)) yield break;
            int operand = 0;
            switch (code.OperandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget: case OperandType.ShortInlineI: case OperandType.ShortInlineVar: i += 1; break;
                case OperandType.InlineVar: i += 2; break;
                case OperandType.InlineI8: case OperandType.InlineR: i += 8; break;
                case OperandType.InlineSwitch: int count = BitConverter.ToInt32(il, i); i += 4 + 4 * count; break;
                default: operand = BitConverter.ToInt32(il, i); i += 4; break;
            }
            yield return (code, operand);
        }
    }

    private sealed class Findings
    {
        internal readonly SortedDictionary<string, SortedSet<string>> Categories = new(StringComparer.Ordinal);
        internal readonly SortedSet<string> Host = new(StringComparer.Ordinal);
        internal int Methods;
        internal void Add(string category, string detail)
        {
            if (!Categories.TryGetValue(category, out var set)) Categories[category] = set = new SortedSet<string>(StringComparer.Ordinal);
            set.Add(detail);
        }
    }

    private static void RunStatic(Assembly community)
    {
        Type[] types;
        try { types = community.GetTypes(); }
        catch (ReflectionTypeLoadException error) { types = error.Types.OfType<Type>().ToArray(); }
        var areas = types.GroupBy(type => Area(type.Namespace)).OrderBy(group => group.Key, StringComparer.Ordinal).ToArray();
        var results = new List<(string Area, string Manual, Findings Found)>();
        foreach (var area in areas)
        {
            if (area.Key == "(other)") continue;
            var found = new Findings();
            var visited = new HashSet<MethodBase>();
            var queue = new Queue<MethodBase>();
            foreach (var type in area)
                foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Cast<MethodBase>().Concat(type.GetConstructors(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)))
                    if (visited.Add(method)) queue.Enqueue(method);
            while (queue.TryDequeue(out var method))
            {
                found.Methods++;
                Scan(method, community, found, visited, queue, area.Key);
            }
            results.Add((area.Key, Manual(area.Key), found));
        }
        foreach (var (area, manual, found) in results)
        {
            string summary = found.Categories.Count == 0 ? "clean"
                : string.Join("; ", found.Categories.Select(pair => $"{pair.Key}({string.Join(",", pair.Value.Take(4))}{(pair.Value.Count > 4 ? ",…" : "")})"));
            Console.WriteLine($"AUDIT|{area}|manual={manual}|methods={found.Methods}|static={summary}");
        }
        // Host API that excluded namespaces use and no verified one does: the inputs the manual audit kept out.
        var verifiedHost = new HashSet<string>(results.Where(r => r.Manual is "verified" or "identity").SelectMany(r => r.Found.Host), StringComparer.Ordinal);
        foreach (var (area, manual, found) in results.Where(r => r.Manual.StartsWith("excluded", StringComparison.Ordinal) || r.Manual == "not-listed"))
        {
            var extra = found.Host.Where(name => !verifiedHost.Contains(name)).ToArray();
            if (extra.Length != 0) Console.WriteLine($"AUDIT-HOST|{area}|{manual}|{string.Join(",", extra.Take(12))}{(extra.Length > 12 ? ",…" : "")}");
        }
    }

    private static void Scan(MethodBase method, Assembly assembly, Findings found, HashSet<MethodBase> visited, Queue<MethodBase> queue, string area)
    {
        if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0) found.Add("native", "DllImport " + method.Name);
        byte[]? il;
        try { il = method.GetMethodBody()?.GetILAsByteArray(); }
        catch { return; }
        if (il is null) return;
        Type[]? typeArgs = method.DeclaringType is { IsGenericType: true } declaring ? declaring.GetGenericArguments() : null;
        Type[]? methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
        foreach (var (code, token) in Decode(il))
        {
            if (code.OperandType is not (OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineTok or OperandType.InlineType)) continue;
            MemberInfo? member;
            try { member = method.Module.ResolveMember(token, typeArgs, methodArgs); }
            catch { continue; }
            switch (member)
            {
                case MethodBase callee:
                    Classify(callee, found);
                    if (callee.Module.Assembly == assembly)
                    {
                        if ((callee.Attributes & MethodAttributes.PinvokeImpl) != 0) found.Add("native", "DllImport " + callee.Name);
                        var definition = callee is MethodInfo { IsGenericMethod: true } generic ? generic.GetGenericMethodDefinition() : callee;
                        if (Area(definition.DeclaringType?.Namespace) != area && visited.Add(definition)) queue.Enqueue(definition);
                    }
                    break;
                case FieldInfo field:
                    if (field.DeclaringType?.FullName is { } owner && owner.StartsWith("YukkuriMovieMaker.", StringComparison.Ordinal) && field.Module.Assembly != assembly)
                        found.Host.Add(owner + "." + field.Name);
                    if (field.IsStatic && !field.IsInitOnly && (code == OpCodes.Stsfld || code == OpCodes.Ldsflda)
                        && !(method is ConstructorInfo { IsStatic: true } && method.DeclaringType == field.DeclaringType)
                        && field.DeclaringType?.Name.StartsWith("<>c", StringComparison.Ordinal) != true)
                        found.Add("static-write", $"{field.DeclaringType?.Name}.{field.Name}");
                    break;
                case Type type:
                    if (type.FullName is { } name && name.StartsWith("YukkuriMovieMaker.", StringComparison.Ordinal) && type.Assembly != assembly) found.Host.Add(name);
                    break;
            }
        }
    }

    private static void Classify(MethodBase callee, Findings found)
    {
        string type = callee.DeclaringType?.FullName ?? string.Empty;
        string name = callee.Name;
        string label = $"{callee.DeclaringType?.Name}.{name}";
        if (type.StartsWith("YukkuriMovieMaker.", StringComparison.Ordinal) && !type.StartsWith(Prefix, StringComparison.Ordinal))
            found.Host.Add(type + "." + name);
        if (type == "System.DateTime" && name is "get_Now" or "get_UtcNow" or "get_Today"
            || type == "System.DateTimeOffset" && name is "get_Now" or "get_UtcNow"
            || type == "System.Diagnostics.Stopwatch" || type == "System.TimeProvider"
            || type == "System.Environment" && name.StartsWith("get_TickCount", StringComparison.Ordinal))
            found.Add("clock", label);
        else if (type == "System.Random")
        {
            if (name == ".ctor") found.Add(callee.GetParameters().Length == 0 ? "random-unseeded" : "random-seeded", "Random(" + callee.GetParameters().Length + ")");
            else if (name == "get_Shared") found.Add("random-unseeded", "Random.Shared");
        }
        else if (type == "System.Guid" && name == "NewGuid" || type.StartsWith("System.Security.Cryptography.RandomNumberGenerator", StringComparison.Ordinal))
            found.Add("random-unseeded", label);
        else if (type == "System.Runtime.CompilerServices.RuntimeHelpers" && name == "GetHashCode" || type == "System.Object" && name == "GetHashCode")
            found.Add("identity-hash", label);
        else if (type == "System.String" && name == "GetHashCode" || type == "System.HashCode")
            found.Add("process-hash", label);
        else if (type.StartsWith("System.IO.", StringComparison.Ordinal)
            && (callee.DeclaringType?.Name is "File" or "FileInfo" or "FileStream" or "Directory" or "DirectoryInfo" or "FileSystemInfo" or "FileSystemWatcher" or "StreamWriter"
                || callee.DeclaringType?.Name == "StreamReader" && name == ".ctor" && callee.GetParameters().FirstOrDefault()?.ParameterType == typeof(string))
            || type.Contains("WIC", StringComparison.Ordinal) && name.Contains("Filename", StringComparison.Ordinal)
            || type.StartsWith("YukkuriMovieMaker.Plugin.FileSource", StringComparison.Ordinal))
            found.Add("files", label);
        else if (type.StartsWith("System.Net.", StringComparison.Ordinal)) found.Add("network", label);
        else if (type == "System.Diagnostics.Process"
            || type == "System.Runtime.InteropServices.Marshal" && name.StartsWith("GetDelegateForFunctionPointer", StringComparison.Ordinal)
            || type == "System.Runtime.InteropServices.NativeLibrary")
            found.Add("native", label);
        else if (type == "System.Threading.Tasks.Task" && name is "Run" or "get_Factory" or "Delay"
            || type == "System.Threading.Tasks.TaskFactory" && name.StartsWith("StartNew", StringComparison.Ordinal)
            || type == "System.Threading.Thread" && name == ".ctor" || type == "System.Threading.ThreadPool"
            || type == "System.Threading.Timer" && name == ".ctor" || type == "System.Threading.Tasks.Parallel")
            found.Add("threads", label);
        else if (type == "System.Reflection.MethodBase" && name == "Invoke" || type == "System.Activator"
            || type == "System.Delegate" && name == "DynamicInvoke" || type.StartsWith("System.Linq.Expressions", StringComparison.Ordinal) && name == "Compile"
            || type == "System.Type" && name == "InvokeMember")
            found.Add("reflection", label);
    }

    // ---- 2. Trial renders ----

    private const int Frames = 30, Width = 192, Height = 108;
    private static readonly int[] Samples = [3, 17, 29];

    private static void RunTrials(Assembly host, IGraphicsDevicesAndContext context, Assembly community)
    {
        var effectInterface = typeof(YukkuriMovieMaker.Plugin.Effects.IVideoEffect);
        Type[] types;
        try { types = community.GetTypes(); }
        catch (ReflectionTypeLoadException error) { types = error.Types.OfType<Type>().ToArray(); }
        var effects = types.Where(type => type is { IsClass: true, IsAbstract: false } && effectInterface.IsAssignableFrom(type)
            && type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes) is not null)
            .OrderBy(type => type.FullName, StringComparer.Ordinal).ToArray();
        Console.WriteLine($"TRIAL|{effects.Length} video effects");
        TimelineFrameCache.Enabled = false;
        var dc = context.DeviceContext;
        bool moving = Enum.TryParse<AnimationType>("直線移動", out var linear);
        Console.WriteLine($"TRIAL|moving shape: {(moving ? "直線移動 -60 to 60" : "no linear animation type, still shape")}");

        var (baseTimeline, baseScene) = Build(null, moving, linear);
        var viewport = new TimelineFrameCache.PreviewViewport(Width, Height, Matrix3x2.Identity, new Vector2(Width / 2f, Height / 2f), 96, 96,
            new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            dc.AntialiasMode, dc.TextAntialiasMode, dc.PrimitiveBlend, dc.UnitMode, baseScene.ID, baseTimeline.ID, Stopwatch.GetTimestamp(), false);
        Dictionary<int, byte[]> plain;
        using (var source = Create(host, context, baseScene)) plain = Render(source, baseTimeline, dc, viewport, Samples);
        int baseMoving = Enumerable.Range(1, Samples.Length - 1).Count(i => !plain[Samples[i]].SequenceEqual(plain[Samples[i - 1]]));
        Console.WriteLine($"TRIAL|plain shape: {baseMoving} of {Samples.Length - 1} sample steps differ");

        foreach (var type in effects)
        {
            string area = Area(type.Namespace);
            string manual = Manual(area);
            var clock = Stopwatch.StartNew();
            try
            {
                var (timeline, scene) = Build(type, moving, linear);
                var copyTimeline = YukkuriMovieMaker.Json.Json.LoadFromText<Timeline>(YukkuriMovieMaker.Json.Json.GetJsonText(timeline))!;
                var copyScenes = new Scenes(false); copyScenes.AddScene(copyTimeline);
                var copyScene = new Scene(copyTimeline, copyScenes, []);
                var flags = new List<string>();
                Dictionary<int, byte[]> first;
                using (var a = Create(host, context, scene))
                {
                    first = Render(a, timeline, dc, viewport, Enumerable.Range(0, Frames).ToArray());
                    var order = Render(a, timeline, dc, viewport, [29, 3, 17]);
                    if (Samples.Any(f => !order[f].SequenceEqual(first[f]))) flags.Add("order");
                    Thread.Sleep(350);
                    var later = Render(a, timeline, dc, viewport, [17]);
                    if (!later[17].SequenceEqual(first[17])) flags.Add("later");
                }
                using (var b = Create(host, context, scene))
                {
                    var fresh = Render(b, timeline, dc, viewport, [17, 3, 29]);
                    if (Samples.Any(f => !fresh[f].SequenceEqual(first[f]))) flags.Add("fresh-renderer");
                }
                using (var c = Create(host, context, copyScene))
                {
                    var copied = Render(c, copyTimeline, dc, viewport, Samples);
                    if (Samples.Any(f => !copied[f].SequenceEqual(first[f]))) flags.Add("copy");
                }
                bool noop = Samples.All(f => first[f].SequenceEqual(plain[f]));
                string verdict = flags.Count == 0 ? (noop ? "same-as-plain" : "deterministic")
                    : flags.SequenceEqual(["copy"]) ? "identity" : "unstable:" + string.Join("+", flags);
                Console.WriteLine($"TRIAL|{area}|{type.Name}|manual={manual}|trial={verdict}|{clock.ElapsedMilliseconds} ms");
            }
            catch (Exception error)
            {
                Console.WriteLine($"TRIAL|{area}|{type.Name}|manual={manual}|trial=error:{error.GetBaseException().GetType().Name}: {error.GetBaseException().Message}");
            }
        }
    }

    private static (Timeline, Scene) Build(Type? effect, bool moving, AnimationType linear)
    {
        var timeline = new Timeline();
        timeline.VideoInfo.Width = Width; timeline.VideoInfo.Height = Height; timeline.VideoInfo.FPS = 30;
        var shape = new ShapeItem { Frame = 0, Length = Frames, Layer = 0 };
        if (moving) shape.X.AnimationType = linear;
        if (effect is not null)
            shape.VideoEffects = shape.VideoEffects.Add((YukkuriMovieMaker.Plugin.Effects.IVideoEffect)Activator.CreateInstance(effect, nonPublic: true)!);
        timeline.Items = timeline.Items.Add(shape);
        timeline.RefreshTimelineLengthAndMaxLayer();
        if (moving)
        {
            var json = JsonNode.Parse(YukkuriMovieMaker.Json.Json.GetJsonText(timeline))!;
            if (json["Items"]?[0]?["X"]?["Values"] is JsonArray { Count: > 0 } values)
            {
                var from = values[0]!.DeepClone();
                var to = values[0]!.DeepClone();
                from["Value"] = -60.0;
                to["Value"] = 60.0;
                values.Clear();
                values.Add(from);
                values.Add(to);
                timeline = YukkuriMovieMaker.Json.Json.LoadFromText<Timeline>(json.ToJsonString())!;
                timeline.RefreshTimelineLengthAndMaxLayer();
            }
        }
        var scenes = new Scenes(false); scenes.AddScene(timeline);
        return (timeline, new Scene(timeline, scenes, []));
    }

    private static Dictionary<int, byte[]> Render(ITimelineSource source, Timeline timeline, Vortice.Direct2D1.ID2D1DeviceContext dc,
        TimelineFrameCache.PreviewViewport viewport, int[] frames)
    {
        var result = new Dictionary<int, byte[]>();
        foreach (int frame in frames)
        {
            source.Update(timeline.VideoInfo.GetTimeFrom(frame), TimelineSourceUsage.Paused);
            result[frame] = TimelineFrameCache.CapturePreview(dc, source.Output, viewport)!;
        }
        return result;
    }

    private static ITimelineSource Create(Assembly host, IGraphicsDevicesAndContext context, Scene scene) =>
        (ITimelineSource)Activator.CreateInstance(host.GetType("YukkuriMovieMaker.Player.Video.TimelineSource", true)!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [context, scene, null], null)!;
}
