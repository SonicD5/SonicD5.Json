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
        if (nullableValue != null) obj = nullableValue;
        try {
            foreach (var lib in config.LibaryPack) {
                if (!lib.CheckType(linkedType.Value, out var foundType)) continue;

                JsonSerialization.CallbackContext ctx = new() {
                    Result = sb.Copy(),
                    Object = obj,
                    Type = linkedType,
                    Config = config,
                    IndentCount = indentCount,
#pragma warning disable CS8601
					FoundType = foundType,
#pragma warning restore CS8601
				};
                ctx.Invoker = (o, lt, ic) => {
                    StringBuilder result = ctx.Result;
                    Serialize(ref result, o, lt, config, ic);
                    ctx.Result = result;
                };
				lib.SCallback.Invoke(ref ctx);
                if (ctx.HasSkiped) continue;
                sb = ctx.Result;
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
                        var tempBuffer = buffer.Copy();
                        var result = Deserialize(ref tempBuffer, new(t, null), config);
                        buffer = tempBuffer;
                        return result;
                    } catch { continue; }
                }
                throw new JsonReflectionException("Invalid dynamic type cast");
            }
            foreach (var lib in config.LibaryPack) {
                if (!lib.CheckType(type, out var foundType)) continue;
                JsonDeserialization.CallbackContext ctx = new() {
                    Buffer = buffer.Copy(),
                    Type = linkedType,
                    Config = config,
#pragma warning disable CS8601
					FoundType = foundType,
#pragma warning restore CS8601
					Invoker = (ref buffer, lt) => Deserialize(ref buffer, lt, config)
                };
                var result = lib.DCallback.Invoke(ref ctx);
                if (result == null) continue;
                buffer = ctx.Buffer;
                return result;
            }
            throw new JsonReflectionException();

        } catch (Exception inner) {
            throw linkedType.Previous == null ? new JsonException($"Deserialization has been failed (Type call sequence: {string.Join(" -> ", linkedType.Select(t => StringType(t, true)))})", inner) : inner;
        }
    }
}
