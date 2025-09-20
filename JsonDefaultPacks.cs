using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace SonicD5.Json;

public sealed partial class JsonSerialization {
    private static readonly List<JsonSerialization> DefaultPack = [
       new() {
            TypePredicate = (t, ref _) => t.IsAssignableTo(typeof(string)),
            JsonTypes = JsonTypes.String,
            Callback = (ref sb, obj, _, config, _, _, _, ref _) => sb.Append($"\"{obj.ToString().Escape(config.UnicodeEscape)}\""),
        },
        new() {
            TypePredicate = (t, ref _) => t.IsPrimitive || t == typeof(decimal),
            JsonTypes = JsonTypes.Number | JsonTypes.Boolean,
            Callback = (ref sb, obj, _, _, _, _, _, ref _) => sb.Append(obj switch {
                float f => f.ToString(CultureInfo.InvariantCulture),
                double d => d.ToString(CultureInfo.InvariantCulture),
                decimal d => d.ToString(CultureInfo.InvariantCulture),
                bool b => b ? "true" : "false",
                _ => obj.ToString()
            })
        },
        new() {
            TypePredicate = (t, ref _) => t.IsEnum,
            JsonTypes = JsonTypes.String,
            Callback = (ref sb, obj, linkedType, config, _, _, _, ref _) => {
                var t = linkedType.Value;
                var field = t.GetFields().First(f => f.Name == Enum.GetName(t, obj));

                sb.Append(field.IsDefined(typeof(JsonSerializableAttribute)) ? field.GetCustomAttribute<JsonSerializableAttribute>()!.Name :
                field.Name.ConvertCase(config.NamingConvetion));
            }
        },
        new() {
            TypePredicate = (t, ref _) => t.IsArray,
            JsonTypes = JsonTypes.Array,
            Callback = (ref sb, obj, lt, config, ic, inv, _, ref _) => SerializeArray(sb, obj, lt, config, ic, inv),
        },
        new() {
            TypePredicate = (t, ref _) => t.GetInterfaces().Any(i => {
                if (!i.IsGenericType) return false;
                var genericDef = i.GetGenericTypeDefinition();
                return genericDef == typeof(ICollection<>) || genericDef == typeof(IReadOnlyCollection<>);
            }),
            JsonTypes = JsonTypes.Array,
            Callback = (ref sb, obj, linkedType, config, ic, invoker, _, ref _) => SerializeCollection(sb, (IEnumerable)obj, new(linkedType.Value.GetInterfaces().First(i => {
                    if (!i.IsGenericType) return false;
                    var genericDef = i.GetGenericTypeDefinition();
                    return genericDef == typeof(ICollection<>) || genericDef == typeof(IReadOnlyCollection<>);
                }).GetGenericArguments()[0], linkedType), config, ic, invoker)
        },
        new() {
            TypePredicate = (t, ref _) => t.IsValueType || t.IsClass,
            JsonTypes = JsonTypes.Object,
            Callback = (ref sb, obj, lt, config, ic, inv, _, ref _) => SerializeObject(sb, obj, lt, config, ic, inv)
        },
    ];

    private static void SerializeObject(StringBuilder sb, object obj, LinkedType linkedType, Config config, int indentCount, Invoker invoker) {
        var members = linkedType.Value.GetFieldsAndProperties();

        if (members.Length == 0) {
            sb.Append("{}");
            return;
        }

        bool isNotFirst = false;
        bool hasIndent = config.Indent != "";
        int newIndentCount = hasIndent ? indentCount + 1 : 0;
        string quoute = config.ObjectFieldConvention == ObjectFieldConventions.NoQuote ? "" : ((char)config.ObjectFieldConvention).ToString();

        if (indentCount > 0) sb.AppendLine();
        sb.Append(config.Indent.Repeat(indentCount));
        sb.Append('{');

        foreach (var m in members) {
            if (m.IsDefined(typeof(JsonSerializationIgnoreAttribute))) continue;

            var f = m as FieldInfo;
            if (f != null && (f.IsSecurityCritical || (config.IgnoreNullValues && f.GetValue(obj) == null))) continue;

            var p = m as PropertyInfo;
            if (p != null && config.IgnoreNullValues && p.GetValue(obj) == null) continue;

            if (isNotFirst) {
                sb.Append(',');
                if (hasIndent)
                    sb.AppendLine();
            } else {
                if (hasIndent)
                    sb.AppendLine();
                isNotFirst = true;
            }
            sb.Append($"{config.Indent.Repeat(newIndentCount)}" +
                $"{quoute}{(m.IsDefined(typeof(JsonSerializableAttribute)) ? m.GetCustomAttribute<JsonSerializableAttribute>()!.Name : m.Name.ConvertCase(config.NamingConvetion))}{quoute}:" +
                $"{(hasIndent ? ' ' : "")}");

            if (f != null) invoker.Invoke(f.GetValue(obj), new(f.FieldType, linkedType), newIndentCount);
            else if (p != null) invoker.Invoke(p.GetValue(obj), new(p.PropertyType, linkedType), newIndentCount);
        }
        if (isNotFirst) {
            if (hasIndent)
                sb.AppendLine();
            sb.Append(config.Indent.Repeat(indentCount));
        }
        sb.Append('}');
    }

    private static void SerializeArray(StringBuilder sb, object obj, LinkedType linkedType, Config config, int indentCount, Invoker invoker) {
        var array = (Array)obj;
        if (array.Length == 0) {
            sb.Append("[]");
            return;
        }
        var eType = linkedType.Value.GetElementType()!;
        bool isObjElement = eType == typeof(object);
        bool isNotFirst = false;
        bool hasIndent = config.Indent != "";
        int newIndentCount = hasIndent ? indentCount + 1 : 0;

        if (indentCount > 0)
            sb.AppendLine();
        sb.Append(config.Indent.Repeat(indentCount));
        sb.Append('[');

        foreach (var value in array) {
            if (isNotFirst) {
                sb.Append(',');
                if (hasIndent)
                    sb.AppendLine();
            } else {
                if (hasIndent)
                    sb.AppendLine();
                isNotFirst = true;
            }
            sb.Append(config.Indent.Repeat(newIndentCount));
            invoker.Invoke(value, new(!isObjElement ? eType : (value == null ? typeof(object) : value.GetType()), linkedType), newIndentCount);
        }
        if (isNotFirst) {
            if (hasIndent)
                sb.AppendLine();
            sb.Append(config.Indent.Repeat(indentCount));
        }
        sb.Append(']');
    }

	private static void SerializeCollection(StringBuilder sb, IEnumerable collection, LinkedType linkedEType, Config config, int indentCount, Invoker invoker) {
        if ((collection is ICollection ic && ic.Count == 0) || !collection.GetEnumerator().MoveNext()) {
            sb.Append("[]");
            return;
        }
        bool isObjElement = linkedEType.Value == typeof(object);
        bool isNotFirst = false;
        bool hasIndent = config.Indent != "";
        int newIndentCount = hasIndent ? indentCount + 1 : 0;

        if (indentCount > 0) sb.AppendLine();
        sb.Append(config.Indent.Repeat(indentCount));
        sb.Append('[');

        foreach (var item in collection) {
            if (config.IgnoreNullValues && item == null)
                continue;
            if (isNotFirst) {
                sb.Append(',');
                if (hasIndent)
                    sb.AppendLine();
            } else {
                if (hasIndent)
                    sb.AppendLine();
                isNotFirst = true;
            }
            sb.Append(config.Indent.Repeat(newIndentCount));
            invoker.Invoke(item, new(!isObjElement ? linkedEType.Value : (item == null ? typeof(object) : item.GetType()), linkedEType.Previous), newIndentCount);
        }
        if (isNotFirst) {
            if (hasIndent)
                sb.AppendLine();
            sb.Append(config.Indent.Repeat(indentCount));
        }
        sb.Append(']');
    }
}

public sealed partial class JsonDeserialization {
    [GeneratedRegex(@"^[-+]?0[xX]")]
    private static partial Regex HexRegex();
    private static T HexApplier<T>(T number, bool isNegative) where T : ISignedNumber<T> => isNegative ? -number : number;

    private static readonly List<JsonDeserialization> DefaultPack = [
        new() {
            TypePredicate = (t, ref _) => t == typeof(string),
            JsonTypes = JsonTypes.String,
            Callback = (ref buffer, _, _, _, _) => buffer.ReadString()
        },
        new() {
            TypePredicate = (t, ref _) => t.IsPrimitive || t == typeof(decimal),
            JsonTypes = JsonTypes.Number | JsonTypes.Boolean,
            Callback = (ref buffer, linkedType, _, _, _) => {
                var type = linkedType.Value;
                string raw = buffer.ReadPrimitive();
                bool isHex = HexRegex().IsMatch(raw);
                bool isNegative = raw.StartsWith('-');
                if (isHex && isNegative && type.GetInterfaces().Contains(typeof(ISignedNumber<>).MakeGenericType(type))) raw = raw.Replace("-", "");
                return Type.GetTypeCode(type) switch {
                    TypeCode.Boolean => bool.Parse(raw),
                    TypeCode.Single => float.Parse(raw, CultureInfo.InvariantCulture),
                    TypeCode.Double => double.Parse(raw, CultureInfo.InvariantCulture),
                    TypeCode.Decimal => decimal.Parse(raw, CultureInfo.InvariantCulture),

                    TypeCode.Byte => isHex ? Convert.ToByte(raw, 16) : byte.Parse(raw),
                    TypeCode.Int16 => isHex ? HexApplier(Convert.ToInt16(raw, 16), isNegative) : short.Parse(raw),
                    TypeCode.Int32 => isHex ? HexApplier(Convert.ToInt32(raw, 16), isNegative) : int.Parse(raw),
                    TypeCode.Int64 => isHex ? HexApplier(Convert.ToInt64(raw, 16), isNegative) : long.Parse(raw),
                    TypeCode.SByte => isHex ? HexApplier(Convert.ToSByte(raw, 16), isNegative) : sbyte.Parse(raw),
                    TypeCode.UInt16 => isHex ? Convert.ToUInt16(raw, 16) : ushort.Parse(raw),
                    TypeCode.UInt32 => isHex ? Convert.ToUInt32(raw, 16) : uint.Parse(raw),
                    TypeCode.UInt64 => isHex ? Convert.ToUInt64(raw, 16) : ulong.Parse(raw),
                    _ => default!
                };
            }
        },
        new() {
            TypePredicate = (t, ref _) => t.IsEnum,
            JsonTypes = JsonTypes.String,
            Callback = (ref buffer, linkedType, config, _, _) => {
                var type = linkedType.Value;
                var fields = type.GetFields();
                string? raw = buffer.ReadString();

                foreach (var field in fields) {
                    if ((field.IsDefined(typeof(JsonSerializableAttribute)) && field.GetCustomAttribute<JsonSerializableAttribute>()!.Name == raw) ||
                        (config.RequiredNamingEquality ? field.Name == raw : Enum.GetValues<NamingConvetions>().Any(v => field.Name.ConvertCase(v) == raw)))
                        return field.GetValue(null)!;
                }
                throw new JsonReflectionException($"\"{type.Name}\" enum hasn't \"{raw}\" value");
            }
        },
        new() {
            TypePredicate = (t, ref _) => t.IsArray,
            JsonTypes = JsonTypes.Array,
            Callback = (ref buffer, linkedType, _, invoker, _) => DeserializeArray(ref buffer, linkedType, invoker),
        },
        new() {
            TypePredicate = (t, ref icType) => { 
                icType = t.GetInterfaces().FirstOrDefault(i => {
				    if (!i.IsGenericType) return false;
				    var genericDef = i.GetGenericTypeDefinition();
				    return genericDef == typeof(ICollection<>) || genericDef == typeof(IReadOnlyCollection<>);
			    });
                return icType != null;
            },
            JsonTypes = JsonTypes.Array,
            Callback = (ref buffer, linkedType, _, invoker, icType) => {
                var eType = icType.GetGenericArguments()[0];
                return DeserializeCollection(ref buffer, new(eType, linkedType), !linkedType.Value.GetInterfaces().Contains(typeof(ICollection<>).MakeGenericType(eType)), invoker);
            }
        },
        new() {
            TypePredicate = (t, ref _) => t.IsValueType || t.IsClass,
            JsonTypes = JsonTypes.Object,
            Callback = (ref buffer, linkedType, config, invoker, _) => DeserializeObject(ref buffer, linkedType, config, invoker),
        }
    ];

    private static Array DeserializeArray(ref JsonReadBuffer buffer, LinkedType linkedType, Invoker invoker) {
        var next = buffer.Next();

        if (next != JsonReadBuffer.NextType.Array) throw new JsonSyntaxException($"The object start must be '['", buffer);
        var eType = linkedType.Value.GetElementType()!;
        next = buffer.NextBlock();
        if (next == JsonReadBuffer.NextType.EndArray) return Array.CreateInstance(eType, 0);
        if (next == JsonReadBuffer.NextType.Punctuation) throw new JsonSyntaxException($"Punctuation must not be here", buffer);
        List<object?> elems = [];

        while (true) {
            if (next == JsonReadBuffer.NextType.Undefined) {
                elems.Add(invoker.Invoke(ref buffer, new(eType, linkedType)));
                next = buffer.NextBlock();
            }
            if (next == JsonReadBuffer.NextType.Punctuation) {
                next = buffer.NextBlock();
                if (next == JsonReadBuffer.NextType.Punctuation) throw new JsonSyntaxException($"Punctuation must not be here", buffer);
                if (next == JsonReadBuffer.NextType.EndArray) break;
                continue;
            }
            if (next == JsonReadBuffer.NextType.EndArray) break;
            throw new JsonSyntaxException(buffer);
        }

        var array = Array.CreateInstance(eType, elems.Count);
        for (int i = 0; i < array.Length; i++) array.SetValue(elems[i], i);
        return array;
    }

    private static object DeserializeCollection(ref JsonReadBuffer buffer, LinkedType linkedEtype, bool readOnly, Invoker invoker) {
        var next = buffer.Next();

        if (next != JsonReadBuffer.NextType.Array) throw new JsonSyntaxException($"The object start must be '['", buffer);
        var type = linkedEtype.Previous!.Value;
        next = buffer.NextBlock();
        if (next == JsonReadBuffer.NextType.EndArray) return Activator.CreateInstance(type)!;
        if (next == JsonReadBuffer.NextType.Punctuation) throw new JsonSyntaxException($"Punctuation must not be here", buffer);

        var eType = linkedEtype.Value;
        var raw = Activator.CreateInstance(readOnly ? typeof(List<>).MakeGenericType(eType) : type)!;
        var addMethod = raw.GetType().GetMethod("Add")!;

        while (true) {
            if (next == JsonReadBuffer.NextType.Undefined) {
                addMethod.Invoke(raw, [invoker.Invoke(ref buffer, linkedEtype)]);
                next = buffer.NextBlock();
            }
            if (next == JsonReadBuffer.NextType.Punctuation) {
                next = buffer.NextBlock();
                if (next == JsonReadBuffer.NextType.Punctuation)
                    throw new JsonSyntaxException($"Punctuation must not be here", buffer);
                if (next == JsonReadBuffer.NextType.EndArray)
                    break;
                continue;
            }
            if (next == JsonReadBuffer.NextType.EndArray)
                break;
            throw new JsonSyntaxException(buffer);
        }

        if (readOnly) {
            var ctor = type.GetConstructor(BindingFlags.Public | BindingFlags.Instance, [typeof(IEnumerable<>).MakeGenericType(eType)]) ??
                throw new JsonReflectionException($"The \"{JsonSerializer.StringType(typeof(IReadOnlyCollection<>), true)}\" implementations must have the public collection init constructor to be deserialized");
            return ctor.Invoke([raw]);
        }
        return raw;
    }

    private static object DeserializeObject(ref JsonReadBuffer buffer, LinkedType linkedType, Config config, Invoker invoker) {
        var next = buffer.Next();
        if (next != JsonReadBuffer.NextType.Block) throw new JsonSyntaxException("The object start must be '{'", buffer);
        object obj = Activator.CreateInstance(linkedType.Value)!;
        next = buffer.NextBlock();
        if (next == JsonReadBuffer.NextType.EndBlock) return obj;
        if (next == JsonReadBuffer.NextType.Punctuation) throw new JsonSyntaxException($"Punctuation must not be here", buffer);

        var members = linkedType.Value.GetFieldsAndProperties();

        while (true) {
            if (next == JsonReadBuffer.NextType.Undefined) {
                string name = buffer.ReadObjectFieldName();
                var m = members.FirstOrDefault(m => m.IsDefined(typeof(JsonSerializableAttribute)) ? m.GetCustomAttribute<JsonSerializableAttribute>()!.Name == name : (config.RequiredNamingEquality ? m.Name == name : Enum.GetValues<NamingConvetions>().Any(v => m.Name.ConvertCase(v) == name)));
                if (m == null) buffer.SkipObjectField();
                else if (m is PropertyInfo p) p.SetValue(obj, invoker.Invoke(ref buffer, new(p.PropertyType, linkedType)));
                else if (m is FieldInfo f) f.SetValue(obj, invoker.Invoke(ref buffer, new(f.FieldType, linkedType)));
                next = buffer.NextBlock();
            }
            if (next == JsonReadBuffer.NextType.Punctuation) {
                next = buffer.NextBlock();
                if (next == JsonReadBuffer.NextType.Punctuation) throw new JsonSyntaxException($"Punctuation must not be here", buffer);
                if (next == JsonReadBuffer.NextType.EndBlock) break;
                continue;
            }
            if (next == JsonReadBuffer.NextType.EndBlock) break;
            throw new JsonSyntaxException(buffer);
        }
        return obj;
    }
}