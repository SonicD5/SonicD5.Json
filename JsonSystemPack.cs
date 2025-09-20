using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Xml;
using static SonicD5.Json.JsonSerializer;

namespace SonicD5.Json;

public static class JsonSystemPack {

	public static readonly JsonSerialization CharSerialization = new() { 
		TypePredicate = (t, ref _) => t == typeof(char),
		JsonTypes = JsonTypes.String,
		Callback = (ref sb, obj, linkedType, config, _, _, _, ref hasSkiped) => { 
			if (!char.TryParse(obj.ToString().Escape(config.UnicodeEscape), out _)) {
				hasSkiped = true;
				return;
			}
            sb.Append($"\"{obj}\"");
        }
	};

	public static readonly Func<string?, JsonSerialization> DateTimeSerialization = format => new() {
		TypePredicate = (t, ref _) => t == typeof(DateTime),
		JsonTypes = JsonTypes.Number | JsonTypes.String,
		Callback = (ref sb, obj, _, config, _, _, _, ref _) => { 
			DateTime dt = (DateTime)obj;
			sb.Append(format == null ? (dt - DateTime.UnixEpoch).TotalSeconds : $"\"{dt.ToString(format).Escape(config.UnicodeEscape)}\""); 
		}
    };

	public static readonly JsonSerialization TypeSerialization = new() { 
		TypePredicate = (t, ref _) => t.IsAssignableTo(typeof(Type)),
		JsonTypes = JsonTypes.String,
		Callback = (ref sb, obj, _, _, _, _, _, ref _) => sb.Append($"\"{StringType((Type)obj)}\"")
    };


	public static readonly JsonSerialization StringKeyDictionarySerialization = new() {
		TypePredicate = (t, ref iDictType) => {
			iDictType = t.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>));
			return iDictType != null;
		},
		JsonTypes = JsonTypes.Object,
		Callback = (ref sb, obj, linkedType, config, indentCount, invoker, iDictType, ref hasCanceled) => {
            var kType = iDictType.GetGenericArguments()[0];
            if (!kType.HasJsonTypes(config.Pack, JsonTypes.String)) {
                hasCanceled = true;
                return;
            }
            var dict = (IDictionary)obj;
            if (dict.Count == 0) {
                sb.Append("{}");
                return;
            }

            var vType = iDictType.GetGenericArguments()[1];
            bool isObjValue = vType == typeof(object);
            bool isObjKey = kType == typeof(object);
            bool isNotFirst = false;
            bool hasIndent = config.Indent != "";
            int newIndentCount = hasIndent ? indentCount + 1 : 0;

            if (indentCount > 0) sb.AppendLine();
            sb.Append(config.Indent.Repeat(indentCount));
            sb.Append('{');

            foreach (var key in dict.Keys) {
                var value = dict[key];
                if (config.IgnoreNullValues && value == null) continue;
                if (isNotFirst) {
                    sb.Append(',');
                    if (hasIndent) sb.AppendLine();
                } else {
                    if (hasIndent) sb.AppendLine();
                    isNotFirst = true;
                }
                sb.Append(config.Indent.Repeat(newIndentCount));
                invoker.Invoke(key, new(!isObjKey ? kType : (key == null ? typeof(object) : key.GetType()), linkedType), newIndentCount);
                sb.Append(':');
                if (hasIndent) sb.Append(' ');
                invoker.Invoke(value, new(!isObjValue ? vType : (value == null ? typeof(object) : value.GetType()), linkedType), newIndentCount);
            }
            if (isNotFirst) {
                if (hasIndent) sb.AppendLine();
                sb.Append(config.Indent.Repeat(indentCount));
            }
            sb.Append('}');
        }
	};

	public static readonly JsonDeserialization CharDeserialization = new() { 
		TypePredicate = (t, ref _) => t == typeof(char),
		JsonTypes = JsonTypes.String,
		Callback = (ref buffer, _, _, _, _) => {
			if (!char.TryParse(buffer.ReadString(), out char result)) return null;
			return result;
		}
	};

	public static readonly JsonDeserialization DateTimeDeserialization = new() {
		TypePredicate = (t, ref _) => t == typeof(DateTime),
		JsonTypes = JsonTypes.String | JsonTypes.Number,
		Callback = (ref buffer, linkedType, _, _, _) => {
			var tempBuf = buffer;

            if (tempBuf.TryReadPrimitive(out string rawUnix) && TryDeserialize(rawUnix, out long unix)) {
                buffer = tempBuf;
                return DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
            }

			tempBuf = buffer;
            if (tempBuf.TryReadString(out string iso) && DateTime.TryParse(iso, CultureInfo.InvariantCulture, out var result)) {
                buffer = tempBuf;
                return result;
            }

            throw new JsonReflectionException();
        }
	};

	public static readonly JsonDeserialization StringKeyDictionaryDeserialization = new() {
        TypePredicate = (t, ref iDictType) => {
            iDictType = t.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>));
            return iDictType != null;
        },
		JsonTypes = JsonTypes.Object,
		Callback = (ref buffer, linkedType, config, invoker, iDictType) => {
            if (!iDictType.GetGenericArguments()[0].HasJsonTypes(config.Pack, JsonTypes.String)) return null;
            var type = linkedType.Value;
            var next = buffer.Next();
            if (next != JsonReadBuffer.NextType.Block) throw new JsonSyntaxException();
            next = buffer.NextBlock();
            if (next == JsonReadBuffer.NextType.EndBlock) return Activator.CreateInstance(type);
            if (next != JsonReadBuffer.NextType.Undefined) throw new JsonSyntaxException();

            var dict = (IDictionary)Activator.CreateInstance(type)!;
            LinkedType linkedVType = new(iDictType.GetGenericArguments()[1], linkedType);

            while (true) {
                if (next == JsonReadBuffer.NextType.Undefined) {
                    dict.Add(buffer.ReadObjectFieldName()!, invoker.Invoke(ref buffer, linkedVType));
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

            return dict;
        }
    };
}
