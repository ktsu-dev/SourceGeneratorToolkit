// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.SourceGeneratorToolkit;

using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

/// <summary>
/// One metadata file a generator was asked for, with the text it was found to contain.
/// </summary>
/// <param name="fileName">The file's name, without its directory.</param>
/// <param name="text">The file's contents.</param>
/// <param name="sourceText">The underlying <see cref="SourceText"/>, used to build locations.</param>
/// <param name="path">The file's full path, used to build locations.</param>
/// <remarks>
/// Compared by value — name, path and text — because Roslyn compares incremental step outputs with
/// their default equality. By reference, every new <see cref="AdditionalText"/> the IDE creates for
/// unchanged contents (an undone edit, a save with no change) would re-run the whole generator.
/// </remarks>
public sealed class MetadataFile(string fileName, string text, SourceText? sourceText, string path) : IEquatable<MetadataFile>
{
	/// <summary>
	/// Gets the options <see cref="Deserialize{T}(SourceProductionContext, DiagnosticDescriptor)"/>
	/// reads metadata with: property names match case-insensitively, and <c>//</c> and <c>/* */</c>
	/// comments and trailing commas are accepted, because metadata files are maintained by hand.
	/// </summary>
	/// <remarks>
	/// Read-only, so it can be shared. To change it, copy it, adjust the copy, and keep that copy in a
	/// <see langword="static"/> <see langword="readonly"/> field:
	/// <c>new JsonSerializerOptions(MetadataFile.DefaultSerializerOptions) { Converters = { new JsonStringEnumConverter() } }</c>.
	/// Allocated once rather than per call: constructing <see cref="JsonSerializerOptions"/> inside
	/// the method builds a fresh serializer cache every time (CA1869), which for a generator that
	/// re-reads its metadata on every compilation is the expensive half of the work.
	/// </remarks>
	public static JsonSerializerOptions DefaultSerializerOptions { get; } = CreateDefaultSerializerOptions();

	private static JsonSerializerOptions CreateDefaultSerializerOptions()
	{
		JsonSerializerOptions options = new()
		{
			PropertyNameCaseInsensitive = true,
			ReadCommentHandling = JsonCommentHandling.Skip,
			AllowTrailingCommas = true,
		};
		options.MakeReadOnly(populateMissingResolver: true);
		return options;
	}

	/// <summary>Gets the file's name, without its directory.</summary>
	public string FileName { get; } = fileName;

	/// <summary>Gets the file's contents.</summary>
	public string Text { get; } = text;

	/// <summary>
	/// Gets the file's full path. Held rather than read from the constructor parameter so
	/// <see cref="Equals(MetadataFile)"/> can compare it on another instance.
	/// </summary>
	internal string FilePath { get; } = path;

	/// <inheritdoc/>
	public bool Equals(MetadataFile? other) =>
		other is not null
		&& string.Equals(FileName, other.FileName, StringComparison.Ordinal)
		&& string.Equals(FilePath, other.FilePath, StringComparison.Ordinal)
		&& string.Equals(Text, other.Text, StringComparison.Ordinal);

	/// <inheritdoc/>
	public override bool Equals(object? obj) => Equals(obj as MetadataFile);

	/// <inheritdoc/>
	public override int GetHashCode()
	{
		unchecked
		{
			int hash = StringComparer.Ordinal.GetHashCode(FileName);
			hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(FilePath);
			return (hash * 397) ^ StringComparer.Ordinal.GetHashCode(Text);
		}
	}

	/// <summary>
	/// Finds the first occurrence of <paramref name="needle"/> in the file and returns a location
	/// covering it.
	/// </summary>
	/// <param name="needle">The text to find — typically the offending name from the metadata.</param>
	/// <returns>
	/// A location in the metadata file, or <see cref="Location.None"/> when the text is not found.
	/// </returns>
	/// <remarks>
	/// A diagnostic reported at <see cref="Location.None"/> tells the reader which name is wrong but
	/// not where it is written, which for a metadata file of any size is most of the work. Matching
	/// on the name itself is approximate — the first occurrence wins, and a name that appears in
	/// several entries points at the first — but it is navigable, which no location at all is not.
	/// </remarks>
	public Location FindLocation(string needle)
	{
		if (sourceText is null || string.IsNullOrEmpty(needle))
		{
			return Location.None;
		}

		int index = IndexOfToken(needle, 0, out int length);
		return index < 0 ? Location.None : LocationAt(index, length);
	}

	/// <summary>
	/// Finds <paramref name="needle"/> in the JSON object that holds the first occurrence of
	/// <paramref name="anchor"/>, preferring an occurrence after the anchor, and returns a location
	/// covering it.
	/// </summary>
	/// <param name="anchor">Text that scopes the search — typically the entry the needle belongs to.</param>
	/// <param name="needle">The text to find within that scope.</param>
	/// <returns>
	/// A location in the metadata file, falling back to the first unscoped match of
	/// <paramref name="needle"/> and then to <see cref="Location.None"/>.
	/// </returns>
	/// <remarks>
	/// The unscoped <see cref="FindLocation(string)"/> is enough when the needle is a typo, which by
	/// definition occurs once. It is not enough when the needle is a name that is spelled correctly
	/// in dozens of places and only wrong in one of them — there the first occurrence is somewhere
	/// else entirely. Anchoring on the surrounding entry picks the right one.
	/// <para>
	/// JSON objects have no key order, so the needle can be written before the anchor in the same
	/// entry, as in <c>{"base":"Meter","name":"Foot"}</c>. Searching only forward from the anchor
	/// would then land in a later entry, or fall back to an earlier one. The search therefore looks
	/// after the anchor within its enclosing object, then before it within that object, and only
	/// then forward past the object and finally anywhere in the file.
	/// </para>
	/// </remarks>
	public Location FindLocation(string anchor, string needle)
	{
		if (sourceText is null || string.IsNullOrEmpty(needle))
		{
			return Location.None;
		}

		int scope = string.IsNullOrEmpty(anchor) ? 0 : IndexOfToken(anchor, 0, out _);
		if (scope < 0)
		{
			return FindLocation(needle);
		}

		int index = -1;
		int length = 0;
		if (TryGetEnclosingObject(scope, out int open, out int close))
		{
			index = IndexOfToken(needle, scope, close, out length);
			if (index < 0)
			{
				index = IndexOfToken(needle, open, scope, out length);
			}
		}

		if (index < 0)
		{
			index = IndexOfToken(needle, scope, out length);
		}

		return index < 0 ? FindLocation(needle) : LocationAt(index, length);
	}

	/// <summary>
	/// Finds the JSON object that encloses <paramref name="index"/>.
	/// </summary>
	/// <param name="index">A position in the file.</param>
	/// <param name="open">The index of the object's opening brace.</param>
	/// <param name="close">
	/// The index just past the object's closing brace, or the end of the file when it is unclosed.
	/// </param>
	/// <returns><see langword="true"/> when <paramref name="index"/> is inside an object.</returns>
	/// <remarks>
	/// The file is scanned from the start rather than backwards from the index, because only a
	/// forward scan can tell which quotes open a string: braces inside strings, and nested objects
	/// written before the index, must not be taken for the enclosing object's braces.
	/// </remarks>
	private bool TryGetEnclosingObject(int index, out int open, out int close)
	{
		List<int> opens = [];
		bool inString = false;
		bool escaped = false;
		int depth = -1;
		open = -1;

		for (int i = 0; i < Text.Length; i++)
		{
			if (i == index)
			{
				if (opens.Count == 0)
				{
					break;
				}

				open = opens[^1];
				depth = opens.Count;
			}

			char c = Text[i];
			if (inString)
			{
				if (escaped)
				{
					escaped = false;
				}
				else if (c == '\\')
				{
					escaped = true;
				}
				else if (c == '"')
				{
					inString = false;
				}

				continue;
			}

			if (c == '"')
			{
				inString = true;
			}
			else if (c == '{')
			{
				opens.Add(i);
			}
			else if (c == '}' && opens.Count > 0)
			{
				if (opens.Count == depth)
				{
					close = i + 1;
					return true;
				}

				opens.RemoveAt(opens.Count - 1);
			}
		}

		close = Text.Length;
		return open >= 0;
	}

	/// <summary>
	/// Finds <paramref name="value"/> at or after <paramref name="startIndex"/>, preferring an
	/// occurrence that is a whole JSON string.
	/// </summary>
	/// <param name="value">The text to find.</param>
	/// <param name="startIndex">Where the search starts.</param>
	/// <param name="length">The length of the match in the file.</param>
	/// <returns>The index of the text itself, inside its quotes when it was found quoted, or -1.</returns>
	/// <remarks>
	/// A plain substring search lands inside longer names: <c>Meter</c> is found in
	/// <c>"SquareMeter"</c>, which is a different entry. Matching whole JSON strings first finds
	/// only the whole string. The raw search remains the fallback so that text which is not a JSON
	/// string on its own, such as a number or an already quoted anchor, is still found.
	/// </remarks>
	private int IndexOfToken(string value, int startIndex, out int length) => IndexOfToken(value, startIndex, Text.Length, out length);

	/// <summary>
	/// Finds <paramref name="value"/> between <paramref name="startIndex"/> and
	/// <paramref name="endIndex"/>, preferring an occurrence that is a whole JSON string.
	/// </summary>
	/// <param name="value">The text to find.</param>
	/// <param name="startIndex">Where the search starts.</param>
	/// <param name="endIndex">Where the search ends; a match must lie wholly before it.</param>
	/// <param name="length">
	/// The length of the match in the file. For a JSON string it is the length of the literal's
	/// escaped contents, which can differ from the length of <paramref name="value"/>.
	/// </param>
	/// <returns>The index of the text itself, inside its quotes when it was found quoted, or -1.</returns>
	/// <remarks>
	/// A generator passes a value it got back from deserializing the file, which is unescaped, while
	/// the file holds the escaped JSON form: <c>C:\temp</c> is written <c>"C:\\temp"</c>, and
	/// System.Text.Json writes <c>Ω</c> as <c>"\u03A9"</c>. So each string literal is decoded and
	/// compared with the value, rather than the value being searched for in the raw text (#68).
	/// </remarks>
	private int IndexOfToken(string value, int startIndex, int endIndex, out int length)
	{
		length = 0;
		if (endIndex - startIndex <= 0)
		{
			return -1;
		}

		foreach (StringLiteral literal in GetStringLiterals())
		{
			if (literal.OpeningQuote < startIndex)
			{
				continue;
			}

			if (literal.ClosingQuote >= endIndex)
			{
				break;
			}

			if (string.Equals(literal.Value, value, StringComparison.Ordinal))
			{
				length = literal.ClosingQuote - literal.OpeningQuote - 1;
				return literal.OpeningQuote + 1;
			}
		}

		int raw = Text.IndexOf(value, startIndex, endIndex - startIndex, StringComparison.Ordinal);
		length = raw < 0 ? 0 : value.Length;
		return raw;
	}

	/// <summary>
	/// A string literal in the file: where its quotes are and what it decodes to.
	/// </summary>
	/// <param name="OpeningQuote">The index of the opening quote.</param>
	/// <param name="ClosingQuote">The index of the closing quote.</param>
	/// <param name="Value">
	/// The decoded string, or <see langword="null"/> when the literal holds an escape JSON does not allow.
	/// </param>
	private readonly record struct StringLiteral(int OpeningQuote, int ClosingQuote, string? Value);

	private List<StringLiteral>? stringLiterals;

	/// <summary>
	/// Gets the file's string literals, in order, scanning the file the first time they are needed.
	/// </summary>
	/// <returns>The closed string literals outside comments.</returns>
	/// <remarks>
	/// The file is scanned from the start, as in <see cref="TryGetEnclosingObject"/>, because only a
	/// forward scan can tell which quotes open a string. <c>//</c> and <c>/* */</c> comments are skipped,
	/// since <see cref="DefaultSerializerOptions"/> accepts them and a quote inside one opens no string.
	/// An unclosed literal at the end of the file is left out.
	/// </remarks>
	private List<StringLiteral> GetStringLiterals()
	{
		if (stringLiterals is not null)
		{
			return stringLiterals;
		}

		List<StringLiteral> literals = [];
		int i = 0;
		while (i < Text.Length)
		{
			if (Text[i] == '"')
			{
				int close = ClosingQuoteOf(i);
				if (close < 0)
				{
					break;
				}

				literals.Add(new StringLiteral(i, close, Unescape(i + 1, close)));
				i = close + 1;
			}
			else
			{
				i = PastComment(i);
			}
		}

		stringLiterals = literals;
		return literals;
	}

	/// <summary>
	/// Moves past a <c>//</c> or <c>/* */</c> comment starting at <paramref name="index"/>, or past one
	/// character when none starts there.
	/// </summary>
	/// <param name="index">A position outside any string.</param>
	/// <returns>The index after the comment, or after the character.</returns>
	private int PastComment(int index)
	{
		if (Text[index] != '/' || index + 1 >= Text.Length)
		{
			return index + 1;
		}

		if (Text[index + 1] == '/')
		{
			int lineFeed = Text.IndexOf('\n', index);
			return lineFeed < 0 ? Text.Length : lineFeed + 1;
		}

		if (Text[index + 1] == '*')
		{
			int end = Text.IndexOf("*/", index + 2, StringComparison.Ordinal);
			return end < 0 ? Text.Length : end + 2;
		}

		return index + 1;
	}

	/// <summary>
	/// Finds the quote that closes the string opened at <paramref name="openingQuote"/>.
	/// </summary>
	/// <param name="openingQuote">The index of the opening quote.</param>
	/// <returns>The index of the closing quote, or -1 when the string is not closed.</returns>
	private int ClosingQuoteOf(int openingQuote)
	{
		int i = openingQuote + 1;
		while (i < Text.Length)
		{
			if (Text[i] == '"')
			{
				return i;
			}

			// A backslash escapes the character after it, so an escaped quote does not close the string.
			i += Text[i] == '\\' ? 2 : 1;
		}

		return -1;
	}

	/// <summary>
	/// Decodes the contents of a JSON string literal.
	/// </summary>
	/// <param name="start">The index of the first character after the opening quote.</param>
	/// <param name="end">The index of the closing quote.</param>
	/// <returns>The decoded string, or <see langword="null"/> when it holds an escape JSON does not allow.</returns>
	private string? Unescape(int start, int end)
	{
		int backslash = Text.IndexOf('\\', start, end - start);
		if (backslash < 0)
		{
			return Text[start..end];
		}

		System.Text.StringBuilder decoded = new(end - start);
		decoded.Append(Text, start, backslash - start);
		int i = backslash;
		while (i < end)
		{
			if (Text[i] != '\\')
			{
				decoded.Append(Text[i]);
				i++;
			}
			else if (!TryAppendEscape(i, end, decoded, out i))
			{
				return null;
			}
		}

		return decoded.ToString();
	}

	/// <summary>
	/// Decodes the escape sequence whose backslash is at <paramref name="backslash"/>.
	/// </summary>
	/// <param name="backslash">The index of the backslash.</param>
	/// <param name="end">The index of the closing quote.</param>
	/// <param name="decoded">The decoded text, which the escaped character is appended to.</param>
	/// <param name="next">The index after the escape sequence.</param>
	/// <returns><see langword="false"/> when the escape is one JSON does not allow.</returns>
	private bool TryAppendEscape(int backslash, int end, System.Text.StringBuilder decoded, out int next)
	{
		next = backslash + 2;
		if (next > end)
		{
			return false;
		}

		char escape = Text[backslash + 1];
		if (escape == 'u')
		{
			next = backslash + 6;
			if (next > end || !ushort.TryParse(Text.Substring(backslash + 2, 4), System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out ushort code))
			{
				return false;
			}

			decoded.Append((char)code);
			return true;
		}

		char? unescaped = escape switch
		{
			'"' or '\\' or '/' => escape,
			'b' => '\b',
			'f' => '\f',
			'n' => '\n',
			'r' => '\r',
			't' => '\t',
			_ => null,
		};
		if (unescaped is not char simple)
		{
			return false;
		}

		decoded.Append(simple);
		return true;
	}

	/// <summary>
	/// Deserializes the file into <typeparamref name="T"/>.
	/// </summary>
	/// <typeparam name="T">The metadata shape to deserialize into.</typeparam>
	/// <param name="context">The source production context, used to report a parse failure.</param>
	/// <param name="parseFailed">
	/// The descriptor reported when the file cannot be parsed. Its message format takes the file
	/// name and the reason.
	/// </param>
	/// <returns>The deserialized metadata, or <see langword="null"/> when parsing failed.</returns>
	/// <remarks>
	/// A parse failure is always reported. Swallowing it — the tempting shape, because a generator
	/// has nowhere obvious to throw — means malformed metadata produces no diagnostic at all and the
	/// generator silently emits something wrong.
	/// <para>
	/// Malformed input is not the only way deserialization fails, so catching only
	/// <see cref="JsonException"/> is not enough. <see cref="JsonSerializer"/> reports a shape it
	/// cannot construct — an interface or abstract type, or a type with several parameterized
	/// constructors and no <c>[JsonConstructor]</c> — as <see cref="NotSupportedException"/>, and
	/// some converter-configuration failures as <see cref="InvalidOperationException"/>. Neither
	/// derives from <see cref="JsonException"/>, so both used to escape to the Roslyn driver, which
	/// reports its own generic CS8785 naming neither the file nor the reason and abandons every
	/// other file in the same invocation. They degrade to the same diagnostic as malformed JSON.
	/// </para>
	/// <para>
	/// <see cref="JsonSerializer"/> also lets the model's own constructor exceptions through
	/// unwrapped, so an ordinary guard clause meeting a missing property throws
	/// <see cref="ArgumentNullException"/>, and ordinary model code throws whatever it throws: a
	/// <see cref="NullReferenceException"/> from <c>name.Trim()</c> on a missing property, a
	/// <see cref="FormatException"/> or <see cref="OverflowException"/> from <c>int.Parse</c>. Any of
	/// those escaping would be the same CS8785, so every exception is reported the same way, with its
	/// type name in the reason. Only <see cref="OperationCanceledException"/>, which the driver uses
	/// to stop a run, and <see cref="OutOfMemoryException"/> are let through.
	/// </para>
	/// </remarks>
	public T? Deserialize<T>(SourceProductionContext context, DiagnosticDescriptor parseFailed)
		where T : class =>
		Deserialize<T>(context, parseFailed, DefaultSerializerOptions);

	/// <summary>
	/// Deserializes the file into <typeparamref name="T"/> with the given options.
	/// </summary>
	/// <typeparam name="T">The metadata shape to deserialize into.</typeparam>
	/// <param name="context">The source production context, used to report a parse failure.</param>
	/// <param name="parseFailed">
	/// The descriptor reported when the file cannot be parsed. Its message format takes the file
	/// name and the reason.
	/// </param>
	/// <param name="options">
	/// The options to read the file with. Pass a cached instance, such as a copy of
	/// <see cref="DefaultSerializerOptions"/> held in a <see langword="static"/> <see langword="readonly"/>
	/// field, rather than a new one per call (CA1869).
	/// </param>
	/// <returns>The deserialized metadata, or <see langword="null"/> when parsing failed.</returns>
	/// <remarks>
	/// Failures are reported exactly as by
	/// <see cref="Deserialize{T}(SourceProductionContext, DiagnosticDescriptor)"/>.
	/// </remarks>
	/// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
	public T? Deserialize<T>(SourceProductionContext context, DiagnosticDescriptor parseFailed, JsonSerializerOptions options)
		where T : class
	{
		Ensure.NotNull(options);

		T? metadata;
		try
		{
			metadata = JsonSerializer.Deserialize<T>(Text, options);
		}
		catch (JsonException ex)
		{
			context.ReportAt(parseFailed, LocationOf(ex), FileName, ex.Message);
			return null;
		}
		catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
		{
			context.Report(parseFailed, FileName, $"{ex.GetType().Name}: {ex.Message}");
			return null;
		}

		if (metadata is not null)
		{
			return metadata;
		}

		context.Report(parseFailed, FileName, "the document deserialized to null");
		return null;
	}

	/// <summary>
	/// Finds where in the file a <see cref="JsonException"/> says the JSON went wrong.
	/// </summary>
	/// <param name="ex">The exception thrown while reading the file.</param>
	/// <returns>
	/// A location covering the character at the reported position, or <see cref="Location.None"/>
	/// when the exception carries no position or there is no source text to locate it in.
	/// </returns>
	/// <remarks>
	/// <see cref="JsonException.LineNumber"/> counts <c>\n</c> line feeds from zero, and
	/// <see cref="JsonException.BytePositionInLine"/> counts UTF-8 bytes rather than characters, so
	/// the position is walked through the line a character at a time. It is clamped to the line, so
	/// a position past its end points at the end of the line.
	/// </remarks>
	private Location LocationOf(JsonException ex)
	{
		if (sourceText is null || ex.LineNumber is not long lineNumber || lineNumber < 0)
		{
			return Location.None;
		}

		int index = LineStart(lineNumber);
		if (index < 0)
		{
			return Location.None;
		}

		long bytes = ex.BytePositionInLine ?? 0;
		while (bytes > 0 && index < Text.Length && !EndsLine(index))
		{
			int width = CharactersAt(index);
			bytes -= Utf8Bytes(index, width);
			index += width;
		}

		int length = index < Text.Length && !EndsLine(index) ? 1 : 0;
		return LocationAt(index, length);
	}

	/// <summary>
	/// Says whether the character at an offset ends a line as System.Text.Json counts lines: a
	/// <c>\n</c>, or the <c>\r</c> of a <c>\r\n</c> pair.
	/// </summary>
	/// <param name="index">The offset of the character.</param>
	/// <returns><see langword="true"/> when the character ends the line.</returns>
	/// <remarks>
	/// A lone <c>\r</c> is not a line break to System.Text.Json, so in a file with CR-only line
	/// endings it reports every position on line zero, measured from the start of the file. Stopping
	/// at that <c>\r</c> would clamp the position to the end of the first line.
	/// </remarks>
	private bool EndsLine(int index) =>
		Text[index] == '\n' || (Text[index] == '\r' && index + 1 < Text.Length && Text[index + 1] == '\n');

	/// <summary>
	/// Finds where a line starts, counting lines by <c>\n</c> line feeds as System.Text.Json does.
	/// </summary>
	/// <param name="lineNumber">The zero-based line number.</param>
	/// <returns>The offset of the line's first character, or -1 when the file has fewer lines.</returns>
	private int LineStart(long lineNumber)
	{
		int index = 0;
		for (long line = 0; line < lineNumber && index >= 0; line++)
		{
			int lineFeed = Text.IndexOf('\n', index);
			index = lineFeed < 0 ? -1 : lineFeed + 1;
		}

		return index;
	}

	/// <summary>
	/// Counts the characters that make up the code point at an offset: two for a surrogate pair,
	/// otherwise one.
	/// </summary>
	/// <param name="index">The offset of the code point's first character.</param>
	/// <returns>The number of characters in the code point.</returns>
	private int CharactersAt(int index) =>
		char.IsHighSurrogate(Text[index]) && index + 1 < Text.Length && char.IsLowSurrogate(Text[index + 1]) ? 2 : 1;

	/// <summary>
	/// Counts the bytes UTF-8 uses for the code point at an offset.
	/// </summary>
	/// <param name="index">The offset of the code point's first character.</param>
	/// <param name="characters">The number of characters in the code point.</param>
	/// <returns>The code point's length in UTF-8.</returns>
	private int Utf8Bytes(int index, int characters) =>
		characters == 2 ? 4 : Text[index] switch
		{
			< '\u0080' => 1,
			< '\u0800' => 2,
			_ => 3,
		};

	private Location LocationAt(int index, int length)
	{
		TextSpan span = new(index, length);
		return Location.Create(FilePath, span, sourceText!.Lines.GetLinePositionSpan(span));
	}
}

/// <summary>
/// The metadata files a generator asked for, keyed by file name.
/// </summary>
public sealed class MetadataSet
{
	private readonly IReadOnlyDictionary<string, MetadataFile> byName;

	/// <summary>
	/// Initializes a new instance of the <see cref="MetadataSet"/> class.
	/// </summary>
	/// <param name="files">The files that were found, keyed by name without directory.</param>
	/// <exception cref="ArgumentNullException"><paramref name="files"/> is <see langword="null"/>.</exception>
	public MetadataSet(IReadOnlyDictionary<string, MetadataFile> files)
	{
		Ensure.NotNull(files);
		byName = files;
	}

	/// <summary>
	/// Gets the named metadata file.
	/// </summary>
	/// <param name="fileName">The file name the generator declared.</param>
	/// <returns>The file, or <see langword="null"/> when it was not supplied to the compilation.</returns>
	public MetadataFile? this[string fileName] =>
		byName.TryGetValue(fileName, out MetadataFile? file) ? file : null;
}
