using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Text;

namespace SonicD5.Json;

public static partial class JsonSerializer {
    public const string Null = "null";

    public static string Serialize(object? obj, JsonSerialization.Config config) {
        if (obj == null) return Null;
        StringBuilder sb = new();
        Serialize(ref sb, obj, new(obj.GetType(), null), config, 0);
        return sb.ToString();
    }

    public static string Serialize(object? obj) => Serialize(obj, new());

    private static void Serialize(ref StringBuilder sb, object? obj, LinkedType linkedType, JsonSerialization.Config config, int indentCount) {
        if (obj == null) {
            sb.Append(Null);
            return;
        }
        var type = linkedType.Value;
        var nullableValue = Nullable.GetUnderlyingType(type);
        if (nullableValue != null) Serialize(ref sb, obj, new(nullableValue, linkedType), config, indentCount);
        try {
            foreach (var s in config.Pack) {
                Type? foundType = null;
                if (!s.TypePredicate(linkedType.Value, ref foundType)) continue;
                StringBuilder sbTemp = new(sb.ToString());
                bool hasSkiped = false;
                s.Callback.Invoke(ref sbTemp, obj, linkedType, config, indentCount, (o, lt, ic) => Serialize(ref sbTemp, o, lt, config, ic), foundType, ref hasSkiped);
                if (hasSkiped) continue;
                sb = sbTemp;
                return;
            }
            throw new JsonReflectionException();
        } catch (Exception inner) {
            throw linkedType.Previous == null ? new JsonException($"Serialization has been failed (Type call sequence: {string.Join(" -> ", linkedType.Select(t => StringType(t, true)))})", inner) : inner;
        }
    }

    public static bool TrySerialize(object? obj, JsonSerialization.Config config, [NotNullWhen(true)] out string? result) {
        try {
            result = Serialize(obj, config);
            return true;
        } catch {
            result = null;
            return false;
        }
    }

    public static bool TrySerialize(object? obj, [NotNullWhen(true)] out string? result) => TrySerialize(obj, new(), out result);

    public static object? Deserialize(string json, Type type, JsonDeserialization.Config config) {
        if (string.IsNullOrWhiteSpace(json)) return default;
        JsonReadBuffer buffer = new(json);
        return Deserialize(ref buffer, new(type, null), config);
    }
    public static object? Deserialize(string json, Type type) => Deserialize(json, type, new());
    public static T? Deserialize<T>(string json, JsonDeserialization.Config config) => (T?)Deserialize(json, typeof(T), config);
    public static T? Deserialize<T>(string json) => Deserialize<T>(json, new());

    public static bool TryDeserialize(string json, Type type, JsonDeserialization.Config config, out object? result) {
        try {
            result = Deserialize(json, type, config);
            return true;
        } catch {
            result = default;
            return false;
        }
    }

    public static bool TryDeserialize<T>(string json, JsonDeserialization.Config config, out T? result) {
        bool valid = TryDeserialize(json, typeof(T), config, out object? raw);
        result = (T?)raw;
        return valid;
    }

    public static bool TryDeserialize(string json, Type type, out object? result) => TryDeserialize(json, type, new(), out result);
    public static bool TryDeserialize<T>(string json, out T? result) => TryDeserialize(json, new(), out result);


    private static object? Deserialize(ref JsonReadBuffer buffer, LinkedType linkedType, JsonDeserialization.Config config) {
        var type = linkedType.Value;
        var nullableValue = Nullable.GetUnderlyingType(type);
        if (buffer.NextIsNull()) {
            if (type.IsClass || nullableValue != null) return null;
            throw new JsonReflectionException($"This object cannot be NULL");
        }
        if (nullableValue != null) return Deserialize(ref buffer, new(nullableValue, linkedType.Previous), config);

        try {
            if (type == typeof(object)) {
                foreach (var t in config.DynamicAvalableTypes) {
                    try {
                        var tempBuffer = buffer;
                        var result = Deserialize(ref tempBuffer, new(t, null), config);
                        buffer = tempBuffer;
                        return result;
                    } catch { continue; }
                }
                throw new JsonReflectionException("Invalid dynamic type cast");
            }

            if (type.IsInterface || type.IsAbstract) throw new JsonReflectionException("The type cannot be ethier an interface or an abstract class");
            foreach (var d in config.Pack) {
                Type? foundType = null;
                if (!d.TypePredicate(type, ref foundType)) continue;
                var tempBuffer = buffer;
                var result = d.Callback.Invoke(ref tempBuffer, linkedType, config, (ref buf, lt) => Deserialize(ref buf, lt, config), foundType);
                if (result == null) continue;
                buffer = tempBuffer;
                return result;
            }
            throw new JsonReflectionException();

        } catch (Exception inner) {
            throw linkedType.Previous == null ? new JsonException($"Deserialization has been failed (Type call sequence: {string.Join(" -> ", linkedType.Select(t => StringType(t, true)))})", inner) : inner;
        }
    }
}
