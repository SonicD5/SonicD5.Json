using System.Collections;
using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Runtime.CompilerServices;
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
	Object = 2,
	Array = 4,
	String = 8,
	Number = 16,
	Boolean = 32,
}

public class JsonException : Exception {
	public JsonException() { }
	public JsonException(string? message) : base(message) { }
	public JsonException(string? message, Exception? innerException) : base(message, innerException) { }
}

public class JsonReflectionException(string? message) : Exception(message) {
	public JsonReflectionException() : this("Invalid type") { }
}

public class JsonSyntaxException : Exception {
	public JsonSyntaxException(string? message, JsonReadBuffer? buffer = null) : base(buffer == null ? message : $"{message}, throwed at ({buffer.LineIndex}:{buffer.BufferIndex}") { }

	public JsonSyntaxException(JsonReadBuffer? buffer = null) : this("Wrong syntax", buffer) { }

}

public sealed partial class JsonLibary {
	public struct PredicateContext {
		public required Type Type { get; init; }
		public Type? FoundType { get; set; }
	}

	public delegate bool Predicate(ref PredicateContext ctx);
	public required JsonTypes JsonTypes { get; init; }
	public required Predicate TPredicate { private get; init; }
	public required JsonSerialization.Callback SCallback { get; init; }
	public required JsonDeserialization.Callback DCallback { get; init; }

    public bool CheckType(Type type, out Type? foundType) {
		PredicateContext ctx = new() { Type = type };
		bool result = TPredicate.Invoke(ref ctx);
        foundType = ctx.FoundType;
		return result;
    }

	public abstract class Config {
        private readonly ImmutableList<JsonLibary> _customLibPack = [];
        public ImmutableList<JsonLibary> LibaryPack { get => _customLibPack.AddRange(DefaultLibaryPack); init => _customLibPack = value; }
    }
}
public static class JsonSerialization {
    public sealed class Config : JsonLibary.Config {

        public NamingConvetions NamingConvetion { get; init; } = NamingConvetions.Any;
        public ObjectFieldConventions ObjectFieldConvention { get; init; } = ObjectFieldConventions.DoubleQuote;
		[Range(0, int.MaxValue)]
		public int MinNestLevel { get; init; } = 2;
		public string Indent {
			get => field;
			init {
				for (int i = 0; i < value.Length; i++)
					if (!char.IsWhiteSpace(value[i]))
						throw new JsonSyntaxException("Wrong indent syntax");
				field = value;
			}
		} = "";
        public bool IgnoreNullValues { get; init; }
        public bool UnicodeEscape { get; init; }
    }

    public delegate void Callback(ref CallbackContext ctx);
    public delegate void Invoker(object? obj, LinkedType linkedType, int indentCount);
	public delegate Invoker InvokerInitalizer(ref StringBuilder result);

    public struct CallbackContext {
		public required StringBuilder Result { get; set; }
		public bool HasSkiped { get; set; }
        public required object Object { get; init; }
		public required LinkedType Type { get; init; }
		public required Config Config { get; init; }
		public required int IndentCount { get; init; }
		public Invoker Invoker { get; set { field ??= value; } }
        public required Type FoundType {
            readonly get {
				if (field == null) throw new NullReferenceException("The found type must be deleted or be found");
				return field;
			} init; 
		}
    }
}

public static class JsonDeserialization {
    public sealed class Config : JsonLibary.Config {
        public bool RequiredNamingEquality { get; init; }
        public HashSet<Type> DynamicAvalableTypes { get; init; } = [];
    }

    public delegate object? Callback(ref CallbackContext ctx);
    public delegate object? Invoker(ref JsonReadBuffer buffer, LinkedType linkedType);

	public struct CallbackContext {
		public required JsonReadBuffer Buffer { get; set; }
		public required LinkedType Type { get; init; }
		public required Config Config { get; init; }
		public Invoker Invoker { readonly get => field; set { field ??= value; } }
		public required Type FoundType {
			readonly get {
				if (field == null) throw new NullReferenceException("The found type must be deleted or be found");
				return field;
			}
			init;
		}
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

public sealed class JsonReadBuffer {
	public enum NextType {
		Punctuation,
		Block,
		EndBlock,
		Array,
		EndArray,
		Undefined,
	}

	private Queue<string> _nextLines = [];
	private string _buffer = "";

	public JsonReadBuffer Copy() => new() {
        _buffer = _buffer,
        _nextLines = new(_nextLines),
		BufferIndex = BufferIndex,
		LineIndex = LineIndex,
    };

	private JsonReadBuffer() { }

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
			_buffer = _nextLines.Dequeue();
		} else _buffer = line;
	}

	private bool IsNEOB => BufferIndex < _buffer.Length;

	public int BufferIndex { get; private set; }
	public int LineIndex { get; private set; }

	public override string ToString() {
		return _buffer != "" ? $"({LineIndex}:{BufferIndex}) -> \"{_buffer}\", {_nextLines.Count} lines left" : "N/A";
	}

	private void ReadNextLine() {
		read:
		if (IsNEOB) return;
		if (!_nextLines.TryDequeue(out string? nextLine)) throw new JsonSyntaxException($"Unexpected end of JSON input", this);
		LineIndex++;
		BufferIndex = 0;
		if (string.IsNullOrWhiteSpace(nextLine)) goto read;
		_buffer = nextLine;
	}

	public bool NextIsNull() {
		ReadNextLine();
		bool comment = false;
		repeat:
		for (; IsNEOB; BufferIndex++) {
			string slice2 = _buffer.Slice(BufferIndex, 2);
			if (slice2 == "//") {
				BufferIndex = _buffer.Length;
				break;
			}
			char c = _buffer[BufferIndex];
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
			if (c == 'n' && _buffer.Slice(BufferIndex, Null.Length) == Null) {
				BufferIndex += Null.Length;
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
			string slice2 = _buffer.Slice(BufferIndex, 2);
			if (slice2 == "//") {
				BufferIndex = _buffer.Length;
				break;
			}
			char c = _buffer[BufferIndex];
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
			string slice2 = _buffer.Slice(BufferIndex, 2);
			if (slice2 == "//") {
				BufferIndex = _buffer.Length;
				break;
			}
			char c = _buffer[BufferIndex];
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
				char c = _buffer[BufferIndex];

				switch (c) {
					case '\'':
					case '"': {
						BufferIndex++;
						char quote = c;
						bool isEscaped = false;
						read_string:
						for (; IsNEOB; BufferIndex++) {
							c = _buffer[BufferIndex];
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
		for (; start < _buffer.Length; start++) if (!char.IsWhiteSpace(_buffer[start])) break;
		char quote = _buffer[start] switch { '"' => '"', '\'' => '\'', _ => '\0' };

		int end;
		if (quote != '\0') {
			end = ++start;
			bool isSlash = false;
			for (; end < _buffer.Length; end++) {
				char c = _buffer[end];
				if (c == '\\') { isSlash = !isSlash; continue; }
				if (isSlash) { isSlash = false; continue; }
				if (c == quote) break;
			}
			BufferIndex = end + 1;
			SeekPropEndChar();
			return UnescapeString(_buffer[start..end]);
		}
        end = start;
        for (; end < _buffer.Length; end++) {
            char c = _buffer[end];
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
        string value = _buffer[start..end];
        if (value.Length == 0) throw new JsonSyntaxException($"Size of property name cannot be zero", this);
        return value;
    }
	public string ReadString() {
		read:
		ReadNextLine();

		char quote = '"';
		for (; BufferIndex < _buffer.Length; BufferIndex++) {
			char c = _buffer[BufferIndex];
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
		for (; end < _buffer.Length; end++) {
			char c = _buffer[end];
			if (c == '\\' && end + 1 == _buffer.Length) {
				end = -1;
				BufferIndex = _buffer.Length;
				ReadNextLine();
				sb.Append('\n');
				continue;
			}
			if (c == quote) break;
			sb.Append(c);
		}
		if (end == _buffer.Length) throw new JsonSyntaxException($"Unterminated string", this);

		BufferIndex = end + 1;
		return UnescapeString(sb.ToString());
	}

	public string ReadPrimitive() {
		ReadNextLine();

		int start = BufferIndex;
		for (; start < _buffer.Length; start++)
			if (!char.IsWhiteSpace(_buffer[start]))
				break;
		if (start == _buffer.Length)
			throw new JsonSyntaxException($"Unexpected end of primitive value", this);

		int end = start;
		for (; end < _buffer.Length; end++) {
			char c = _buffer[end];
			if (c is ',' or '}' or ']' || char.IsWhiteSpace(c)) break;
		}

		BufferIndex = end;
		return _buffer[start..end];
	}

    public bool TryReadString(out string result) => TryRead(ReadString, out result);
    public bool TryReadPrimitive(out string result) => TryRead(ReadPrimitive, out result);

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
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
			char c = _buffer[BufferIndex++];
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