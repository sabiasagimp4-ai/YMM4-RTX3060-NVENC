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
        if (FindField(type, "decodedFrame") is { } frame && !frame.FieldType.IsValueType
            && HasTime(frame.FieldType, "SampleTime") && HasTime(frame.FieldType, "SampleDuration")) return "MF2";
        if (HasTime(type, "currentTime") && HasTime(type, "currentDuration") && HasTime(type, "streamStartTime")) return "legacy";
        if (type.Name == "CachedVideoFileSource" && InnerSources(type, videoSource).Count == 1) return "wrapper";
        return "unverified";
    }

    internal static FieldInfo? FindField(Type type, string name)
    {
        for (var value = type; value is not null; value = value.BaseType)
            if (value.GetField(name, Instance | BindingFlags.DeclaredOnly) is { } field) return field;
        return null;
    }

    internal static List<FieldInfo> InnerSources(Type type, Type videoSource)
    {
        var fields = new List<FieldInfo>();
        for (var value = type; value is not null; value = value.BaseType)
            fields.AddRange(value.GetFields(Instance | BindingFlags.DeclaredOnly).Where(field => videoSource.IsAssignableFrom(field.FieldType)));
        return fields;
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
