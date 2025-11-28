using System.Collections;
using System.Globalization;
using static SonicD5.Json.JsonSerializer;

namespace SonicD5.Json;

public static class JsonSystemLibaries {

	public static readonly JsonLibary CharLibary = new() { 
		TPredicate = (ref ctx) => ctx.Type == typeof(char),
		JsonTypes = JsonTypes.String,
		SCallback = (ref ctx) => { 
			if (!char.TryParse(ctx.Object.ToString().Escape(ctx.Config.UnicodeEscape), out char c)) {
				ctx.HasSkiped = true;
				return;
			}
            ctx.Result.Append($"\"{c}\"");
        },
        DCallback = (ref ctx) => {
            if (!char.TryParse(ctx.Buffer.ReadString(), out char result)) return null;
            return result;
        }
    };

	public static readonly Func<string?, JsonLibary> DateTimeLibary = format => new() {
		TPredicate = (ref ctx) => ctx.Type == typeof(DateTime),
		JsonTypes = JsonTypes.Number | JsonTypes.String,
		SCallback = (ref ctx) => { 
			DateTime dt = (DateTime)ctx.Object;
			ctx.Result.Append(format == null ? (dt - DateTime.UnixEpoch).TotalSeconds : $"\"{dt.ToString(format).Escape(ctx.Config.UnicodeEscape)}\""); 
		},
        DCallback = (ref ctx) => {
            var tempBuf = ctx.Buffer.Copy();

            if (tempBuf.TryReadPrimitive(out string rawUnix) && TryDeserialize(rawUnix, out long unix)) {
                ctx.Buffer = tempBuf;
                return DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
            }

            tempBuf = ctx.Buffer;
            if (tempBuf.TryReadString(out string iso) && DateTime.TryParse(iso, CultureInfo.InvariantCulture, out var result)) {
                ctx.Buffer = tempBuf;
                return result;
            }

            throw new JsonReflectionException();
        }
    };

    public static readonly JsonLibary TypeLibary = new() {
        TPredicate = (ref ctx) => ctx.Type.IsAssignableTo(typeof(Type)),
        JsonTypes = JsonTypes.String,
        SCallback = (ref ctx) => ctx.Result.Append($"\"{StringType((Type)ctx.Object)}\""),
        DCallback = (ref ctx) => throw new NotImplementedException()
    };


	public static readonly JsonLibary StringKeyDictionaryLibary = new() {
		TPredicate = (ref ctx) => {
			ctx.FoundType = ctx.Type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>));
			return ctx.FoundType != null;
		},
		JsonTypes = JsonTypes.Object,
		SCallback = (ref ctx) => {
            if (!ctx.FoundType.GetGenericArguments()[0].HasJsonTypes(ctx.Config.LibaryPack, JsonTypes.String)) {
                ctx.HasSkiped = true;
                return;
            }
            var dict = (IDictionary)ctx.Object;
            if (dict.Count == 0) {
                ctx.Result.Append("{}");
                return;
            }

            bool isNotFirst = false;
            bool hasIndent = ctx.Config.Indent != "";

			int newIndentCount = 0;
			if (ctx.IndentCount < ctx.Config.MinNestLevel) newIndentCount = ctx.IndentCount + 1;
			else if (hasIndent) foreach (var v in dict.Values) {
				if (v != null && v.GetType().HasJsonTypes(ctx.Config.LibaryPack, JsonTypes.Array, JsonTypes.Object)) {
					newIndentCount = ctx.IndentCount + 1;
					break;
				}
			};
			bool notNested = newIndentCount != 0;

			if (!ctx.Type.Value.HasJsonTypes(ctx.Config.LibaryPack, JsonTypes.Object) && notNested)
				ctx.Result.Append(ctx.Config.Indent.Repeat(ctx.IndentCount - 1));
			ctx.Result.Append('{');
			foreach (var key in dict.Keys) {
                var value = dict[key];
                if (ctx.Config.IgnoreNullValues && value == null) continue;
				if (isNotFirst) {
					ctx.Result.Append(',');
					if (notNested) ctx.Result.AppendLine();
					else if (hasIndent) ctx.Result.Append(' ');
				} else {
					if (notNested) ctx.Result.AppendLine();
					isNotFirst = true;
				}
				ctx.Result.Append(ctx.Config.Indent.Repeat(newIndentCount));
                ctx.Invoker.Invoke(key, new(key == null ? typeof(object) : key.GetType(), ctx.Type), newIndentCount);
                ctx.Result.Append(':');
				if (hasIndent) ctx.Result.Append(' ');
				ctx.Invoker.Invoke(value, new(value == null ? typeof(object) : value.GetType(), ctx.Type), newIndentCount);
            }
			if (notNested) {
				ctx.Result.AppendLine();
				ctx.Result.Append(ctx.Config.Indent.Repeat(ctx.IndentCount));
			}
			ctx.Result.Append('}');
		},
        DCallback = (ref ctx) => {
            var kType = ctx.FoundType.GetGenericArguments()[0];
			if (!kType.HasJsonTypes(ctx.Config.LibaryPack, JsonTypes.String)) return null;
            var type = ctx.Type.Value;
            var next = ctx.Buffer.Next();
            if (next != JsonReadBuffer.NextType.Block) throw new JsonSyntaxException(ctx.Buffer);
            next = ctx.Buffer.NextBlock();
            if (next == JsonReadBuffer.NextType.EndBlock) return Activator.CreateInstance(type);
            if (next != JsonReadBuffer.NextType.Undefined) throw new JsonSyntaxException(ctx.Buffer);

            var dict = (IDictionary)Activator.CreateInstance(type)!;
            LinkedType linkedVType = new(ctx.FoundType.GetGenericArguments()[1], ctx.Type);

            while (true) {
                if (next == JsonReadBuffer.NextType.Undefined) {
                    var buffer = ctx.Buffer.Copy();
                    JsonReadBuffer keyBuffer = new($"\"{buffer.ReadObjectFieldName()}\"");
                    dict.Add(ctx.Invoker.Invoke(ref keyBuffer, new(kType, ctx.Type))!, ctx.Invoker.Invoke(ref buffer, linkedVType));
                    ctx.Buffer = buffer;
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

            return dict;
        }
    };
}
