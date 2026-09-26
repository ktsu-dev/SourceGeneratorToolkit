// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.SourceGeneratorToolkit.Test;

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.CodeAnalysis;
using ktsu.SourceGeneratorToolkit.Testing;

[TestClass]
public sealed class GeneratorBaseTests
{
	private static string MetadataDirectory => Path.Combine(AppContext.BaseDirectory, "Metadata");

	private static GeneratorHarness Harness => new(MetadataDirectory);

	[TestMethod]
	public void GeneratesFromTheDeclaredMetadataFile()
	{
		GeneratorRunResult result = Harness.Run(new ThingsGenerator());

		Assert.AreEqual(0, result.Diagnostics.Length);
		Assert.AreEqual(1, result.GeneratedSources.Length);

		string source = result.GeneratedSources[0].SourceText.ToString();
		StringAssert.Contains(source, "class Alpha");
		StringAssert.Contains(source, "class Beta");
	}

	[TestMethod]
	public void MultiFileGeneratorReceivesEveryFileItDeclared()
	{
		GeneratorRunResult result = Harness.Run(new PairGenerator());

		Assert.AreEqual(0, result.Diagnostics.Length);
		StringAssert.Contains(result.GeneratedSources[0].SourceText.ToString(), "2 things, 1 others");
	}

	[TestMethod]
	public void AMissingMetadataFileIsReportedRatherThanSilentlyProducingNothing()
	{
		GeneratorRunResult result = Harness.Run(new AbsentFileGenerator());

		Assert.AreEqual(0, result.GeneratedSources.Length);
		Assert.AreEqual(1, result.Diagnostics.Length);
		Assert.AreEqual(TestDiagnostics.MetadataFileMissing.Id, result.Diagnostics[0].Id);
		StringAssert.Contains(result.Diagnostics[0].GetMessage(), "nowhere.json");
	}

	[TestMethod]
	public void AMultiFileGeneratorReportsEachFileThatIsAbsent()
	{
		GeneratorRunResult result = Harness.RunWithOnly(new PairGenerator(), "things.json");

		Assert.AreEqual(1, result.Diagnostics.Length);
		Assert.AreEqual(TestDiagnostics.MetadataFileMissing.Id, result.Diagnostics[0].Id);
		StringAssert.Contains(result.Diagnostics[0].GetMessage(), "others.json");
	}

	[TestMethod]
	public void AMalformedMetadataFileIsReportedRatherThanSwallowed()
	{
		GeneratorRunResult result = Harness.Run(
			new ThingsGenerator(),
			new Dictionary<string, string> { ["things.json"] = "{ not json" });

		Assert.AreEqual(0, result.GeneratedSources.Length);
		Assert.AreEqual(1, result.Diagnostics.Length);
		Assert.AreEqual(TestDiagnostics.MetadataParseFailed.Id, result.Diagnostics[0].Id);
		Assert.AreEqual(DiagnosticSeverity.Error, result.Diagnostics[0].Severity);
	}

	[TestMethod]
	public void AMalformedSecondMetadataFileIsAlsoReported()
	{
		GeneratorRunResult result = Harness.Run(
			new PairGenerator(),
			new Dictionary<string, string> { ["others.json"] = "{ not json" });

		Assert.AreEqual(0, result.GeneratedSources.Length);
		Assert.AreEqual(1, result.Diagnostics.Length);
		Assert.AreEqual(TestDiagnostics.MetadataParseFailed.Id, result.Diagnostics[0].Id);
	}

	[TestMethod]
	public void AShapeTheSerializerCannotConstructIsReportedRatherThanCrashingTheGenerator()
	{
		// An interface-typed model throws NotSupportedException, not JsonException. Uncaught, it
		// leaves the driver to report its own CS8785, which names neither the file nor the reason.
		GeneratorRunResult result = Harness.Run(new UnsupportedShapeGenerator());

		Assert.IsNull(result.Exception, $"The generator threw instead of reporting a diagnostic: {result.Exception}");
		Assert.AreEqual(0, result.GeneratedSources.Length);
		Assert.AreEqual(1, result.Diagnostics.Length);
		Assert.AreEqual(TestDiagnostics.MetadataParseFailed.Id, result.Diagnostics[0].Id);
		Assert.AreEqual(DiagnosticSeverity.Error, result.Diagnostics[0].Severity);
		StringAssert.Contains(result.Diagnostics[0].GetMessage(), "things.json");
	}

	[TestMethod]
	public void AmbiguousConstructorsAreReportedRatherThanCrashingTheGenerator()
	{
		// The other NotSupportedException shape: two parameterized constructors, no [JsonConstructor].
		GeneratorRunResult result = Harness.Run(new AmbiguousConstructorGenerator());

		Assert.IsNull(result.Exception, $"The generator threw instead of reporting a diagnostic: {result.Exception}");
		Assert.AreEqual(0, result.GeneratedSources.Length);
		Assert.AreEqual(1, result.Diagnostics.Length);
		Assert.AreEqual(TestDiagnostics.MetadataParseFailed.Id, result.Diagnostics[0].Id);
	}

	[TestMethod]
	public void AConverterConfigurationFailureIsReportedRatherThanCrashingTheGenerator()
	{
		// Two properties claiming one JSON name is neither malformed input nor an unconstructable
		// type: System.Text.Json cannot build the contract and says so with InvalidOperationException.
		GeneratorRunResult result = Harness.Run(new ConflictingNamesGenerator());

		Assert.IsNull(result.Exception, $"The generator threw instead of reporting a diagnostic: {result.Exception}");
		Assert.AreEqual(0, result.GeneratedSources.Length);
		Assert.AreEqual(1, result.Diagnostics.Length);
		Assert.AreEqual(TestDiagnostics.MetadataParseFailed.Id, result.Diagnostics[0].Id);
	}

	[TestMethod]
	public void OneFileFailingOnAnUnsupportedShapeStillLeavesTheOthersProcessed()
	{
		// A throw out of Deserialize abandons the whole RegisterSourceOutput callback, so every other
		// declared file goes unread. A reported diagnostic stops at the file it belongs to.
		GeneratorRunResult result = Harness.Run(new ResilientPairGenerator());

		Assert.IsNull(result.Exception, $"The generator threw instead of reporting a diagnostic: {result.Exception}");
		Assert.AreEqual(1, result.Diagnostics.Length);
		Assert.AreEqual(TestDiagnostics.MetadataParseFailed.Id, result.Diagnostics[0].Id);
		Assert.AreEqual(1, result.GeneratedSources.Length);
		StringAssert.Contains(result.GeneratedSources[0].SourceText.ToString(), "1 others, things unread");
	}

	[TestMethod]
	public void AJsonNullDocumentIsReportedRatherThanTreatedAsEmpty()
	{
		GeneratorRunResult result = Harness.Run(
			new ThingsGenerator(),
			new Dictionary<string, string> { ["things.json"] = "null" });

		Assert.AreEqual(1, result.Diagnostics.Length);
		Assert.AreEqual(TestDiagnostics.MetadataParseFailed.Id, result.Diagnostics[0].Id);
		StringAssert.Contains(result.Diagnostics[0].GetMessage(), "deserialized to null");
	}

	[TestMethod]
	public void AnEmptyMetadataFileCountsAsMissing()
	{
		GeneratorRunResult result = Harness.Run(
			new ThingsGenerator(),
			new Dictionary<string, string> { ["things.json"] = "" });

		Assert.AreEqual(1, result.Diagnostics.Length);
		Assert.AreEqual(TestDiagnostics.MetadataFileMissing.Id, result.Diagnostics[0].Id);
	}

	[TestMethod]
	public void AFileWhoseNameMerelyEndsWithTheWantedOneIsNotMatched()
	{
		// EndsWith matching would take otherthings.json for things.json. Whichever won, the
		// generator would emit types that are not in the file it declared.
		GeneratorRunResult result = Harness.Run(
			new ThingsGenerator(),
			new Dictionary<string, string>
			{
				["otherthings.json"] = """{ "things": [ { "name": "Wrong" } ] }""",
			});

		string source = result.GeneratedSources[0].SourceText.ToString();
		Assert.AreEqual(0, result.Diagnostics.Length);
		StringAssert.Contains(source, "class Alpha");
		Assert.IsFalse(source.Contains("Wrong", StringComparison.Ordinal));
	}

	[TestMethod]
	public void OutputEndsEveryLineWithLfWhateverTheHostTerminatorIs()
	{
		GeneratorRunResult result = Harness.Run(new ThingsGenerator());

		string source = result.GeneratedSources[0].SourceText.ToString();

		Assert.IsFalse(source.Contains('\r'), "Generated output must be LF-only to stay byte-identical across platforms.");
		StringAssert.Contains(source, "\n");
	}

	[TestMethod]
	public void TheHeaderCarriesTheCopyrightAndTheGeneratedMarker()
	{
		GeneratorRunResult result = Harness.Run(new ThingsGenerator());

		string source = result.GeneratedSources[0].SourceText.ToString();

		Assert.IsTrue(source.StartsWith($"// {TestDiagnostics.Copyright}\n// <auto-generated />\n", StringComparison.Ordinal), source[..80]);
	}

	[TestMethod]
	public void TheGeneratorReusesItsOutputWhenNothingChanged()
	{
		Assert.IsTrue(
			Harness.ReusesCachedOutputOnRerun(new ThingsGenerator()),
			"An IIncrementalGenerator that recomputes everything on every keystroke still passes every output assertion.");
	}

	[TestMethod]
	public void TheRerunCheckCatchesAGeneratorThatRecomputesOnEveryEdit()
	{
		Assert.IsFalse(
			Harness.ReusesCachedOutputOnRerun(new CompilationBoundGenerator()),
			"A generator whose output hangs off the compilation recomputes on every keystroke, and the check must say so.");
	}

	[TestMethod]
	public void TheRerunCheckDoesNotPassAGeneratorWithNoOutputSteps()
	{
		Assert.IsFalse(Harness.ReusesCachedOutputOnRerun(new SilentGenerator()));
	}
}
