using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace SonicD5.Json;

public sealed partial class JsonLibary {
    [GeneratedRegex(@"^[-+]?0[xX]")]
    private static partial Regex HexRegex();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static T HexApplier<T>(T number, bool isNegative) where T : ISignedNumber<T> => isNegative ? -number : number;

    private static readonly List<JsonLibary> DefaultLibaryPack = [
        new() {
            TPredicate = (ref ctx) => ctx.Type.IsAssignableTo(typeof(string)),
            JsonTypes = JsonTypes.String,
            SCallback = (ref ctx) => ctx.Result.Append($"\"{ctx.Object.ToString().Escape(ctx.Config.UnicodeEscape)}\""),
            DCallback = (ref ctx) => ctx.Buffer.ReadString()
        },
        new() {
            TPredicate = (ref ctx) => ctx.Type.IsPrimitive || ctx.Type == typeof(decimal),
            JsonTypes = JsonTypes.Number | JsonTypes.Boolean,
            SCallback = (ref ctx) => 
            ctx.Result.Append(ctx.Object switch {
                float f => f.ToString(CultureInfo.InvariantCulture),
                double d => d.ToString(CultureInfo.InvariantCulture),
                decimal d => d.ToString(CultureInfo.InvariantCulture),
                bool b => b ? "true" : "false",
                _ => ctx.Object.ToString()
            }),
            DCallback = (ref ctx) => {
                var type = ctx.Type.Value;
                string raw = ctx.Buffer.ReadPrimitive();
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
            TPredicate = (ref ctx) => ctx.Type.IsEnum,
            JsonTypes = JsonTypes.String,
            SCallback = (ref ctx) => {
                var type = ctx.Type.Value;
                var obj = ctx.Object;
                var field = type.GetFields().First(f => f.Name == Enum.GetName(type, obj));

                ctx.Result.Append(field.IsDefined(typeof(JsonSerializableAttribute)) ? field.GetCustomAttribute<JsonSerializableAttribute>()!.Name :
                field.Name.ConvertCase(ctx.Config.NamingConvetion));
            },
            DCallback = (ref ctx) => {
                var type = ctx.Type.Value;
                var fields = type.GetFields();
                string? raw = ctx.Buffer.ReadString();

                foreach (var field in fields) {
                    if ((field.IsDefined(typeof(JsonSerializableAttribute)) && field.GetCustomAttribute<JsonSerializableAttribute>()!.Name == raw) ||
                        (ctx.Config.RequiredNamingEquality ? field.Name == raw : Enum.GetValues<NamingConvetions>().Any(v => field.Name.ConvertCase(v) == raw)))
                        return field.GetValue(null)!;
                }
                throw new JsonReflectionException($"\"{type.Name}\" enum hasn't \"{raw}\" value");
            }
        },
        new() {
            TPredicate = (ref ctx) => ctx.Type.IsArray,
            JsonTypes = JsonTypes.Array,
            SCallback = SerializeArray,
            DCallback = DeserializeArray,
        },
        new() {
            TPredicate = (ref ctx) => {
                ctx.FoundType = ctx.Type.GetInterfaces().FirstOrDefault(i => {
                    if (!i.IsGenericType) return false;
                    var genericDef = i.GetGenericTypeDefinition();
                    return genericDef == typeof(ICollection<>) || genericDef == typeof(IReadOnlyCollection<>);
                });
                return ctx.FoundType != null;
            },
            JsonTypes = JsonTypes.Array,
            SCallback = SerializeCollection,
            DCallback = (ref ctx) => {
                var eType = ctx.FoundType.GetGenericArguments()[0];
                return DeserializeCollection(ref ctx, !ctx.Type.Value.GetInterfaces().Contains(typeof(ICollection<>).MakeGenericType(eType)));
            }
        },
        new() {
            TPredicate = (ref ctx) => 
            ctx.Type.IsGenericType && ctx.Type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>),
            JsonTypes = JsonTypes.Object,
            SCallback = (ref ctx) => SerializeObject(ref ctx, ctx.Type.Value.GetFieldsAndProperties(m => m.MemberType == MemberTypes.Field, BindingFlags.NonPublic)),
            DCallback = (ref ctx) => DeserializeObject(ref ctx, ctx.Type.Value.GetFieldsAndProperties(m => m.MemberType == MemberTypes.Field, BindingFlags.NonPublic))
        },
        new() {
            TPredicate = (ref ctx) => ctx.Type.IsValueType || ctx.Type.IsClass,
            JsonTypes = JsonTypes.Object,
            SCallback = (ref ctx) => SerializeObject(ref ctx, ctx.Type.Value.GetFieldsAndProperties(DefaultMemberFilter)),
            DCallback = (ref ctx) => DeserializeObject(ref ctx, ctx.Type.Value.GetFieldsAndProperties(DefaultMemberFilter))
        },
    ];

    public static bool DefaultMemberFilter(MemberInfo m) => m.MemberType == MemberTypes.Field || (m is PropertyInfo p && p.CanWrite && p.CanRead);
    public static void SerializeObject(ref JsonSerialization.CallbackContext ctx, MemberInfo[] members) {
        if (members.Length == 0) {
            ctx.Result.Append("{}");
            return;
        }

        bool isNotFirst = false;
        bool hasIndent = ctx.Config.Indent != "";

        int newIndentCount = 0;
        if (ctx.IndentCount < ctx.Config.MinNestLevel) newIndentCount = ctx.IndentCount + 1;
        else if (hasIndent) foreach (var m in members) {
			Type? type = null;
			if (m is FieldInfo f) type = f.FieldType;
			else if (m is PropertyInfo p) type = p.PropertyType;
			if (type!.HasJsonTypes(ctx.Config.LibaryPack, JsonTypes.Array, JsonTypes.Object)) {
				newIndentCount = ctx.IndentCount + 1;
                break;
			}
		};
        bool notNested = newIndentCount != 0;
		string quoute = ctx.Config.ObjectFieldConvention == ObjectFieldConventions.NoQuote ? "" : ((char)ctx.Config.ObjectFieldConvention).ToString();

		if (!ctx.Type.Value.HasJsonTypes(ctx.Config.LibaryPack, JsonTypes.Object) && notNested) 
            ctx.Result.Append(ctx.Config.Indent.Repeat(ctx.IndentCount - 1));
		ctx.Result.Append('{');

        foreach (var m in members) {
            if (m.IsDefined(typeof(JsonSerializationIgnoreAttribute))) continue;

            var f = m as FieldInfo;
            if (f != null && (f.IsSecuritySafeCritical || (ctx.Config.IgnoreNullValues && f.GetValue(ctx.Object) == null))) continue;

            var p = m as PropertyInfo;

            var pValue = p?.GetValue(ctx.Object);
			if (p != null && ctx.Config.IgnoreNullValues && pValue == null) continue;

			if (isNotFirst) {
				ctx.Result.Append(',');
				if (notNested) ctx.Result.AppendLine();
				else if (hasIndent) ctx.Result.Append(' ');
			} else {
				if (notNested)
					ctx.Result.AppendLine();
				isNotFirst = true;
			}
			ctx.Result.Append(ctx.Config.Indent.Repeat(newIndentCount));
            ctx.Result.Append(quoute);
            ctx.Result.Append(m.IsDefined(typeof(JsonSerializableAttribute)) ? m.GetCustomAttribute<JsonSerializableAttribute>()!.Name : m.Name.ConvertCase(ctx.Config.NamingConvetion));
			ctx.Result.Append($"{quoute}:");
            if (hasIndent) ctx.Result.Append(' ');
			if (f != null) ctx.Invoker.Invoke(f.GetValue(ctx.Object), new(f.FieldType, ctx.Type), newIndentCount);
            else if (p != null) ctx.Invoker.Invoke(pValue, new(p.PropertyType, ctx.Type), newIndentCount);
        }
        if (notNested) { 
            ctx.Result.AppendLine();
			ctx.Result.Append(ctx.Config.Indent.Repeat(ctx.IndentCount));
		}
        ctx.Result.Append('}');
    }

    public static void SerializeArray(ref JsonSerialization.CallbackContext ctx) {
        var array = (Array)ctx.Object;
        if (array.Length == 0) {
            ctx.Result.Append("[]");
            return;
        }
        bool isNotFirst = false;
        bool hasIndent = ctx.Config.Indent != "";

		int newIndentCount = 0;
		if (ctx.IndentCount < ctx.Config.MinNestLevel) newIndentCount = ctx.IndentCount + 1;
		else if (hasIndent) foreach (var v in array) {
			if (v != null && v.GetType().HasJsonTypes(ctx.Config.LibaryPack, JsonTypes.Array, JsonTypes.Object)) {
				newIndentCount = ctx.IndentCount + 1;
				break;
			}
		};
		bool notNested = newIndentCount != 0;

		if (!ctx.Type.Value.HasJsonTypes(ctx.Config.LibaryPack, JsonTypes.Object) && notNested) 
            ctx.Result.Append(ctx.Config.Indent.Repeat(ctx.IndentCount - 1));
		ctx.Result.Append('[');

		foreach (var value in array) {
            if (isNotFirst) {
                ctx.Result.Append(',');
                if (notNested) ctx.Result.AppendLine();
                else if (hasIndent) ctx.Result.Append(' ');
			} else {
                if (notNested) ctx.Result.AppendLine();
                isNotFirst = true;
            }
            ctx.Result.Append(ctx.Config.Indent.Repeat(newIndentCount));
            ctx.Invoker.Invoke(value, new(value == null ? typeof(object) : value.GetType(), ctx.Type), newIndentCount);
        }
        if (notNested) { 
            ctx.Result.AppendLine();
			ctx.Result.Append(ctx.Config.Indent.Repeat(ctx.IndentCount));
		}
        ctx.Result.Append(']');
    }

	public static void SerializeCollection(ref JsonSerialization.CallbackContext ctx) {
        var collection = (IEnumerable)ctx.Object;
        if ((collection is ICollection ic && ic.Count == 0) || !collection.GetEnumerator().MoveNext()) {
            ctx.Result.Append("[]");
            return;
        }
        bool isNotFirst = false;
        bool hasIndent = ctx.Config.Indent != "";

		int newIndentCount = 0;
		if (ctx.IndentCount < ctx.Config.MinNestLevel) newIndentCount = ctx.IndentCount + 1;
		else if (hasIndent) foreach (var i in collection) {
			if (i != null && i.GetType().HasJsonTypes(ctx.Config.LibaryPack, JsonTypes.Array, JsonTypes.Object)) {
				newIndentCount = ctx.IndentCount + 1;
				break;
			}
		};
		bool notNested = newIndentCount != 0;

		if (!ctx.Type.Value.HasJsonTypes(ctx.Config.LibaryPack, JsonTypes.Object) && notNested)
			ctx.Result.Append(ctx.Config.Indent.Repeat(ctx.IndentCount - 1));
		ctx.Result.Append('[');

		foreach (var item in collection) {
            if (ctx.Config.IgnoreNullValues && item == null) continue;
			if (isNotFirst) {
				ctx.Result.Append(',');
				if (notNested) ctx.Result.AppendLine();
				else if (hasIndent) ctx.Result.Append(' ');
			} else {
				if (notNested) ctx.Result.AppendLine();
				isNotFirst = true;
			}
			ctx.Result.Append(ctx.Config.Indent.Repeat(newIndentCount));
            ctx.Invoker.Invoke(item, new(item == null ? typeof(object) : item.GetType(), ctx.Type), newIndentCount);
        }
		if (notNested) {
			ctx.Result.AppendLine();
			ctx.Result.Append(ctx.Config.Indent.Repeat(ctx.IndentCount));
		}
		ctx.Result.Append(']');
	}


    public static Array DeserializeArray(ref JsonDeserialization.CallbackContext ctx) {
        var next = ctx.Buffer.Next();

        if (next != JsonReadBuffer.NextType.Array)
            throw new JsonSyntaxException($"The object start must be '['", ctx.Buffer);
        var eType = ctx.Type.Value.GetElementType()!;
        next = ctx.Buffer.NextBlock();
        if (next == JsonReadBuffer.NextType.EndArray)
            return Array.CreateInstance(eType, 0);
        if (next == JsonReadBuffer.NextType.Punctuation)
            throw new JsonSyntaxException($"Punctuation must not be here", ctx.Buffer);
        List<object?> elems = [];

        while (true) {
            if (next == JsonReadBuffer.NextType.Undefined) {
                var buffer = ctx.Buffer;
                elems.Add(ctx.Invoker.Invoke(ref buffer, new(eType, ctx.Type)));
                ctx.Buffer = buffer;
                next = ctx.Buffer.NextBlock();
            }
            if (next == JsonReadBuffer.NextType.Punctuation) {
                next = ctx.Buffer.NextBlock();
                if (next == JsonReadBuffer.NextType.Punctuation)
                    throw new JsonSyntaxException($"Punctuation must not be here", ctx.Buffer);
                if (next == JsonReadBuffer.NextType.EndArray)
                    break;
                continue;
            }
            if (next == JsonReadBuffer.NextType.EndArray) break;
            throw new JsonSyntaxException(ctx.Buffer);
        }

        var array = Array.CreateInstance(eType, elems.Count);
        for (int i = 0; i < array.Length; i++) array.SetValue(elems[i], i);
        return array;
    }

    public static object DeserializeCollection(ref JsonDeserialization.CallbackContext ctx, bool readOnly) {
        var next = ctx.Buffer.Next();
        LinkedType linkedEType = new(ctx.FoundType.GetGenericArguments()[0], ctx.Type);

        if (next != JsonReadBuffer.NextType.Array)
            throw new JsonSyntaxException($"The object start must be '['", ctx.Buffer);
        var type = linkedEType.Previous!.Value;
        next = ctx.Buffer.NextBlock();
        if (next == JsonReadBuffer.NextType.EndArray)
            return Activator.CreateInstance(type)!;
        if (next == JsonReadBuffer.NextType.Punctuation)
            throw new JsonSyntaxException($"Punctuation must not be here", ctx.Buffer);

        var eType = linkedEType.Value;
        var raw = Activator.CreateInstance(readOnly ? typeof(List<>).MakeGenericType(eType) : type)!;
        var addMethod = (readOnly ? raw.GetType() : ctx.FoundType).GetMethod("Add")!;

        while (true) {
            if (next == JsonReadBuffer.NextType.Undefined) {
                var buffer = ctx.Buffer.Copy();
                addMethod.Invoke(raw, [ctx.Invoker.Invoke(ref buffer, linkedEType)]);
                ctx.Buffer = buffer;
                next = ctx.Buffer.NextBlock();
            }
            if (next == JsonReadBuffer.NextType.Punctuation) {
                next = ctx.Buffer.NextBlock();
                if (next == JsonReadBuffer.NextType.Punctuation)
                    throw new JsonSyntaxException($"Punctuation must not be here", ctx.Buffer);
                if (next == JsonReadBuffer.NextType.EndArray)
                    break;
                continue;
            }
            if (next == JsonReadBuffer.NextType.EndArray)
                break;
            throw new JsonSyntaxException(ctx.Buffer);
        }

        if (readOnly) {
            var ctor = type.GetConstructor(BindingFlags.Public | BindingFlags.Instance, [typeof(IEnumerable<>).MakeGenericType(eType)]) ??
                throw new JsonReflectionException($"The \"{JsonSerializer.StringType(typeof(IReadOnlyCollection<>), true)}\" implementations must have the public collection init constructor to be deserialized");
            return ctor.Invoke([raw]);
        }
        return raw;
    }

    public static object DeserializeObject(ref JsonDeserialization.CallbackContext ctx, MemberInfo[] members) {
        var next = ctx.Buffer.Next();
        if (next != JsonReadBuffer.NextType.Block)
            throw new JsonSyntaxException("The object start must be '{'", ctx.Buffer);
        object obj = Activator.CreateInstance(ctx.Type.Value)!;
        next = ctx.Buffer.NextBlock();
        if (next == JsonReadBuffer.NextType.EndBlock)
            return obj;
        if (next == JsonReadBuffer.NextType.Punctuation)
            throw new JsonSyntaxException($"Punctuation must not be here", ctx.Buffer);

        while (true) {
            if (next == JsonReadBuffer.NextType.Undefined) {
                string name = ctx.Buffer.ReadObjectFieldName();
                bool namingEq = ctx.Config.RequiredNamingEquality;
                var m = members.FirstOrDefault(m => m.IsDefined(typeof(JsonSerializableAttribute)) ? m.GetCustomAttribute<JsonSerializableAttribute>()!.Name == name : (namingEq ? m.Name == name : Enum.GetValues<NamingConvetions>().Any(v => m.Name.ConvertCase(v) == name)));
                if (m == null) ctx.Buffer.SkipObjectField();
                else {
                    var buffer = ctx.Buffer.Copy();
					if (m is PropertyInfo p) p.SetValue(obj, ctx.Invoker.Invoke(ref buffer, new(p.PropertyType, ctx.Type)));
					else if (m is FieldInfo f) f.SetValue(obj, ctx.Invoker.Invoke(ref buffer, new(f.FieldType, ctx.Type)));
                    ctx.Buffer = buffer;
				}
                next = ctx.Buffer.NextBlock();
            }
            if (next == JsonReadBuffer.NextType.Punctuation) {
                next = ctx.Buffer.NextBlock();
                if (next == JsonReadBuffer.NextType.Punctuation)
                    throw new JsonSyntaxException($"Punctuation must not be here", ctx.Buffer);
                if (next == JsonReadBuffer.NextType.EndBlock)
                    break;
                continue;
            }
            if (next == JsonReadBuffer.NextType.EndBlock)
                break;
            throw new JsonSyntaxException(ctx.Buffer);
        }
        return obj;
    }
}