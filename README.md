# ktsu.SourceGeneratorToolkit

> Reusable infrastructure for Roslyn source generators driven by JSON metadata.

[![License](https://img.shields.io/github/license/ktsu-dev/SourceGeneratorToolkit.svg?label=License&logo=nuget)](LICENSE.md)
[![NuGet Version](https://img.shields.io/nuget/v/ktsu.SourceGeneratorToolkit?label=Stable&logo=nuget)](https://nuget.org/packages/ktsu.SourceGeneratorToolkit)
[![NuGet Version](https://img.shields.io/nuget/vpre/ktsu.SourceGeneratorToolkit?label=Latest&logo=nuget)](https://nuget.org/packages/ktsu.SourceGeneratorToolkit)
[![NuGet Downloads](https://img.shields.io/nuget/dt/ktsu.SourceGeneratorToolkit?label=Downloads&logo=nuget)](https://nuget.org/packages/ktsu.SourceGeneratorToolkit)
[![GitHub commit activity](https://img.shields.io/github/commit-activity/m/ktsu-dev/SourceGeneratorToolkit?label=Commits&logo=github)](https://github.com/ktsu-dev/SourceGeneratorToolkit/commits/main)
[![GitHub contributors](https://img.shields.io/github/contributors/ktsu-dev/SourceGeneratorToolkit?label=Contributors&logo=github)](https://github.com/ktsu-dev/SourceGeneratorToolkit/graphs/contributors)
[![GitHub Actions Workflow Status](https://img.shields.io/github/actions/workflow/status/ktsu-dev/SourceGeneratorToolkit/dotnet.yml?label=Build&logo=github)](https://github.com/ktsu-dev/SourceGeneratorToolkit/actions)

## Introduction

`ktsu.SourceGeneratorToolkit` is the part of a metadata-driven source generator that is not about
your domain. A generator that reads JSON from `AdditionalFiles` has to find those files, parse
them, say something useful when one is missing or malformed, point a diagnostic at the offending
line, and emit bytes that are identical on every platform — and none of that changes from one
generator to the next.

This package supplies that layer so a generator is left with its own emission logic. It pairs with
[`ktsu.CodeBlocker`](https://github.com/ktsu-dev/CodeBlocker), which owns the C# syntax the
generator writes, and with `ktsu.SourceGeneratorToolkit.Testing`, which drives a generator under
`CSharpGeneratorDriver` the way MSBuild does.

It was extracted from [ktsu-dev/Semantics](https://github.com/ktsu-dev/Semantics), where it
accumulated by being needed twice.

## Features

- **Declare the files, not the plumbing**: A generator names the metadata files it reads through
  `MetadataFileNames` and emits from `Generate`. Reading one file or five is the same amount of code.
- **Silent failures made loud**: A declared file that never reached the compilation, and a file that
  will not parse, each report a diagnostic instead of producing nothing and no explanation.
- **Navigable diagnostics**: `MetadataFile.FindLocation` maps a name from the metadata back to a
  position in the file, so a diagnostic lands on the offending line rather than on nothing. The
  scoped overload anchors on the surrounding entry, for a name that is spelled correctly in dozens
  of places and wrong in one.
- **One diagnostic scheme**: `DiagnosticCatalog` allocates every descriptor under one prefix and
  category, and exposes them as a list — so a test can assert that each one is tracked in
  `AnalyzerReleases.Unshipped.md`, rather than finding out from RS2008 at build time.
- **Deterministic output**: Generated sources are written with LF, not `Environment.NewLine`, so
  output that is committed and verified by CI is byte-identical wherever it was produced.
- **Exact file-name matching**: Metadata is matched on a path's whole last segment. `EndsWith`
  matching also takes `otherthings.json` for `things.json`.
- **Consumer-agnostic**: The diagnostics and the file-header copyright are supplied by the deriving
  class, so nothing about any one repository is baked into the package.
- **A test harness that ships**: `ktsu.SourceGeneratorToolkit.Testing` supplies the metadata the way
  MSBuild's `AdditionalFiles` item group does, and can run a generator twice to prove it reuses its
  cached output.

## Installation

### Package Manager Console

```powershell
Install-Package ktsu.SourceGeneratorToolkit
Install-Package ktsu.SourceGeneratorToolkit.Testing
```

### .NET CLI

```bash
dotnet add package ktsu.SourceGeneratorToolkit
dotnet add package ktsu.SourceGeneratorToolkit.Testing
```

### Package Reference

```xml
<PackageReference Include="ktsu.SourceGeneratorToolkit" Version="x.y.z" />
<PackageReference Include="ktsu.SourceGeneratorToolkit.Testing" Version="x.y.z" />
```

`ktsu.SourceGeneratorToolkit` goes in the generator project; `ktsu.SourceGeneratorToolkit.Testing`
goes in the project that tests it.

## Usage Examples

### Basic Example

Bind the toolkit to your repository once, in a base class of your own. This is the intended seam:
it is where your diagnostic catalogue and your file header live, and it is what keeps the package
itself free of anything specific to you.

```csharp
using ktsu.SourceGeneratorToolkit;
using Microsoft.CodeAnalysis;

public static class MyDiagnostics
{
	public const string Copyright = "Copyright (c) 2026 contributors";

	public static DiagnosticCatalog Catalog { get; } = new("MYG", "MyProject.Generators");

	public static DiagnosticDescriptor MetadataFileMissing { get; } = Catalog.Warning(
		1,
		"A metadata file is missing from the compilation",
		"Metadata file '{0}' was not supplied as an AdditionalFile.");

	public static DiagnosticDescriptor MetadataParseFailed { get; } = Catalog.Error(
		2,
		"A metadata file could not be parsed",
		"Metadata file '{0}' could not be parsed: {1}");
}
```

Then write the generator. Everything above `Generate` is inherited.

```csharp
using ktsu.CodeBlocker;
using ktsu.CodeBlocker.Templates;
using ktsu.SourceGeneratorToolkit;
using Microsoft.CodeAnalysis;

[Generator]
public sealed class ThingsGenerator() : GeneratorBase<ThingsMetadata>("things.json")
{
	protected override DiagnosticCatalog Diagnostics => MyDiagnostics.Catalog;

	protected override DiagnosticDescriptor MetadataFileMissing => MyDiagnostics.MetadataFileMissing;

	protected override DiagnosticDescriptor MetadataParseFailed => MyDiagnostics.MetadataParseFailed;

	protected override void Generate(SourceProductionContext context, ThingsMetadata metadata, CodeBlocker codeBlocker)
	{
		SourceFileTemplate file = new() { Namespace = "Generated" };

		foreach (ThingDefinition thing in metadata.Things)
		{
			file.Classes.Add(new ClassTemplate
			{
				Name = thing.Name,
				Kind = TypeKind.Class,
				Keywords = { CSharpKeywords.Public },
			});
		}

		WriteSourceFile(codeBlocker, file, MyDiagnostics.Copyright);
		context.AddSource("Things.g.cs", codeBlocker.ToString());
	}
}
```

Supply the metadata from the consuming project:

```xml
<ItemGroup>
  <AdditionalFiles Include="Metadata/*.json" />
</ItemGroup>
```

### Reading More Than One Metadata File

Derive from `GeneratorBase` rather than `GeneratorBase<T>` and declare every file. Each one is
deserialized into whatever shape it holds, and each one that is absent reports on its own.

```csharp
[Generator]
public sealed class PairGenerator : GeneratorBase
{
	protected override IReadOnlyList<string> MetadataFileNames => ["things.json", "units.json"];

	protected override DiagnosticCatalog Diagnostics => MyDiagnostics.Catalog;

	protected override DiagnosticDescriptor MetadataFileMissing => MyDiagnostics.MetadataFileMissing;

	protected override DiagnosticDescriptor MetadataParseFailed => MyDiagnostics.MetadataParseFailed;

	protected override void Generate(SourceProductionContext context, MetadataSet metadata)
	{
		ThingsMetadata? things = metadata["things.json"]?.Deserialize<ThingsMetadata>(context, MetadataParseFailed);
		UnitsMetadata? units = metadata["units.json"]?.Deserialize<UnitsMetadata>(context, MetadataParseFailed);

		if (things is null || units is null)
		{
			return;
		}

		using CodeBlocker codeBlocker = CreateCodeBlocker();
		// ... emit
	}
}
```

### Pointing a Diagnostic at the Metadata

A diagnostic reported at `Location.None` names the mistake but not where it is written. Ask the
file where the name is instead.

```csharp
MetadataFile? file = metadata["things.json"];

// The first occurrence — right when the name is a typo, which occurs once.
context.ReportAt(MyDiagnostics.UnknownReference, file?.FindLocation(name), name);

// Anchored on the surrounding entry — right when the name is spelled correctly in dozens of
// places and only wrong in this one.
context.ReportAt(MyDiagnostics.UnknownReference, file?.FindLocation(thing.Name, name), name);
```

### Testing a Generator

```csharp
using ktsu.SourceGeneratorToolkit.Testing;

GeneratorHarness harness = new(Path.Combine(AppContext.BaseDirectory, "Metadata"));

// Runs against every metadata file in the directory, the way MSBuild supplies them.
GeneratorRunResult result = harness.Run(new ThingsGenerator());
Assert.AreEqual(0, result.Diagnostics.Length);

// Substitute one file's contents to exercise a failure path.
GeneratorRunResult malformed = harness.Run(
	new ThingsGenerator(),
	new Dictionary<string, string> { ["things.json"] = "{ not json" });

// Withhold a file to assert what the generator says when it is missing.
GeneratorRunResult partial = harness.RunWithOnly(new PairGenerator(), "things.json");

// Assert it behaves like an incremental generator, not just that its output is right.
Assert.IsTrue(harness.ReusesCachedOutputOnRerun(new ThingsGenerator()));
```

The test project must reference the generator as a plain library rather than as an analyzer, and
must opt out of dependency bundling — otherwise the generator's bundled `netstandard2.0` facades
land in the test project's compile references and collide with the in-box types (CS0433 on
`ReadOnlySpan<T>`, `Vector4`, and friends):

```xml
<ProjectReference Include="..\MyProject.Generators\MyProject.Generators.csproj"
                  AdditionalProperties="BundleAnalyzerDependencies=false" />
```

## API Reference

### `GeneratorBase`

`IIncrementalGenerator` base for a generator driven by one or more JSON metadata files supplied as
`AdditionalFiles`.

#### Properties

| Name | Type | Description |
|------|------|-------------|
| `MetadataFileNames` | `IReadOnlyList<string>` | The metadata files this generator reads, without their directories. Abstract. |
| `Diagnostics` | `DiagnosticCatalog` | The catalogue this generator's diagnostics are allocated from. Abstract. |
| `MetadataFileMissing` | `DiagnosticDescriptor` | Reported when a declared file is not in the compilation. Takes the file name. Abstract. |
| `MetadataParseFailed` | `DiagnosticDescriptor` | Reported when a file cannot be parsed. Takes the file name and the reason. Abstract. |

#### Methods

| Name | Return Type | Description |
|------|-------------|-------------|
| `Initialize(IncrementalGeneratorInitializationContext)` | `void` | Finds the declared files and registers the source output. Sealed behaviour; do not reimplement. |
| `Generate(SourceProductionContext, MetadataSet)` | `void` | Emits this generator's sources. Abstract. |
| `CreateCodeBlocker()` | `CodeBlocker` | A `CodeBlocker` pinned to LF, so output is byte-identical on every platform. Static. |
| `WriteFileHeader(CodeBlocker, string?)` | `void` | Writes the copyright line and the `<auto-generated />` marker. A null or empty copyright writes only the marker. Static. |
| `WriteSourceFile(CodeBlocker, SourceFileTemplate, string?)` | `void` | Writes a whole source file, header included. Static. |

### `GeneratorBase<T>`

Base for a generator driven by exactly one metadata file, deserialized into `T`. Takes the file
name as a constructor argument.

#### Methods

| Name | Return Type | Description |
|------|-------------|-------------|
| `Generate(SourceProductionContext, T, CodeBlocker)` | `void` | Emits this generator's sources from its deserialized metadata. Abstract. |

### `MetadataFile`

One metadata file a generator asked for, with the text it was found to contain.

#### Properties

| Name | Type | Description |
|------|------|-------------|
| `FileName` | `string` | The file's name, without its directory. |
| `Text` | `string` | The file's contents. |

#### Methods

| Name | Return Type | Description |
|------|-------------|-------------|
| `FindLocation(string needle)` | `Location` | A location covering the first occurrence of `needle`, or `Location.None`. |
| `FindLocation(string anchor, string needle)` | `Location` | A location covering the first occurrence of `needle` at or after `anchor`, falling back to the unscoped match and then to `Location.None`. |
| `Deserialize<T>(SourceProductionContext, DiagnosticDescriptor)` | `T?` | Deserializes the file, reporting the descriptor and returning null on failure. |

### `MetadataSet`

The metadata files a generator asked for, keyed by file name.

#### Properties

| Name | Type | Description |
|------|------|-------------|
| `this[string fileName]` | `MetadataFile?` | The named file, or null when it was not supplied to the compilation. |

### `DiagnosticCatalog`

Declares a generator's diagnostics under one identifier prefix and category. Takes both as
constructor arguments.

#### Properties

| Name | Type | Description |
|------|------|-------------|
| `Category` | `string` | The category every descriptor in this catalogue is reported under. |
| `Descriptors` | `IReadOnlyList<DiagnosticDescriptor>` | Every descriptor allocated, in allocation order. |

#### Methods

| Name | Return Type | Description |
|------|-------------|-------------|
| `Warning(int number, string title, string messageFormat)` | `DiagnosticDescriptor` | Allocates a warning. The number is formatted to three digits. |
| `Error(int number, string title, string messageFormat)` | `DiagnosticDescriptor` | Allocates an error. The number is formatted to three digits. |

### `DiagnosticReporting`

Extension methods on `SourceProductionContext`.

#### Methods

| Name | Return Type | Description |
|------|-------------|-------------|
| `Report(DiagnosticDescriptor, params object?[])` | `void` | Reports a diagnostic with no source location. |
| `ReportAt(DiagnosticDescriptor, Location?, params object?[])` | `void` | Reports a diagnostic at a location, treating null as `Location.None`. |

### `CSharpKeywords`

C# vocabulary a generator emits, named so a typo in a keyword is a compile error rather than
malformed generated source.

#### Fields

| Name | Type | Description |
|------|------|-------------|
| `Public` | `const string` | The `public` modifier. |
| `Static` | `const string` | The `static` modifier. |

### `GeneratorHarness`

In `ktsu.SourceGeneratorToolkit.Testing`. Runs an incremental generator over a directory of
metadata files and hands back what it produced. Takes the metadata directory as a constructor
argument.

#### Methods

| Name | Return Type | Description |
|------|-------------|-------------|
| `Run(IIncrementalGenerator, IReadOnlyDictionary<string, string>?)` | `GeneratorRunResult` | Runs one generator against every file in the directory. An override replaces a file's contents, or adds a file. |
| `RunWithOnly(IIncrementalGenerator, params string[])` | `GeneratorRunResult` | Runs one generator against only the named files, so a test can assert what it does when one is absent. |
| `RunAll(IReadOnlyList<IIncrementalGenerator>, IReadOnlyDictionary<string, string>?)` | `GeneratorDriverRunResult` | Runs several generators and returns the whole driver result. |
| `ReusesCachedOutputOnRerun(IIncrementalGenerator)` | `bool` | Runs the generator twice over identical metadata and reports whether the second run reused the first run's outputs. |

## Contributing

Contributions are welcome! Feel free to open issues or submit pull requests.

## License

This project is licensed under the MIT License. See the [LICENSE.md](LICENSE.md) file for details.
