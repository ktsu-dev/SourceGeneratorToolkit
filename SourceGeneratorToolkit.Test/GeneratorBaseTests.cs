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
}
