// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.SourceGeneratorToolkit.Test;

using System.Collections.Generic;
using ktsu.CodeBlocker;
using ktsu.CodeBlocker.Templates;
using Microsoft.CodeAnalysis;

/// <summary>
/// A metadata shape, standing in for the domain models a real generator deserializes.
/// </summary>
public sealed class ThingsMetadata
{
	public List<ThingDefinition> Things { get; set; } = [];
}

/// <summary>One entry in <see cref="ThingsMetadata"/>.</summary>
public sealed class ThingDefinition
{
	public string Name { get; set; } = string.Empty;

	public string Kind { get; set; } = string.Empty;
}

/// <summary>The other metadata shape, so multi-file loading has something to load.</summary>
public sealed class OthersMetadata
{
	public List<ThingDefinition> Others { get; set; } = [];
}

/// <summary>
/// The diagnostics the test generators report, allocated the way a consuming repository allocates
/// its own: one catalogue, one prefix, one category.
/// </summary>
internal static class TestDiagnostics
{
	internal const string Copyright = "Copyright (c) 2023-2026 ktsu-dev contributors";

	internal static DiagnosticCatalog Catalog { get; } = new("TST", "SourceGeneratorToolkit.Test");

	internal static DiagnosticDescriptor MetadataFileMissing { get; } = Catalog.Warning(
		1,
		"A metadata file is missing from the compilation",
		"Metadata file '{0}' was not supplied as an AdditionalFile.");

	internal static DiagnosticDescriptor MetadataParseFailed { get; } = Catalog.Error(
		2,
		"A metadata file could not be parsed",
		"Metadata file '{0}' could not be parsed: {1}");
}

/// <summary>
/// Emits one type per entry in <c>things.json</c>. Exercises <see cref="GeneratorBase{T}"/>.
/// </summary>
internal sealed class ThingsGenerator() : GeneratorBase<ThingsMetadata>("things.json")
{
	protected override DiagnosticCatalog Diagnostics => TestDiagnostics.Catalog;

	protected override DiagnosticDescriptor MetadataFileMissing => TestDiagnostics.MetadataFileMissing;

	protected override DiagnosticDescriptor MetadataParseFailed => TestDiagnostics.MetadataParseFailed;

	protected override void Generate(SourceProductionContext context, ThingsMetadata metadata, CodeBlocker codeBlocker)
	{
		SourceFileTemplate file = new()
		{
			Namespace = "Generated",
		};

		foreach (ThingDefinition thing in metadata.Things)
		{
			file.Classes.Add(new ClassTemplate
			{
				Name = thing.Name,
				Kind = ktsu.CodeBlocker.Templates.TypeKind.Class,
				Keywords = { CSharpKeywords.Public },
			});
		}

		WriteSourceFile(codeBlocker, file, TestDiagnostics.Copyright);
		context.AddSource("Things.g.cs", codeBlocker.ToString());
	}
}

/// <summary>
/// Reads both metadata files. Exercises <see cref="GeneratorBase"/> directly, which is the shape
/// that used to be reimplemented per generator to get around a single-file base.
/// </summary>
internal sealed class PairGenerator : GeneratorBase
{
	protected override IReadOnlyList<string> MetadataFileNames => ["things.json", "others.json"];

	protected override DiagnosticCatalog Diagnostics => TestDiagnostics.Catalog;

	protected override DiagnosticDescriptor MetadataFileMissing => TestDiagnostics.MetadataFileMissing;

	protected override DiagnosticDescriptor MetadataParseFailed => TestDiagnostics.MetadataParseFailed;

	protected override void Generate(SourceProductionContext context, MetadataSet metadata)
	{
		ThingsMetadata? things = metadata["things.json"]?.Deserialize<ThingsMetadata>(context, MetadataParseFailed);
		OthersMetadata? others = metadata["others.json"]?.Deserialize<OthersMetadata>(context, MetadataParseFailed);

		if (things is null || others is null)
		{
			return;
		}

		using CodeBlocker codeBlocker = CreateCodeBlocker();
		WriteFileHeader(codeBlocker, TestDiagnostics.Copyright);
		codeBlocker.WriteLine($"// {things.Things.Count} things, {others.Others.Count} others");
		context.AddSource("Pair.g.cs", codeBlocker.ToString());
	}
}

/// <summary>
/// Declares a file nothing supplies, so the missing-file path has a generator to run.
/// </summary>
internal sealed class AbsentFileGenerator() : GeneratorBase<ThingsMetadata>("nowhere.json")
{
	protected override DiagnosticCatalog Diagnostics => TestDiagnostics.Catalog;

	protected override DiagnosticDescriptor MetadataFileMissing => TestDiagnostics.MetadataFileMissing;

	protected override DiagnosticDescriptor MetadataParseFailed => TestDiagnostics.MetadataParseFailed;

	protected override void Generate(SourceProductionContext context, ThingsMetadata metadata, CodeBlocker codeBlocker) =>
		context.AddSource("Nowhere.g.cs", "// unreachable");
}
