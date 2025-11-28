using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace SonicD5.Json;

public static partial class JsonSerializer {
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static StringBuilder Copy(this StringBuilder sb) => new(sb.ToString());

    public static bool HasJsonTypes(this Type t, IEnumerable<JsonLibary> pack, params IEnumerable<JsonTypes> types) {
		var lib = pack.First(l => l.CheckType(t, out _));
		return types.Any(t => lib.JsonTypes.HasFlag(t));
    }

    public static MemberInfo[] GetFieldsAndProperties(this Type type, Func<MemberInfo, bool> memberFilter, BindingFlags bindingFlags = BindingFlags.Public) => [.. type.GetMembers(BindingFlags.Instance | bindingFlags).Where(m => m is PropertyInfo p ? p.GetIndexParameters().Length == 0 : true && memberFilter(m))];

	public static string? Escape(this string? str, bool unicodeEscape = false) {
		if (str == null) return null;
		StringBuilder sb = new(str.Length);

		foreach (char c in str) { 
			switch (c) {
				case '\\':
				case '"':
					sb.Append($"\\{c}");
					continue;
				case '\b':
					sb.Append($"\\b");
					continue;
				case '\f':
					sb.Append($"\\f");
					continue;
				case '\n':
					sb.Append($"\\n");
					continue;
				case '\r':
					sb.Append($"\\r");
					continue;
				case '\t':
					sb.Append($"\\t");
					continue;
			}
			if (unicodeEscape && !char.IsAscii(c)) {
				sb.Append($"\\u{(ushort)c:X4}");
				continue;
			}
			sb.Append(c);
		}

		return sb.ToString();
	}

	private static string ToKebabCase(string str) {
		StringBuilder sb = new();
		bool previousSymbIsSeparator = true;

		for (int i = 0; i < str.Length; i++) {
			char symb = str[i];

			if (char.IsUpper(symb) || char.IsDigit(symb)) {
				if (!previousSymbIsSeparator && i > 0 && (char.IsLower(str[i - 1]) || (i < str.Length - 1 && char.IsLower(str[i + 1])))) sb.Append('-');
				sb.Append(char.ToLowerInvariant(symb));
				previousSymbIsSeparator = false;
			}
			else if (char.IsLower(symb)) {
				sb.Append(symb);
				previousSymbIsSeparator = false;
			}

			else if (symb is ' ' or '_' or '-') {
				if (!previousSymbIsSeparator) sb.Append('-');
				previousSymbIsSeparator = true;
			}
		}

		return sb.ToString();
	}

	private static string ToSnakeCase(string str) {
		StringBuilder sb = new(str.Length + Math.Min(2, str.Length / 5));
		UnicodeCategory? previousCategory = default;

		for (int i = 0; i < str.Length; i++) {
			char symb = str[i];

			if (symb == '_') {
				sb.Append('_');
				previousCategory = null;
				continue;
			}

			UnicodeCategory category = char.GetUnicodeCategory(symb);

			switch (category) {
				case UnicodeCategory.UppercaseLetter:
				case UnicodeCategory.TitlecaseLetter:
					if (previousCategory == UnicodeCategory.SpaceSeparator ||
						previousCategory == UnicodeCategory.LowercaseLetter ||
						previousCategory != UnicodeCategory.DecimalDigitNumber &&
						previousCategory != null &&
						i > 0 &&
						i + 1 < str.Length &&
						char.IsLower(str[i + 1])) {
						sb.Append('_');
					}

					symb = char.ToLower(symb, CultureInfo.InvariantCulture);
					break;

				case UnicodeCategory.LowercaseLetter:
				case UnicodeCategory.DecimalDigitNumber:
					if (previousCategory == UnicodeCategory.SpaceSeparator) {
						sb.Append('_');
					}
					break;

				default:
					if (previousCategory != null) previousCategory = UnicodeCategory.SpaceSeparator;
					continue;
			}
			sb.Append(symb);
			previousCategory = category;
		}
		return sb.ToString();
	}

	private static string ToCamelCase(string str, bool removeWhitespace = true, bool preserveLeadingUnderscore = false) {
		if (str.All(c => !char.IsLetter(c) && char.IsUpper(c))) str = str.ToLower(); 

		bool addLeadingUnderscore = preserveLeadingUnderscore && str.StartsWith('_');
		StringBuilder sb = new(str.Length);
		bool toUpper = false;

		foreach (char c in str) {
			if (c is '-' or '_' || (removeWhitespace && char.IsWhiteSpace(c))) toUpper = true;
			else {
				sb.Append(toUpper ? char.ToUpperInvariant(c) : c);
				toUpper = false;
			}
		}

		if (sb.Length > 0) sb[0] = char.ToLowerInvariant(sb[0]);
		if (addLeadingUnderscore) sb.Insert(0, '_');
		return sb.ToString();
	}

	private static string ToPascalCase(string str) {
		StringBuilder sb = new();
		var textInfo = CultureInfo.CurrentCulture.TextInfo;
		bool newWord = true;

		for (int i = 0; i < str.Length; i++) {
			char currentChar = str[i];

			if (char.IsLetterOrDigit(currentChar)) {
				if (newWord) {
					sb.Append(textInfo.ToUpper(currentChar));
					newWord = false;
				}
				else sb.Append(i < str.Length - 1 && char.IsUpper(currentChar) && char.IsLower(str[i + 1]) ? currentChar : char.ToLowerInvariant(currentChar));
			}
			else newWord = true;

			if (i < str.Length - 1 && char.IsLower(str[i]) && char.IsUpper(str[i + 1])) newWord = true;
		}

		return sb.ToString();
	}



	/// <summary>
	/// Thanks for raw version of case converter to https://github.com/markcastle/CaseConverter
	/// </summary>
	/// <param name="str"></param>
	/// <param name="convention"></param>
	/// <returns></returns>
	public static string ConvertCase(this string str, NamingConvetions convention) {
		if (string.IsNullOrEmpty(str)) return str;

		return convention switch {
			NamingConvetions.Any => str,
			NamingConvetions.SnakeCase => ToSnakeCase(str),
			NamingConvetions.KebabCase => ToKebabCase(str),
			NamingConvetions.PascalCase => ToPascalCase(str),
			NamingConvetions.CamelCase => ToCamelCase(str),
			_ => str
		};
	}

	public static string? Repeat(this string? str, int count) {
		if (count <= 0) return "";
		if (string.IsNullOrEmpty(str) || count == 1) return str;
		StringBuilder sb = new(count);
		for (int i = 0; i < count; i++) sb.Append(str);
		return sb.ToString();
	}

	internal static string Slice(this string source, int start, int count) {
		if (string.IsNullOrEmpty(source)) return source;
		int length = source.Length;
		int normalizedStart = start < 0 ? Math.Max(length + start, 0) : Math.Min(start, length);
		int actualCount = Math.Clamp(length - normalizedStart, 0, count);
		if (actualCount <= 0) return "";
		return source.Substring(normalizedStart, actualCount);
	}

	public static string StringType(Type type, bool hideGenericArgs = false) {
		var nullableValue = Nullable.GetUnderlyingType(type);
		if (nullableValue != null) return $"{StringType(nullableValue, hideGenericArgs)}?";
		StringBuilder sb = new();
		if (!string.IsNullOrEmpty(type.Namespace)) sb.Append($"{type.Namespace}.");
		if (type.IsGenericType) {
			var genericArgs = type.GetGenericArguments();
			sb.Append(type.Name[..type.Name.IndexOf('`')]);
			sb.Append('<');
			sb.Append(hideGenericArgs ? new(',', genericArgs.Length - 1) : string.Join(", ", genericArgs.Select(t => StringType(t))));
			sb.Append('>');
		} else sb.Append(type.Name);
		return sb.ToString();
	}

	public static string StringType<T>(bool hideGenericArgs = false) => StringType(typeof(T), hideGenericArgs);
}
