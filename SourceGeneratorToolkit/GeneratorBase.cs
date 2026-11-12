// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.SourceGeneratorToolkit;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using ktsu.CodeBlocker;
using ktsu.CodeBlocker.Templates;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

/// <summary>
/// Base class for a generator driven by one or more JSON metadata files supplied as
/// <c>AdditionalFiles</c>.
/// </summary>
/// <remarks>
/// A derived generator declares the files it reads through <see cref="MetadataFileNames"/> and
/// emits from <see cref="Generate"/>; finding the files, and reporting anything missing or
/// malformed, happens here. Declaring more than one file is the normal case rather than a reason to
/// reimplement <see cref="Initialize"/>.
/// <para>
/// The diagnostics are supplied by the derived class rather than allocated here, so a consuming
/// repository keeps one catalogue with one identifier prefix. Binding them, and the file header,
/// in a repository-local base class is the intended seam.
/// </para>
/// </remarks>
public abstract class GeneratorBase : IIncrementalGenerator
{
	/// <summary>
	/// Gets the names of the metadata files this generator reads, without their directories.
	/// </summary>
	protected abstract IReadOnlyList<string> MetadataFileNames { get; }

	/// <summary>
	/// Gets the catalogue this generator's diagnostics are allocated from.
	/// </summary>
	/// <remarks>
	/// <see cref="MetadataFileMissing"/> and <see cref="MetadataParseFailed"/> must both be allocated
	/// from it. <see cref="Initialize"/> throws otherwise, so the two diagnostics this base reports
	/// cannot drift to a prefix other than the one the generator's own tests enumerate.
	/// </remarks>
	protected abstract DiagnosticCatalog Diagnostics { get; }

	/// <summary>
	/// Gets the descriptor reported when a declared metadata file is not in the compilation. Its
	/// message format takes the file name.
	/// </summary>
	protected abstract DiagnosticDescriptor MetadataFileMissing { get; }

	/// <summary>
	/// Gets the descriptor reported when a metadata file cannot be parsed. Its message format takes
	/// the file name and the reason.
	/// </summary>
	/// <remarks>
	/// Also reported when the compilation holds several files of one declared name with different
	/// contents. The reason then lists their paths, and none of them is handed to
	/// <see cref="Generate"/>.
	/// </remarks>
	protected abstract DiagnosticDescriptor MetadataParseFailed { get; }

	/// <inheritdoc/>
	/// <exception cref="InvalidOperationException">
	/// <see cref="Diagnostics"/> is <see langword="null"/>, or <see cref="MetadataFileMissing"/> or
	/// <see cref="MetadataParseFailed"/> was not allocated from it.
	/// </exception>
	public void Initialize(IncrementalGeneratorInitializationContext context)
	{
		EnsureCataloged();

		IReadOnlyList<string> wanted = MetadataFileNames;

		IncrementalValueProvider<ImmutableArray<MetadataFile>> metadataFiles = context.AdditionalTextsProvider
			.Where(file => wanted.Any(name => IsNamed(file.Path, name)))
			.Select((file, cancellationToken) =>
			{
				SourceText? sourceText = file.GetText(cancellationToken);
				return new MetadataFile(NameOf(file.Path), sourceText?.ToString() ?? string.Empty, sourceText, file.Path);
			})
			.Collect();

		IncrementalValueProvider<(ImmutableArray<MetadataFile> Files, ParseOptions Options)> inputs =
			metadataFiles.Combine(context.ParseOptionsProvider);

		context.RegisterSourceOutput(inputs, (productionContext, input) =>
		{
			ImmutableArray<MetadataFile> files = input.Files;

			// A duplicate name with identical contents means the same metadata reached the compilation
			// twice; the first with content wins. An empty file is still present, so it is handed on
			// and fails to parse rather than being reported as missing.
			Dictionary<string, MetadataFile> byName = new(StringComparer.Ordinal);
			HashSet<string> conflicted = new(StringComparer.Ordinal);
			foreach (IGrouping<string, MetadataFile> group in files.GroupBy(file => file.FileName, StringComparer.Ordinal))
			{
				// Same-named files with different contents are not one file supplied twice. Picking one
				// would generate from whichever the build happened to list first, with nothing to say
				// the other was ignored, so neither is used and both paths are reported.
				MetadataFile[] withContent = [.. group.Where(file => file.Text.Length > 0)];
				if (withContent.Select(file => file.Text).Distinct(StringComparer.Ordinal).Skip(1).Any())
				{
					conflicted.Add(group.Key);
					productionContext.Report(
						MetadataParseFailed,
						group.Key,
						"it was supplied more than once with different contents: "
							+ string.Join(", ", withContent.Select(file => file.FilePath)));
					continue;
				}

				byName[group.Key] = withContent.FirstOrDefault() ?? group.First();
			}

			// A missing file must say so. Producing no output and no explanation is indistinguishable
			// from a generator that simply had nothing to emit.
			foreach (string name in wanted.Where(name => !byName.ContainsKey(name) && !conflicted.Contains(name)))
			{
				productionContext.Report(MetadataFileMissing, name);
			}

			RunForLanguageVersion(
				(input.Options as CSharpParseOptions)?.LanguageVersion,
				() => Generate(productionContext, new MetadataSet(byName)));
		});
	}

	// A required catalogue that nothing reads looks enforced and isn't: a generator could report its
	// two built-in diagnostics under a prefix other than the one it releases and tests, which is the
	// drift DiagnosticCatalog exists to prevent. Thrown from Initialize, the failure reaches the
	// consumer's build as the driver's CS8784 warning naming this message, and the generator emits
	// nothing.
	private void EnsureCataloged()
	{
		DiagnosticCatalog catalog = Diagnostics
			?? throw new InvalidOperationException($"{GetType().Name}.Diagnostics returned null. A generator must name the catalogue its diagnostics are allocated from.");

		EnsureCataloged(catalog, MetadataFileMissing, nameof(MetadataFileMissing));
		EnsureCataloged(catalog, MetadataParseFailed, nameof(MetadataParseFailed));
	}

	private void EnsureCataloged(DiagnosticCatalog catalog, DiagnosticDescriptor? descriptor, string member)
	{
		if (descriptor is not null && catalog.Descriptors.Any(cataloged => cataloged.Id == descriptor.Id))
		{
			return;
		}

		string cataloged = catalog.Descriptors.Count == 0
			? "none"
			: string.Join(", ", catalog.Descriptors.Select(entry => entry.Id));
		throw new InvalidOperationException(
			$"{GetType().Name}.{member} ({descriptor?.Id ?? "null"}) was not allocated from {GetType().Name}.Diagnostics, "
				+ $"the '{catalog.Category}' catalogue (descriptors: {cataloged}). Allocate it from that catalogue.");
	}

	/// <summary>
	/// Runs an action with the consumer's language version available to <see cref="WriteFileHeader(CodeBlocker, string?)"/>.
	/// </summary>
	/// <param name="languageVersion">The consuming compilation's language version, if known.</param>
	/// <param name="action">The action to run.</param>
	/// <remarks>
	/// Held for the duration of the action so the static header writer, which has no access to the
	/// compilation, can still match its output to the consumer's language version.
	/// </remarks>
	private static void RunForLanguageVersion(LanguageVersion? languageVersion, Action action)
	{
		LanguageVersion? previous = consumerLanguageVersion;
		consumerLanguageVersion = languageVersion;
		try
		{
			action();
		}
		finally
		{
			consumerLanguageVersion = previous;
		}
	}

	/// <summary>
	/// Emits this generator's sources.
	/// </summary>
	/// <param name="context">The source production context to add sources to.</param>
	/// <param name="metadata">The metadata files this generator declared.</param>
	protected abstract void Generate(SourceProductionContext context, MetadataSet metadata);

	/// <summary>
	/// Creates a <see cref="CodeBlocker"/> configured the way generated sources are written.
	/// </summary>
	/// <returns>A new <see cref="CodeBlocker"/>.</returns>
	/// <remarks>
	/// The line terminator is pinned rather than inherited from the host. Generator output that is
	/// committed and verified by CI has to be byte-identical wherever it was produced, and
	/// <see cref="System.CodeDom.Compiler.IndentedTextWriter"/> uses
	/// <see cref="System.Environment.NewLine"/> — which makes the same generator emit different
	/// bytes on Windows and Linux. LF, to match the <c>* text=auto eol=lf</c> that a repository
	/// storing generated sources wants in its <c>.gitattributes</c>.
	/// </remarks>
	protected static CodeBlocker CreateCodeBlocker() =>
		CodeBlocker.Create(CodeBlocker.DefaultIndentString, NewLines.Lf);

	/// <summary>
	/// Every line terminator the C# compiler recognises, which a copyright is split on so no text escapes its
	/// comment. Longest first, so CRLF is one break rather than two.
	/// </summary>
	private static readonly string[] CopyrightLineBreaks = ["\r\n", "\n", "\r", "\u0085", "\u2028", "\u2029"];

	/// <summary>
	/// The language version of the compilation the generator is currently producing sources for, or
	/// <see langword="null"/> outside <see cref="Generate"/>.
	/// </summary>
	[ThreadStatic]
	private static LanguageVersion? consumerLanguageVersion;

	/// <summary>
	/// Writes the header every generated file starts with.
	/// </summary>
	/// <param name="codeBlocker">The <see cref="CodeBlocker"/> to write to.</param>
	/// <param name="copyright">
	/// The copyright written above the generated-file marker, without its <c>//</c> prefix.
	/// <see langword="null"/> or empty writes only the marker. A multi-line copyright is commented
	/// line by line at every line terminator the C# compiler recognises (CR, LF, CRLF, NEL,
	/// LINE SEPARATOR and PARAGRAPH SEPARATOR), so a consumer's multi-line file header template
	/// still compiles.
	/// </param>
	/// <remarks>
	/// A parameter rather than a hard-coded literal, so a consuming repository can keep the header in
	/// step with its own file header template from one place, and so nothing about any one
	/// repository leaks into this package.
	/// <para>
	/// The header ends with <c>#nullable enable</c>. Roslyn compiles a file carrying the
	/// generated-file marker with the nullable context disabled, whatever the consuming project's
	/// <c>Nullable</c> setting, so without the directive every <c>?</c> annotation in the output
	/// raises CS8669 and every unannotated reference member is null-oblivious to its callers.
	/// </para>
	/// <para>
	/// <c>#nullable</c> needs C# 8, so it is left out when the consuming compilation is on an earlier
	/// language version, such as the C# 7.3 a <c>netstandard2.0</c> or .NET Framework project gets by
	/// default. Called from <see cref="Generate"/>, the version is the consuming compilation's; called
	/// anywhere else, it is unknown and the directive is written. Use the overload that takes a
	/// <see cref="LanguageVersion"/> to choose explicitly.
	/// </para>
	/// </remarks>
	/// <exception cref="ArgumentNullException"><paramref name="codeBlocker"/> is <see langword="null"/>.</exception>
	protected static void WriteFileHeader(CodeBlocker codeBlocker, string? copyright) =>
		WriteFileHeader(codeBlocker, copyright, consumerLanguageVersion ?? LanguageVersion.Latest);

	/// <summary>
	/// Writes the header every generated file starts with, for a consumer on the given language version.
	/// </summary>
	/// <param name="codeBlocker">The <see cref="CodeBlocker"/> to write to.</param>
	/// <param name="copyright">The copyright, as for <see cref="WriteFileHeader(CodeBlocker, string?)"/>.</param>
	/// <param name="languageVersion">
	/// The consuming compilation's language version. <c>#nullable enable</c> is written only for C# 8
	/// and later.
	/// </param>
	/// <exception cref="ArgumentNullException"><paramref name="codeBlocker"/> is <see langword="null"/>.</exception>
	protected static void WriteFileHeader(CodeBlocker codeBlocker, string? copyright, LanguageVersion languageVersion)
	{
		Ensure.NotNull(codeBlocker);

		if (!string.IsNullOrEmpty(copyright))
		{
			foreach (string line in copyright!.Split(CopyrightLineBreaks, StringSplitOptions.None))
			{
				codeBlocker.WriteLine(line.Length == 0 ? "//" : $"// {line}");
			}
		}

		codeBlocker.WriteLine("// <auto-generated />");
		if (languageVersion.MapSpecifiedToEffectiveVersion() >= LanguageVersion.CSharp8)
		{
			codeBlocker.WriteLine("#nullable enable");
		}

		codeBlocker.NewLine();
	}

	/// <summary>
	/// Writes a whole source file, header included.
	/// </summary>
	/// <param name="codeBlocker">The <see cref="CodeBlocker"/> to write to.</param>
	/// <param name="sourceFileTemplate">The file to write.</param>
	/// <param name="copyright">The copyright line, as for <see cref="WriteFileHeader(CodeBlocker, string?)"/>.</param>
	/// <remarks>
	/// As with <see cref="WriteFileHeader(CodeBlocker, string?)"/>, the output matches the consuming
	/// compilation's language version when called from <see cref="Generate"/>, and the latest version
	/// anywhere else. Use the overload that takes a <see cref="LanguageVersion"/> to choose explicitly.
	/// </remarks>
	/// <exception cref="ArgumentNullException">
	/// <paramref name="codeBlocker"/> or <paramref name="sourceFileTemplate"/> is <see langword="null"/>.
	/// </exception>
	protected static void WriteSourceFile(CodeBlocker codeBlocker, SourceFileTemplate sourceFileTemplate, string? copyright) =>
		WriteSourceFile(codeBlocker, sourceFileTemplate, copyright, consumerLanguageVersion ?? LanguageVersion.Latest);

	/// <summary>
	/// Writes a whole source file, header included, for a consumer on the given language version.
	/// </summary>
	/// <param name="codeBlocker">The <see cref="CodeBlocker"/> to write to.</param>
	/// <param name="sourceFileTemplate">The file to write.</param>
	/// <param name="copyright">The copyright line, as for <see cref="WriteFileHeader(CodeBlocker, string?)"/>.</param>
	/// <param name="languageVersion">
	/// The consuming compilation's language version. The template's namespace is file-scoped for C# 10
	/// and later, and a block around the file's contents before that, where a file-scoped namespace
	/// does not compile (CS8370/CS8773).
	/// </param>
	/// <exception cref="ArgumentNullException">
	/// <paramref name="codeBlocker"/> or <paramref name="sourceFileTemplate"/> is <see langword="null"/>.
	/// </exception>
	protected static void WriteSourceFile(CodeBlocker codeBlocker, SourceFileTemplate sourceFileTemplate, string? copyright, LanguageVersion languageVersion)
	{
		Ensure.NotNull(sourceFileTemplate);

		WriteFileHeader(codeBlocker, copyright, languageVersion);
		if (string.IsNullOrEmpty(sourceFileTemplate.Namespace)
			|| languageVersion.MapSpecifiedToEffectiveVersion() >= LanguageVersion.CSharp10)
		{
			codeBlocker.AddSourceFile(sourceFileTemplate);
			return;
		}

		// ktsu.CodeBlocker only writes file-scoped namespaces, so the block is written here around a
		// copy of the template without one. What can't live inside a namespace block is written first,
		// from a copy holding nothing else: the file's documentation and comments, and its global
		// (assembly:/module:) attributes, which C# only accepts after every using and before the
		// namespace. A file with attributes writes its usings there too, so the attributes can be named
		// through them, as CodeBlocker does for a file-scoped namespace; otherwise the usings stay
		// inside the block.
		bool hasAttributes = sourceFileTemplate.Attributes.Count > 0;
		SourceFileTemplate outsideNamespace = new()
		{
			FileName = sourceFileTemplate.FileName,
			Documentation = sourceFileTemplate.Documentation,
		};
		SourceFileTemplate withoutNamespace = new()
		{
			FileName = sourceFileTemplate.FileName,
		};

		foreach (string comment in sourceFileTemplate.Comments)
		{
			outsideNamespace.Comments.Add(comment);
		}

		foreach (string attribute in sourceFileTemplate.Attributes)
		{
			outsideNamespace.Attributes.Add(attribute);
		}

		foreach (string usingDirective in sourceFileTemplate.Usings)
		{
			(hasAttributes ? outsideNamespace : withoutNamespace).Usings.Add(usingDirective);
		}

		foreach (ClassTemplate classTemplate in sourceFileTemplate.Classes)
		{
			withoutNamespace.Classes.Add(classTemplate);
		}

		if (hasAttributes || outsideNamespace.Comments.Count > 0 || outsideNamespace.Documentation is not null)
		{
			codeBlocker.AddSourceFile(outsideNamespace);
		}

		codeBlocker.WriteLine($"namespace {sourceFileTemplate.Namespace}");
		using (new Scope(codeBlocker))
		{
			codeBlocker.AddSourceFile(withoutNamespace);
		}
	}

	/// <summary>
	/// Whether a path names the given file.
	/// </summary>
	/// <param name="path">The additional file's path.</param>
	/// <param name="fileName">The file name to match.</param>
	/// <returns>True when the path's last segment is exactly <paramref name="fileName"/>.</returns>
	/// <remarks>
	/// Matched on the whole last segment rather than with <c>EndsWith</c>, which also matches
	/// anything whose name merely ends with the wanted one.
	/// </remarks>
	private static bool IsNamed(string path, string fileName) =>
		string.Equals(NameOf(path), fileName, StringComparison.Ordinal);

	/// <summary>
	/// The last segment of a path, handling either directory separator so the generator behaves the
	/// same wherever it runs.
	/// </summary>
	/// <param name="path">The path to take the name of.</param>
	/// <returns>The path's last segment.</returns>
	private static string NameOf(string path)
	{
		int separator = path.LastIndexOfAny(['/', '\\']);
		return separator < 0 ? path : path[(separator + 1)..];
	}
}

/// <summary>
/// Base class for a generator driven by exactly one JSON metadata file.
/// </summary>
/// <typeparam name="T">The shape the metadata file deserializes into.</typeparam>
/// <param name="metadataFileName">The metadata file's name, without its directory.</param>
public abstract class GeneratorBase<T>(string metadataFileName) : GeneratorBase
	where T : class
{
	/// <inheritdoc/>
	protected sealed override IReadOnlyList<string> MetadataFileNames => [metadataFileName];

	/// <summary>
	/// Gets the options the metadata file is deserialized with. Defaults to
	/// <see cref="MetadataFile.DefaultSerializerOptions"/>.
	/// </summary>
	/// <remarks>
	/// Override to add converters, a naming policy or stricter reading. Return a cached
	/// <see langword="static"/> <see langword="readonly"/> instance rather than a new one per call:
	/// constructing <see cref="JsonSerializerOptions"/> per call builds a fresh serializer cache every
	/// time (CA1869).
	/// </remarks>
	protected virtual JsonSerializerOptions MetadataSerializerOptions => MetadataFile.DefaultSerializerOptions;

	/// <inheritdoc/>
	protected sealed override void Generate(SourceProductionContext context, MetadataSet metadata)
	{
		T? deserialized = metadata?[metadataFileName]?.Deserialize<T>(context, MetadataParseFailed, MetadataSerializerOptions);
		if (deserialized is null)
		{
			return;
		}

		using CodeBlocker codeBlocker = CreateCodeBlocker();
		Generate(context, deserialized, codeBlocker);
	}

	/// <summary>
	/// Emits this generator's sources from its metadata.
	/// </summary>
	/// <param name="context">The source production context to add sources to.</param>
	/// <param name="metadata">The deserialized metadata.</param>
	/// <param name="codeBlocker">A <see cref="CodeBlocker"/> to build the output in.</param>
	protected abstract void Generate(SourceProductionContext context, T metadata, CodeBlocker codeBlocker);
}
