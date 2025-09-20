using System.Collections;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;

using static SonicD5.Json.JsonSerializer;

namespace SonicD5.Json;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class JsonSerializableAttribute(string name) : Attribute {
	public string Name { get; init; } = name;
}

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class JsonSerializationIgnoreAttribute : Attribute { }

public enum NamingConvetions { Any, CamelCase, SnakeCase, PascalCase, KebabCase }
public enum ObjectFieldConventions { NoQuote, SingleQuote = '\'', DoubleQuote = '\"' }

[Flags]
public enum JsonTypes {
    String,
	Number,
	Boolean,
    Array,
    Object
}

public class JsonException : Exception {
	public JsonException() { }
	public JsonException(string? message) : base(message) { }
	public JsonException(string? message, Exception? innerException) : base(message, innerException) { }
}

public class JsonReflectionException(string? message) : Exception(message) {
	public JsonReflectionException() : this("Invalid type") { }
}

public class JsonSyntaxException(string? message, JsonReadBuffer? buffer = null) : Exception(buffer == null ? message : $"{message}, throwed at ({buffer.Value.LineIndex}:{buffer.Value.BufferIndex})") {
	public JsonSyntaxException(JsonReadBuffer? buffer = null) : this("Wrong syntax", buffer) { }
}

public abstract class JsonPackable<TCallback> where TCallback : Delegate {

	public delegate bool Predicate(Type type, ref Type? foundType);
	public required JsonTypes JsonTypes { get; init; }
	public required Predicate TypePredicate { get; init; }

	public Predicate<Type> TypeChecker => TypeCheck;
	public required TCallback Callback { get; init; }

	private bool TypeCheck(Type type) {
        Type? foundType = null;
		return TypePredicate(type, ref foundType);
    }
}
public sealed partial class JsonSerialization : JsonPackable<JsonSerialization.CallbackFunc> {

	public delegate void CallbackFunc(ref StringBuilder sb, object obj, LinkedType linkedType, Config config, int indentCount, Invoker invoker, Type foundType, ref bool hasSkiped);
    public delegate void Invoker(object? obj, LinkedType linkedType, int indentCount);

    public sealed class Config {
        private readonly string _indent = "";
		private readonly ImmutableList<JsonSerialization> _customPack = [];

        public NamingConvetions NamingConvetion { get; init; } = NamingConvetions.Any;
        public ObjectFieldConventions ObjectFieldConvention { get; init; } = ObjectFieldConventions.DoubleQuote;
        public string Indent {
            get => _indent;
            init {
                for (int i = 0; i < value.Length; i++)
                    if (!char.IsWhiteSpace(value[i]))
                        throw new JsonSyntaxException("Wrong indent syntax");
                _indent = value;
            }
        }
        public bool IgnoreNullValues { get; init; }
        public bool UnicodeEscape { get; init; }
        public ImmutableList<JsonSerialization> Pack { get => _customPack.AddRange(DefaultPack); init => _customPack = value; }
    }
}

public sealed partial class JsonDeserialization : JsonPackable<JsonDeserialization.CallbackFunc> {

	public delegate object? CallbackFunc(ref JsonReadBuffer buffer, LinkedType linkedType, Config config, Invoker invoker, Type foundType);
	public delegate object? Invoker(ref JsonReadBuffer buffer, LinkedType linkedType);

    public sealed class Config {
        private readonly ImmutableList<JsonDeserialization> _customPack = [];

        public bool RequiredNamingEquality { get; init; }
        public ImmutableList<JsonDeserialization> Pack { get => _customPack.AddRange(DefaultPack); init => _customPack = value; } 
        public HashSet<Type> DynamicAvalableTypes { get; init; } = [];
    }
}

public class LinkedType : IEnumerable<Type> {
	public Type Value { get; init; }
	public LinkedType? Previous { get; init; }
	public LinkedType Last { get; private set; }

#pragma warning disable CS8618
    public LinkedType(Type value, LinkedType? previous) {
		Value = value;
		Previous = previous;
		for (var e = this; e != null; e = e.Previous) e.Last = this;
	}
#pragma warning restore CS8618

	public IEnumerator<Type> GetEnumerator() {
		Stack<Type> stack = [];
		for (var e = Last; e != null; e = e.Previous) stack.Push(e.Value);
		return stack.GetEnumerator();
    }
	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public override string ToString() {
		StringBuilder sb = new();
		if (Previous != null) {
			if (Previous.Previous != null)
				sb.Append(".. -> ");
			sb.Append($"{StringType(Previous.Value)} -> ");
		}
		sb.Append($"{StringType(Value)}");
		if (Last != this) {
			if (Last.Previous != this)
				sb.Append($" -> ..");
			sb.Append($" -> {StringType(Last.Value)}");
		}
		return sb.ToString();
	}
}

public struct JsonReadBuffer {
	public enum NextType {
		Punctuation,
		Block,
		EndBlock,
		Array,
		EndArray,
		Undefined,
	}

	private readonly Queue<string> _nextLines = [];
	private string buffer = "";

	public JsonReadBuffer(string text) {
		if (string.IsNullOrWhiteSpace(text)) throw new JsonSyntaxException("Invalid text");

		bool oneLine = true;
		string line = "";
		for (int i = 0; i < text.Length; i++) {
			bool windowsNewLine = text.Slice(i, 2) == Environment.NewLine;
            char c = text[i];
            if (windowsNewLine || c == '\n') {
				oneLine = false;
				_nextLines.Enqueue(line);
				if (windowsNewLine) i++;
				line = "";
				continue;
			}
			line += c;
		}
		if (!oneLine) {
			_nextLines.Enqueue(line);
			buffer = _nextLines.Dequeue();
		} else buffer = line;
	}

	private readonly bool IsNEOB => BufferIndex < buffer.Length;

	public int BufferIndex { get; private set; }
	public int LineIndex { get; private set; }

	public readonly override string ToString() {
		return buffer != "" ? $"({LineIndex}:{BufferIndex}) -> \"{buffer}\", {_nextLines.Count} lines left" : "N/A";
	}

	private void ReadNextLine() {
		read:
		if (IsNEOB) return;
		if (!_nextLines.TryDequeue(out string? nextLine)) throw new JsonSyntaxException($"Unexpected end of JSON input", this);
		LineIndex++;
		BufferIndex = 0;
		if (string.IsNullOrWhiteSpace(nextLine)) goto read;
		buffer = nextLine;
	}

	public bool NextIsNull() {
		ReadNextLine();
		bool comment = false;
		repeat:
		for (; IsNEOB; BufferIndex++) {
			string slice2 = buffer.Slice(BufferIndex, 2);
			if (slice2 == "//") {
				BufferIndex = buffer.Length;
				break;
			}
			char c = buffer[BufferIndex];
			if (comment && slice2 == "*/") {
				comment = false;
				BufferIndex++;
				continue;
			}
			if (!comment && slice2 == "/*") {
				comment = true;
				BufferIndex += 2;
			}
			if (comment || char.IsWhiteSpace(c))
				continue;
			if (c == 'n' && buffer.Slice(BufferIndex, JsonSerializer.Null.Length) == JsonSerializer.Null) {
				BufferIndex += JsonSerializer.Null.Length;
				return true;
			}
			return false;
		}
		ReadNextLine();
		goto repeat;
	}

	public NextType Next() {
		ReadNextLine();
		bool comment = false;
		repeat:
		for (; IsNEOB; BufferIndex++) {
			string slice2 = buffer.Slice(BufferIndex, 2);
			if (slice2 == "//") {
				BufferIndex = buffer.Length;
				break;
			}
			char c = buffer[BufferIndex];
			if (comment && slice2 == "*/") {
				comment = false;
				BufferIndex++;
				continue;
			}
			if (!comment && slice2 == "/*") {
				comment = true;
				BufferIndex += 2;
			}
			if (comment || char.IsWhiteSpace(c))
				continue;
			switch (c) {
				case ',':
					BufferIndex++;
					return NextType.Punctuation;
				case '{':
					BufferIndex++;
					return NextType.Block;
				case '}':
					BufferIndex++;
					return NextType.EndBlock;
				case '[':
					BufferIndex++;
					return NextType.Array;
				case ']':
					BufferIndex++;
					return NextType.EndArray;
				default:
					return NextType.Undefined;
			}
		}
		ReadNextLine();
		goto repeat;
	}
	public NextType NextBlock() {
		ReadNextLine();
		bool comment = false;
		repeat:
		for (; IsNEOB; BufferIndex++) {
			string slice2 = buffer.Slice(BufferIndex, 2);
			if (slice2 == "//") {
				BufferIndex = buffer.Length;
				break;
			}
			char c = buffer[BufferIndex];
			if (comment && slice2 == "*/") {
				comment = false;
				BufferIndex++;
				continue;
			}
			if (!comment && slice2 == "/*") {
				comment = true;
				BufferIndex += 2;
			}

			if (comment || char.IsWhiteSpace(c))
				continue;
			if (c == ',') { BufferIndex++; return NextType.Punctuation; }
			if (c == '}') { BufferIndex++; return NextType.EndBlock; }
			if (c == ']') { BufferIndex++; return NextType.EndArray; }
			return NextType.Undefined;
		}
		ReadNextLine();
		goto repeat;
	}

	internal void SkipObjectField() {
		Stack<char> blocks = [];

		do {
			ReadNextLine();
			for (; IsNEOB; BufferIndex++) {
				char c = buffer[BufferIndex];

				switch (c) {
					case '\'':
					case '"': {
						BufferIndex++;
						char quote = c;
						bool isEscaped = false;
						read_string:
						for (; IsNEOB; BufferIndex++) {
							c = buffer[BufferIndex];
							if (c == '\\') { isEscaped = !isEscaped; continue; }
							if (isEscaped) { isEscaped = false; continue; }
							if (c == quote)
								goto end_string;
						}
						if (!IsNEOB) { ReadNextLine(); goto read_string; }
						end_string:
						if (blocks.Count == 0) { BufferIndex++; return; }
						break;
					}
					case ',':
						if (blocks.Count == 0)
							return;
						break;
					case '{':
						blocks.Push('}');
						break;
					case '[':
						blocks.Push(']');
						break;
					case '}':
					case ']':
						if (blocks.Count == 0)
							return;
						if (c == blocks.Peek()) {
							blocks.Pop();
							if (blocks.Count == 0) { BufferIndex++; return; }
						} else
							throw new JsonSyntaxException($"Expected '{blocks.Peek()}' found '{c}'", this);
						break;
				}
			}
		} while (blocks.Count > 0);
	}
	public string ReadObjectFieldName() {
		ReadNextLine();
		int start = BufferIndex;
		for (; start < buffer.Length; start++) if (!char.IsWhiteSpace(buffer[start])) break;
		char quote = buffer[start] switch { '"' => '"', '\'' => '\'', _ => '\0' };

		int end;
		if (quote != '\0') {
			end = ++start;
			bool isSlash = false;
			for (; end < buffer.Length; end++) {
				char c = buffer[end];
				if (c == '\\') { isSlash = !isSlash; continue; }
				if (isSlash) { isSlash = false; continue; }
				if (c == quote) break;
			}
			BufferIndex = end + 1;
			SeekPropEndChar();
			return UnescapeString(buffer[start..end]);
		}
        end = start;
        for (; end < buffer.Length; end++) {
            char c = buffer[end];
            if (c == ':') {
                BufferIndex = end + 1;
                goto skipFinder;
            }
            if (char.IsWhiteSpace(c)) break;
            if (!char.IsLetterOrDigit(c) && c is not '_' and not '$' && (end <= start || c is not ('\\' or '\u200C' or '\u200D'))) {
                throw new JsonSyntaxException($"Invalid character '{c}' in parameter name", this);
            }
        }
        BufferIndex = end + 1;
        SeekPropEndChar();
		skipFinder:
        string value = buffer[start..end];
        if (value.Length == 0) throw new JsonSyntaxException($"Size of property name cannot be zero", this);
        return value;
    }
	public string ReadString() {
		read:
		ReadNextLine();

		char quote = '"';
		for (; BufferIndex < buffer.Length; BufferIndex++) {
			char c = buffer[BufferIndex];
			if (c is '"' or '\'') {
				quote = c;
				break;
			}
			if (!char.IsWhiteSpace(c)) throw new JsonSyntaxException($"Expected string start in quote", this);
		}

		BufferIndex++;
		if (!IsNEOB) goto read;

		StringBuilder sb = new();

		int end = BufferIndex;
		for (; end < buffer.Length; end++) {
			char c = buffer[end];
			if (c == '\\' && end + 1 == buffer.Length) {
				end = -1;
				BufferIndex = buffer.Length;
				ReadNextLine();
				sb.Append('\n');
				continue;
			}
			if (c == quote) break;
			sb.Append(c);
		}
		if (end == buffer.Length) throw new JsonSyntaxException($"Unterminated string", this);

		BufferIndex = end + 1;
		return UnescapeString(sb.ToString());
	}

	public string ReadPrimitive() {
		ReadNextLine();

		int start = BufferIndex;
		for (; start < buffer.Length; start++)
			if (!char.IsWhiteSpace(buffer[start]))
				break;
		if (start == buffer.Length)
			throw new JsonSyntaxException($"Unexpected end of primitive value", this);

		int end = start;
		for (; end < buffer.Length; end++) {
			char c = buffer[end];
			if (c is ',' or '}' or ']' || char.IsWhiteSpace(c)) break;
		}

		BufferIndex = end;
		return buffer[start..end];
	}

	public bool TryReadString(out string result) => TryRead(ReadString, out result);
	public bool TryReadPrimitive(out string result) => TryRead(ReadPrimitive, out result);

	private static bool TryRead(Func<string> reader, out string result) {
		try {
			result = reader.Invoke();
			return true;
		} catch {
			result = "";
			return false;
		}
	}

	private void SeekPropEndChar() {
		repeat:
		while (IsNEOB) {
			char c = buffer[BufferIndex++];
			if (c == ':') return;
			if (!char.IsWhiteSpace(c)) throw new JsonSyntaxException($"Expected ':', found '{c}'", this);
		}
		ReadNextLine();
		goto repeat;
	}

	private static string UnescapeString(string str) {

		StringBuilder sb = new(str.Length);
		bool isUnescaped = false;

		for (int i = 0; i < str.Length; i++) {
			char c = str[i];
			if (isUnescaped) {
				switch (c) {
					case '\\':
					case '"':
						sb.Append(c);
						break;
					case 'b':
						sb.Append('\b');
						break;
					case 'f':
						sb.Append('\f');
						break;
					case 'n':
						sb.Append('\n');
						break;
					case 'r':
						sb.Append('\r');
						break;
					case 't':
						sb.Append('\t');
						break;
					case 'u':
						// Обработка Unicode escape последовательностей
						if (i + 4 < str.Length) {
							string hex = str.Substring(i + 1, 4);
							if (int.TryParse(hex, NumberStyles.HexNumber,
								CultureInfo.InvariantCulture, out int code)) {
								sb.Append((char)code);
								i += 4;
							} else throw new JsonSyntaxException($"Invalid Unicode escape sequence: \\u{hex}");
						} else throw new JsonSyntaxException("Incomplete Unicode escape sequence");
						break;
					default: throw new JsonSyntaxException($"Invalid escape sequence: \\{c}");
				}
				isUnescaped = false;
			} else if (c == '\\') isUnescaped = true;
			else sb.Append(c);
		}
		if (isUnescaped) throw new JsonSyntaxException("Unterminated escape sequence");
		return sb.ToString();
	}
}