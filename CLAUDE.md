# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build Commands

```bash
# Restore, build, and test (standard workflow)
dotnet restore
dotnet build
dotnet test

# Run a single test
dotnet test --filter "FullyQualifiedName~TestMethodName"

# Build specific configuration
dotnet build -c Release

# Format
dotnet format
```

### Reproducing SonarCloud warnings locally

CI analyses this repository with the SonarCloud scanner, which injects the Sonar analyzers into the
compilation. A plain `dotnet build` does **not** run them, so Sonar findings are invisible locally
and only surface after a push. To run the same analyzers:

```bash
dotnet build -p:CustomAfterMicrosoftCommonProps=$PWD/.sonarlint/sonar-local.props
```

```powershell
dotnet build -p:CustomAfterMicrosoftCommonProps=$PWD\.sonarlint\sonar-local.props
```

Note **`After`**, not `Before`. These projects declare their SDKs with `<Sdk Name="..." />` elements
rather than the `<Project Sdk="...">` attribute, and `CustomBeforeMicrosoftCommonProps` does not
reach that form. `CustomAfterMicrosoftCommonProps` does, and is still early enough for restore to
pick up the analyzer `PackageReference`.

Nothing imports `.sonarlint/` automatically, so normal builds, the CI pipeline, and packaging are
unaffected. The globalconfig approximates CI's rule set rather than reproducing it exactly, so treat
a clean local run as strong evidence rather than proof.

## Project Structure

Two shipping packages plus their tests.

| Project | Package | Responsibility |
|---|---|---|
| `SourceGeneratorToolkit` | `ktsu.SourceGeneratorToolkit` | `GeneratorBase`/`GeneratorBase<T>`, `MetadataFile`/`MetadataSet`, `DiagnosticCatalog`/`DiagnosticReporting`, `CSharpKeywords`. Runs inside the compiler. |
| `SourceGeneratorToolkit.Testing` | `ktsu.SourceGeneratorToolkit.Testing` | `GeneratorHarness` — drives a generator under `CSharpGeneratorDriver`. Does not run inside the compiler. |
| `SourceGeneratorToolkit.Test` | (not packed) | MSTest coverage of both. |

The solution uses:

- **ktsu.Sdk** — shared build configuration, packaging metadata, and the KTSU000x analyzers
- **MSTest.Sdk** — test project SDK with Microsoft Testing Platform
- Both shipping projects target `netstandard2.0` only; the test project targets `net10.0`

### Key Files

- `SourceGeneratorToolkit/GeneratorBase.cs` — the `IIncrementalGenerator` base. Finds the declared
  `AdditionalFiles`, reports what is missing, hands the rest to `Generate`. Also `CreateCodeBlocker`
  and the file-header writers.
- `SourceGeneratorToolkit/MetadataFile.cs` — one metadata file and the set of them. Deserialization,
  and `FindLocation` for pointing a diagnostic at a position in the metadata.
- `SourceGeneratorToolkit/DiagnosticCatalog.cs` — descriptor allocation under one prefix and
  category, plus the `Report`/`ReportAt` extensions.
- `SourceGeneratorToolkit/CSharpKeywords.cs` — the C# vocabulary a generator emits verbatim.
- `SourceGeneratorToolkit.Testing/GeneratorHarness.cs` — the `CSharpGeneratorDriver` harness.
- `SourceGeneratorToolkit.Test/TestGenerators.cs` — fixture generators and metadata models. The
  tests drive real generators rather than asserting against the base class directly.

### Dependencies

- **ktsu.CodeBlocker** — the C# syntax template model the emitted source is described with. Flows to
  consumers: they write templates against it.
- **System.Text.Json** — metadata deserialization. Flows to consumers: their metadata models are its
  DTOs.
- **Microsoft.CodeAnalysis.CSharp / .Common / .Analyzers**, **System.Collections.Immutable** —
  `PrivateAssets="all"`. The analyzer host supplies Roslyn, and a consuming generator references it
  at whatever version it builds against, so it must not flow as a package dependency.
- **Polyfill** — `PrivateAssets="all"`. Source-only; supplies `Ensure` and the range/index types on
  `netstandard2.0`.

## Architecture

The toolkit is deliberately consumer-agnostic. It knows how to find and parse metadata and how to
write deterministic output; it knows nothing about any one repository's diagnostics, copyright, or
domain.

That seam is the abstract members. A consuming repository writes one base class of its own that
binds its diagnostic catalogue and its file header in a single place, and every generator in that
repository derives from it:

```csharp
public abstract class MyGenerator<T>(string metadataFileName) : GeneratorBase<T>(metadataFileName)
	where T : class
{
	protected sealed override DiagnosticCatalog Diagnostics => MyDiagnostics.Catalog;
	protected sealed override DiagnosticDescriptor MetadataFileMissing => MyDiagnostics.MetadataFileMissing;
	protected sealed override DiagnosticDescriptor MetadataParseFailed => MyDiagnostics.MetadataParseFailed;

	protected static void WriteHeaderTo(CodeBlocker codeBlocker) =>
		WriteFileHeader(codeBlocker, MyDiagnostics.Copyright);
}
```

Keep that seam. Anything that would put a specific repository's identity into this package —
a hard-coded copyright, a diagnostic prefix, a known metadata file name — belongs on the consumer's
side of it.

### Decisions worth not relitigating

- **`GeneratorBase` takes N files, not one.** Two generators in the original repository had each
  reimplemented multi-file loading around a single-file base. Declaring more than one file is the
  normal case.
- **Output is pinned to LF.** `IndentedTextWriter` uses `Environment.NewLine`, which makes the same
  generator emit different bytes on Windows and Linux. A repository that commits its generated
  sources and verifies them in CI cannot have that.
- **File names match on the whole last segment.** `EndsWith` matching also takes `otherthings.json`
  for `things.json`.
- **A missing or malformed file always reports.** Producing no output and no explanation is
  indistinguishable from a generator that had nothing to emit, and swallowing a `JsonException`
  means malformed metadata silently generates something wrong.
- **The harness is a separate package, not a separate test project.** A consumer testing their own
  generator needs it, so it ships. It is separate from the analyzer package because it reads files,
  which RS1035 bans for code that runs in an analyzer host — and `EnforceExtendedAnalyzerRules`
  stays on in the analyzer package rather than being suppressed to accommodate it.

## Testing

MSTest, driving real fixture generators through `GeneratorHarness` rather than asserting against the
base class in isolation — a generator that passes under `CSharpGeneratorDriver` is the only thing
that matters.

- `SourceGeneratorToolkit.Test/Metadata/*.json` are the fixtures. They are copied to the output
  directory and supplied to the driver the way MSBuild's `AdditionalFiles` item group supplies them.
- `TestGenerators.cs` holds `ThingsGenerator` (single file), `PairGenerator` (two files) and
  `AbsentFileGenerator` (declares a file nothing supplies), plus the `TST` diagnostic catalogue that
  stands in for a consumer's own.
- Use explicit types (no `var`) in test bodies.

## CI/CD

The shared ktsu pipeline (`.github/workflows/dotnet.yml`) drives `ktsu.KtsuBuild.Tool`: it discovers
test projects, runs them across Linux and Windows, then analyses, versions, packs and publishes.
Version increments are controlled by commit message tags: `[major]`, `[minor]`, `[patch]`, `[pre]`.

Publishing needs the org NuGet secrets (`NUGET_KEY`, `KTSU_PACKAGE_KEY`) and, for analysis,
`SONAR_TOKEN`.

`VERSION.md`, `CHANGELOG.md` and `LATEST_CHANGELOG.md` are written by the pipeline. Do not edit them
by hand.

## Code Quality

Do not add global suppressions for warnings. Use explicit suppression attributes with justifications
when needed, with preprocessor defines only as fallback. Make the smallest, most targeted
suppressions possible.

`EnforceExtendedAnalyzerRules` is on in `SourceGeneratorToolkit` and must stay on: it is the only
thing stopping analyzer-hostile APIs from reaching code that runs inside the compiler. If something
needs the file system, it belongs in `SourceGeneratorToolkit.Testing`.

## Provenance

Extracted from [ktsu-dev/Semantics](https://github.com/ktsu-dev/Semantics) —
[#181](https://github.com/ktsu-dev/Semantics/issues/181) and
[#192](https://github.com/ktsu-dev/Semantics/issues/192) — where this code grew inside a physics
quantity generator and was shaped for extraction before being moved. The template half of it went to
[ktsu.CodeBlocker](https://github.com/ktsu-dev/CodeBlocker) first; this is the Roslyn-side remainder.
