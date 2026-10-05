using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using YukkuriMovieMaker.Plugin.FileWriter;

namespace NVEncVideoWriterPlugin;

// The NVENC writer as YMM4 4.54 and later expect it: an IVideoFileWriter3 that declares GPU frames. The type is made
// at run time, only on a YMM4 that has the interface. A type of the plugin that implemented it could not be loaded on
// an older YMM4, which loads every type of a plugin. Every other member is the writer's own (IVideoFileWriter2).
public class GpuWriterProxy : DispatchProxy
{
    private static readonly ConcurrentDictionary<MethodInfo, MethodInfo> Targets = new();
    private NvencVideoFileWriter? writer;

    internal static IVideoFileWriter Create(Type writer3, NvencVideoFileWriter writer)
    {
        var proxy = (GpuWriterProxy)Create(writer3, typeof(GpuWriterProxy));
        proxy.writer = writer;
        return (IVideoFileWriter)proxy;
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(method);
        if (method.Name == "get_IsGpuFrameSupported" && method.GetParameters().Length == 0) return true;
        var target = Targets.GetOrAdd(method, static m => typeof(NvencVideoFileWriter).GetMethod(m.Name, BindingFlags.Public | BindingFlags.Instance,
                m.GetParameters().Select(p => p.ParameterType).ToArray())
            ?? throw new NotSupportedException($"YMM4 の出力インターフェイスの {m.DeclaringType?.Name}.{m.Name} に対応していません。"));
        try
        {
            return target.Invoke(writer, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is { } inner)
        {
            ExceptionDispatchInfo.Capture(inner).Throw();
            throw;
        }
    }
}
