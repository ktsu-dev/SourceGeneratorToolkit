// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.SourceGeneratorToolkit.Testing;

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

/// <summary>
/// Runs an incremental generator over a set of metadata files and hands back what it produced.
/// </summary>
/// <remarks>
/// Standing a generator up under <see cref="CSharpGeneratorDriver"/> is fiddly — reference
/// resolution, an <see cref="AdditionalText"/> shim, and one packaging escape hatch — and none of it
/// is specific to any one generator, so it ships rather than being rediscovered by the next project
/// that writes one.
/// <para>
/// The escape hatch: a generator project normally bundles its dependencies alongside itself so they
/// are present at analyzer load time, usually through a <c>GetDependencyTargetPaths</c> target. A
/// test project referencing that generator as a plain library rather than as an analyzer — which is
/// what driving it through <see cref="CSharpGeneratorDriver"/> requires — must opt out of the
/// bundling, or the bundled <c>netstandard2.0</c> facades land in its own compile references and
/// collide with the in-box types (CS0433 on <c>ReadOnlySpan&lt;T&gt;</c>, <c>Vector4</c>, and
/// friends). Give the generator's <c>ProjectReference</c> an
/// <c>AdditionalProperties="BundleAnalyzerDependencies=false"</c>, and gate the bundling target on
/// that property.
/// </para>
/// </remarks>
/// <param name="metadataDirectory">The directory the metadata files were copied to.</param>
public sealed class GeneratorHarness(string metadataDirectory)
{
	/// <summary>
	/// Runs a generator against every metadata file in the directory.
	/// </summary>
	/// <param name="generator">The generator to run.</param>
	/// <param name="overrides">
	/// Metadata to substitute or add, keyed by file name. An entry replaces the real file's contents;
	/// a name that is not a real file is added.
	/// </param>
	/// <returns>The generator's run result.</returns>
	/// <remarks>
	/// Every metadata file in the directory is supplied, the way MSBuild's
	/// <c>AdditionalFiles Include="Metadata/*.json"</c> item group supplies them. Handing a generator
	/// only the one file the test is interested in is not how it runs for real, and a generator that
	/// reads two files would report one of them missing.
	/// </remarks>
	public GeneratorRunResult Run(
		IIncrementalGenerator generator,
		IReadOnlyDictionary<string, string>? overrides = null) =>
		RunAll([generator], overrides).Results[0];

	/// <summary>
	/// Runs a generator against a metadata set that contains only the named files.
	/// </summary>
	/// <param name="generator">The generator to run.</param>
	/// <param name="fileNames">The metadata file names to supply.</param>
	/// <returns>The generator's run result.</returns>
	/// <remarks>
	/// The counterpart to <see cref="Run"/>: this is how a test asserts what a generator does when a
	/// file it declared is absent.
	/// </remarks>
	public GeneratorRunResult RunWithOnly(IIncrementalGenerator generator, params string[] fileNames)
	{
		List<AdditionalText> texts = [];
		foreach (string fileName in fileNames ?? [])
		{
			texts.Add(new InMemoryAdditionalText(
				Path.Combine(metadataDirectory, fileName),
				File.ReadAllText(Path.Combine(metadataDirectory, fileName))));
		}

		return Drive([generator], texts).Results[0];
	}

	/// <summary>
	/// Runs generators against the metadata and returns the whole driver result, so a caller can
	/// inspect tracked steps as well as output.
	/// </summary>
	/// <param name="generators">The generators to run.</param>
	/// <param name="overrides">Metadata to substitute or add, keyed by file name.</param>
	/// <returns>The driver's run result.</returns>
	public GeneratorDriverRunResult RunAll(
		IReadOnlyList<IIncrementalGenerator> generators,
		IReadOnlyDictionary<string, string>? overrides = null) =>
		Drive(generators, BuildTexts(overrides));

	/// <summary>
	/// Runs a generator twice over identical metadata and reports whether the second run reused the
	/// first run's cached outputs.
	/// </summary>
	/// <param name="generator">The generator to run.</param>
	/// <returns>True when no tracked output step had to be recomputed on the second run.</returns>
	/// <remarks>
	/// These are <see cref="IIncrementalGenerator"/>s, and output assertions do not check that they
	/// behave like one: a generator that recomputes everything on every keystroke still passes every
	/// output assertion, it just makes the IDE slow.
	/// </remarks>
	public bool ReusesCachedOutputOnRerun(IIncrementalGenerator generator)
	{
		List<AdditionalText> texts = BuildTexts(null);
		CSharpCompilation compilation = CreateCompilation();

		GeneratorDriver driver = CSharpGeneratorDriver.Create(
			generators: [generator.AsSourceGenerator()],
			additionalTexts: texts,
			parseOptions: null,
			optionsProvider: null,
			driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

		driver = driver.RunGenerators(compilation);
		GeneratorDriverRunResult second = driver.RunGenerators(compilation).GetRunResult();

		return !second.Results[0].TrackedOutputSteps
			.SelectMany(pair => pair.Value)
			.SelectMany(step => step.Outputs)
			.Any(output => output.Reason is not (IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged));
	}

	private List<AdditionalText> BuildTexts(IReadOnlyDictionary<string, string>? overrides)
	{
		Dictionary<string, string> byName = [];
		foreach (string path in Directory.GetFiles(metadataDirectory, "*.json"))
		{
			byName[Path.GetFileName(path)] = File.ReadAllText(path);
		}

		if (overrides is not null)
		{
			foreach (KeyValuePair<string, string> entry in overrides)
			{
				byName[entry.Key] = entry.Value;
			}
		}

		List<AdditionalText> texts = [];
		foreach (KeyValuePair<string, string> entry in byName)
		{
			texts.Add(new InMemoryAdditionalText(Path.Combine(metadataDirectory, entry.Key), entry.Value));
		}

		return texts;
	}

	private static GeneratorDriverRunResult Drive(IReadOnlyList<IIncrementalGenerator> generators, List<AdditionalText> texts)
	{
		GeneratorDriver driver = CSharpGeneratorDriver.Create(
			generators: [.. generators.Select(generator => generator.AsSourceGenerator())],
			additionalTexts: texts);

		return driver.RunGenerators(CreateCompilation()).GetRunResult();
	}

	private static CSharpCompilation CreateCompilation() =>
		CSharpCompilation.Create(
			assemblyName: "GeneratorHost",
			syntaxTrees: [],
			references: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
			options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

	/// <summary>
	/// Supplies metadata JSON to the generators the same way MSBuild's AdditionalFiles would.
	/// </summary>
	private sealed class InMemoryAdditionalText(string path, string text) : AdditionalText
	{
		public override string Path { get; } = path;

		public override SourceText GetText(CancellationToken cancellationToken = default) =>
			SourceText.From(text);
	}
}
