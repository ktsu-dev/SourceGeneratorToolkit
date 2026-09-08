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
public sealed class MetadataFile(string fileName, string text, SourceText? sourceText, string path)
{
	/// <summary>
	/// Allocated once rather than per call: constructing <see cref="JsonSerializerOptions"/> inside
	/// the method builds a fresh serializer cache every time (CA1869), which for a generator that
	/// re-reads its metadata on every compilation is the expensive half of the work.
	/// </summary>
	private static readonly JsonSerializerOptions DeserializeOptions = new()
	{
		PropertyNameCaseInsensitive = true
	};

	/// <summary>Gets the file's name, without its directory.</summary>
	public string FileName { get; } = fileName;

	/// <summary>Gets the file's contents.</summary>
	public string Text { get; } = text;

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

		int index = Text.IndexOf(needle, StringComparison.Ordinal);
		return index < 0 ? Location.None : LocationAt(index, needle.Length);
	}

	/// <summary>
	/// Finds <paramref name="needle"/> at or after the first occurrence of <paramref name="anchor"/>,
	/// and returns a location covering it.
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
	/// </remarks>
	public Location FindLocation(string anchor, string needle)
	{
		if (sourceText is null || string.IsNullOrEmpty(needle))
		{
			return Location.None;
		}

		int scope = string.IsNullOrEmpty(anchor) ? 0 : Text.IndexOf(anchor, StringComparison.Ordinal);
		if (scope < 0)
		{
			return FindLocation(needle);
		}

		int index = Text.IndexOf(needle, scope, StringComparison.Ordinal);
		return index < 0 ? FindLocation(needle) : LocationAt(index, needle.Length);
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
	/// </remarks>
	public T? Deserialize<T>(SourceProductionContext context, DiagnosticDescriptor parseFailed)
		where T : class
	{
		try
		{
			T? metadata = JsonSerializer.Deserialize<T>(Text, DeserializeOptions);
			if (metadata is not null)
			{
				return metadata;
			}

			context.Report(parseFailed, FileName, "the document deserialized to null");
			return null;
		}
		catch (JsonException ex)
		{
			context.Report(parseFailed, FileName, ex.Message);
			return null;
		}
	}

	private Location LocationAt(int index, int length)
	{
		TextSpan span = new(index, length);
		return Location.Create(path, span, sourceText!.Lines.GetLinePositionSpan(span));
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
