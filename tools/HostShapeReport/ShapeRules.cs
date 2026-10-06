using System.Reflection;

namespace NVEncVideoWriterPlugin;

// Mirrors FrameRenderReadiness's classification using only Type metadata, so it also runs on
// MetadataLoadContext types (the offline host shape report). ReadinessChecks asserts both agree.
internal static class ShapeRules
{
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    internal static bool IsImplementation(Type type, Type videoSource) =>
        !type.IsInterface && !type.IsAbstract && videoSource.IsAssignableFrom(type);

    internal static string Predict(Type type, Type videoSource)
    {
        if (!type.IsClass || type.ContainsGenericParameters) return "unhookable";
        return type.FullName switch
        {
            "YukkuriMovieMaker.Plugin.FileSource.MediaFoundation.Source2.MFVideoFileSource2" =>
                FindField(type, "decodedFrame") is { } frame && !frame.FieldType.IsValueType
                && HasTime(frame.FieldType, "SampleTime") && HasTime(frame.FieldType, "SampleDuration") ? "MF2" : "unverified",
            "YukkuriMovieMaker.Plugin.FileSource.MediaFoundation.MFVideoFileSource" => StreamClock(type, false) ? "MF-legacy" : "unverified",
            "YukkuriMovieMaker.Plugin.FileSource.FFmpeg.FFmpegVideoFileSource" => FFmpegClock(type) ? "FFmpeg" : "unverified",
            "YukkuriMovieMaker.Plugin.FileSource.WIC.WICGifVideoSource" or "YukkuriMovieMaker.Plugin.FileSource.WIC.WICWebpVideoSource" => "WIC",
            "YukkuriMovieMaker.Plugin.FileSource.WIC.WICSequentialImageVideoSource" => Sequence(type) ? "image" : "unverified",
            "YukkuriMovieMaker.Plugin.CachedVideoFileSource" => WrappedSource(type, videoSource) is not null ? "wrapper" : "unverified",
            _ => "unverified",
        };
    }

    internal static FieldInfo? FindField(Type type, string name)
    {
        for (var value = type; value is not null; value = value.BaseType)
            if (value.GetField(name, Instance | BindingFlags.DeclaredOnly) is { } field) return field;
        return null;
    }

    // CachedVideoFileSource delegates to resource.Source.
    internal static PropertyInfo? WrappedSource(Type type, Type videoSource)
    {
        if (FindField(type, "resource") is not { } resource || resource.FieldType.IsValueType) return null;
        PropertyInfo? source;
        try { source = resource.FieldType.GetProperty("Source", Instance); }
        catch (AmbiguousMatchException) { return null; }
        return source?.GetMethod is not null && source.GetIndexParameters().Length == 0 && videoSource.IsAssignableFrom(source.PropertyType)
            ? source : null;
    }

    // frames (string[]), currentFrame (int), source (a reference) and GetFrameIndex(TimeSpan) -> int.
    private static bool Sequence(Type type) =>
        FindField(type, "frames")?.FieldType.FullName == "System.String[]" && FindField(type, "currentFrame")?.FieldType.FullName == "System.Int32"
        && FindField(type, "source") is { FieldType.IsValueType: false }
        && type.GetMethods(BindingFlags.Public | BindingFlags.Instance).Any(method => method.Name == "GetFrameIndex"
            && method.ReturnType.FullName == "System.Int32" && method.GetParameters() is [{ ParameterType.FullName: "System.TimeSpan" }]);

    private static bool StreamClock(Type type, bool needsDuration) =>
        HasTime(type, "currentTime") && HasTime(type, "currentDuration") && HasTime(type, "streamStartTime")
        && (!needsDuration || HasTime(type, "Duration"));

    // The stream clock with the stream start optional (YMM4 4.52 has none), and one SeekTo(TimeSpan, ...) to hook.
    private static bool FFmpegClock(Type type) =>
        HasTime(type, "currentTime") && HasTime(type, "currentDuration") && HasTime(type, "Duration")
        && (HasTime(type, "streamStartTime") || FindField(type, "streamStartTime") is null && !HasProperty(type, "streamStartTime"))
        && type.GetMethods(Instance | BindingFlags.DeclaredOnly).Count(method => method.Name == "SeekTo" && !method.IsAbstract
            && method.GetParameters() is [{ ParameterType.FullName: "System.TimeSpan" }, ..]) == 1;

    private static bool HasProperty(Type type, string name)
    {
        try { return type.GetProperty(name, Instance) is not null; }
        catch (AmbiguousMatchException) { return true; }
    }

    // A field of that name decides alone; otherwise a readable, non-indexed property is accepted.
    private static bool HasTime(Type type, string name)
    {
        if (FindField(type, name) is { } field) return IsTime(field.FieldType);
        PropertyInfo? property;
        try { property = type.GetProperty(name, Instance); }
        catch (AmbiguousMatchException) { return false; }
        return property is not null && property.GetIndexParameters().Length == 0 && property.GetMethod is not null && IsTime(property.PropertyType);
    }

    private static bool IsTime(Type type) => type.FullName is "System.Int64" or "System.TimeSpan";
}
