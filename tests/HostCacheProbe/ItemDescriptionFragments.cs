using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

namespace NVEncVideoWriterPlugin;

// Experimental test-only implementation. Product descriptions do not use this cache.
// Notifications select the item to invalidate; witnesses also check values and child replacements without
// notifications. This deliberately keeps resource discovery, model splitting and dependency indexing fresh.
internal sealed class ItemDescriptionFragments : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<IItem, Entry> entries = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, HashSet<IItem>> owners = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, Action> subscriptions = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IItem, object[]> rootChildren = new(ReferenceEqualityComparer.Instance);
    private readonly Action<object?> changed;
    private bool disposed, disabled;
    private long epoch;
    internal int Reused { get; private set; }
    internal int Serialized { get; private set; }
    internal string WitnessBypass { get; private set; } = string.Empty;
    internal int Retained { get { lock (gate) return entries.Count; } }
    internal ItemDescriptionFragments(Action<object?> changed) => this.changed = changed;

    internal IDisposable Enter(Scene scene)
    {
        var items = new HashSet<object>(scene.Scenes.Timelines.Append(scene.Timeline).Distinct().SelectMany(t => t.Items), ReferenceEqualityComparer.Instance);
        lock (gate)
        {
            Reused = Serialized = 0;
            foreach (var item in rootChildren.Keys.Where(item => !items.Contains(item)).ToArray()) Remove(item);
        }
        var previous = FrameDescriptionJson.ItemFragments;
        FrameDescriptionJson.ItemFragments = disabled ? null : new Converter(this, items);
        return new Scope(previous);
    }

    private sealed class Scope(JsonConverter? previous) : IDisposable
    { public void Dispose() => FrameDescriptionJson.ItemFragments = previous; }
    private sealed record Witness(Func<object?> Read, object? Value, bool Reference, Type? Type)
    {
        internal bool Current(JsonSerializer serializer)
        {
            object? now = Read();
            if (now?.GetType() != Type) return false;
            return Reference ? ReferenceEquals(now, Value) : Equals(Scalar(now, serializer), Value);
        }
    }
    private sealed record Entry(string Json, Witness[] Witnesses, object[] Children);

    internal void Invalidate(object? sender)
    {
        lock (gate)
        {
            epoch++;
            if (sender is not null && owners.TryGetValue(sender, out var items))
                foreach (var item in items.ToArray()) entries.Remove(item);
            else entries.Clear();
            // Child subscriptions stay until the next successful publication or disposal. A shared child
            // invalidates every owner; it must not be unsubscribed while another owner still uses it.
        }
    }

    internal void Disable()
    {
        lock (gate) disabled = true;
        Invalidate(null);
    }

    private void ChildChanged(object? sender, PropertyChangedEventArgs args)
    { changed(sender); } // Never enter the tracker while holding the fragment gate.

    private void Remove(IItem item)
    {
        entries.Remove(item);
        if (!rootChildren.Remove(item, out var children)) return;
        foreach (var child in children)
        {
            if (!owners.TryGetValue(child, out var items)) continue;
            items.Remove(item);
            if (items.Count != 0) continue;
            owners.Remove(child);
            if (subscriptions.Remove(child, out var remove)) remove();
        }
    }

    private void Publish(IItem item, Entry entry, long before)
    {
        lock (gate)
        {
            if (disposed || epoch != before || rootChildren.Count >= 2000 && !rootChildren.ContainsKey(item)) return;
            Remove(item);
            entries[item] = entry;
            rootChildren[item] = entry.Children;
            foreach (var child in entry.Children)
            {
                if (!owners.TryGetValue(child, out var items)) owners[child] = items = new(ReferenceEqualityComparer.Instance);
                items.Add(item);
                if (child is INotifyPropertyChanged notifying && !subscriptions.ContainsKey(child))
                {
                    notifying.PropertyChanged += ChildChanged;
                    subscriptions[child] = () => notifying.PropertyChanged -= ChildChanged;
                }
            }
        }
    }

    private sealed class Converter(ItemDescriptionFragments cache, HashSet<object> items) : JsonConverter
    {
        private bool writing;
        public override bool CanConvert(Type type) => !writing && (type == typeof(ShapeItem) || type == typeof(TextItem))
            && typeof(Scene).Assembly.ManifestModule.ModuleVersionId == new Guid("23e5b5b5-adcf-43b7-b976-b6b63f8dadea");
        public override bool CanRead => false;
        public override object? ReadJson(JsonReader reader, Type type, object? existing, JsonSerializer serializer) => throw new NotSupportedException();
        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        {
            if (value is not IItem item || !items.Contains(item))
            { Fresh(writer, value, serializer); return; }
            Entry? old; long before;
            lock (cache.gate) { cache.entries.TryGetValue(item, out old); before = cache.epoch; }
            if (old is not null && Current(old, serializer))
            {
                lock (cache.gate)
                    if (!cache.disposed && before == cache.epoch)
                    { writer.WriteRawValue(old.Json); cache.Reused++; return; }
            }
            cache.Serialized++;
            if (!TryWitness(item, serializer, out var witnesses, out var children, out string why))
            { cache.WitnessBypass = why; Fresh(writer, item, serializer); return; } // Byte arrays and foreign/mutable graphs stay at their original path.
            using var text = new StringWriter(CultureInfo.InvariantCulture);
            using (var output = new JsonTextWriter(text)) Fresh(output, item, serializer);
            string json = text.ToString();
            var entry = new Entry(json, witnesses, children);
            if (Current(entry, serializer)) cache.Publish(item, entry, before);
            writer.WriteRawValue(json);
        }
        private void Fresh(JsonWriter writer, object? value, JsonSerializer serializer)
        {
            writing = true;
            try { serializer.Serialize(writer, value, typeof(IItem)); }
            finally { writing = false; }
        }
        private static bool Current(Entry entry, JsonSerializer serializer)
        {
            try { return entry.Witnesses.All(witness => witness.Current(serializer)); }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { return false; }
        }
    }

    private static object? Scalar(object? value, JsonSerializer serializer) => value switch
    {
        null => null,
        double number => BitConverter.DoubleToInt64Bits(number),
        float number => BitConverter.SingleToInt32Bits(number),
        decimal number => string.Join(",", decimal.GetBits(number)),
        DateTime date => date.ToBinary(),
        DateTimeOffset date => (date.Ticks, date.Offset.Ticks),
        System.Numerics.Vector2 point => (BitConverter.SingleToInt32Bits(point.X), BitConverter.SingleToInt32Bits(point.Y)),
        string or bool or char or byte or sbyte or short or ushort or int or uint or long or ulong or Enum or Type => value,
        _ => ScalarJson(value, serializer),
    };
    private static string ScalarJson(object value, JsonSerializer serializer)
    {
        using var text = new StringWriter(CultureInfo.InvariantCulture);
        using var output = new JsonTextWriter(text);
        serializer.Serialize(output, value);
        return text.ToString();
    }

    private static bool TryWitness(IItem item, JsonSerializer serializer, out Witness[] witnesses, out object[] children, out string reason)
    {
        var checks = new List<Witness>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        string at = item.GetType().FullName ?? "item";
        bool Visit(Func<object?> read, int depth)
        {
            if (depth > 48 || checks.Count > 4096) return false;
            object? value = read();
            if (value is null || value is string or Type || value.GetType().IsValueType)
            {
                Type? scalarType = value?.GetType();
                if (scalarType is not null && value is not string and not Type && !scalarType.IsPrimitive && !scalarType.IsEnum
                    && value is not decimal and not DateTime and not DateTimeOffset and not Guid and not TimeSpan
                    && value is not System.Windows.Media.Color and not System.Numerics.Vector2) return false;
                checks.Add(new(read, Scalar(value, serializer), false, scalarType)); return true;
            }
            Type type = value.GetType();
            at = type.FullName ?? type.Name;
            // ImmutableList's elements can still mutate. Visit them, and also witness the list reference.
            bool immutableList = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(System.Collections.Immutable.ImmutableList<>);
            if (!immutableList && type.Assembly != typeof(Scene).Assembly && type.Assembly != typeof(Animation).Assembly) return false;
            if (value is byte[] || value is IEnumerable && !immutableList) return false;
            checks.Add(new(read, value, true, type));
            if (!seen.Add(value)) return true;
            if (immutableList)
            {
                foreach (object? child in (IEnumerable)value)
                    if (!Visit(() => child, depth + 1)) return false;
                return true;
            }
            if (serializer.ContractResolver.ResolveContract(type) is not JsonObjectContract contract
                || contract.Converter is { CanWrite: true } || contract.OnSerializingCallbacks.Count != 0
                || contract.OnSerializedCallbacks.Count != 0 || contract.ExtensionDataGetter is not null) return false;
            foreach (var property in contract.Properties)
            {
                if (property.Ignored || !property.Readable) continue;
                at = type.FullName + "." + property.PropertyName;
                if (property.ShouldSerialize is not null || property.GetIsSpecified is not null || property.ValueProvider is null
                    || property.Converter is { CanWrite: true }) return false;
                if (!Visit(() => property.ValueProvider.GetValue(value), depth + 1)) return false;
            }
            return true;
        }
        try
        {
            bool ok = Visit(() => item, 0);
            witnesses = ok ? checks.ToArray() : [];
            children = ok ? seen.ToArray() : [];
            reason = ok ? string.Empty : at;
            return ok;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        { witnesses = []; children = []; reason = at + ": " + error.GetType().Name; return false; }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true; epoch++;
            foreach (var remove in subscriptions.Values) remove();
            subscriptions.Clear(); owners.Clear(); entries.Clear(); rootChildren.Clear();
        }
    }
}
