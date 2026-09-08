// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.SourceGeneratorToolkit.Test;

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.CodeAnalysis;
using ktsu.SourceGeneratorToolkit.Testing;

[TestClass]
public sealed class GeneratorHarnessTests
{
	private static GeneratorHarness Harness => new(Path.Combine(AppContext.BaseDirectory, "Metadata"));

	[TestMethod]
	public void AnOverrideReplacesTheRealFileContents()
	{
		GeneratorRunResult result = Harness.Run(
			new ThingsGenerator(),
			new Dictionary<string, string> { ["things.json"] = """{ "things": [ { "name": "Substituted" } ] }""" });

		StringAssert.Contains(result.GeneratedSources[0].SourceText.ToString(), "class Substituted");
	}

	[TestMethod]
	public void RunAllDrivesEveryGeneratorOverTheSameMetadata()
	{
		GeneratorDriverRunResult result = Harness.RunAll([new ThingsGenerator(), new PairGenerator()]);

		Assert.AreEqual(2, result.Results.Length);
		Assert.AreEqual(1, result.Results[0].GeneratedSources.Length);
		Assert.AreEqual(1, result.Results[1].GeneratedSources.Length);
	}

	[TestMethod]
	public void RunWithOnlySuppliesNothingElse()
	{
		GeneratorRunResult result = Harness.RunWithOnly(new ThingsGenerator(), "things.json");

		Assert.AreEqual(0, result.Diagnostics.Length);
		Assert.AreEqual(1, result.GeneratedSources.Length);
	}

	[TestMethod]
	public void RunWithOnlySupplyingNoFilesLeavesEveryDeclaredFileMissing()
	{
		GeneratorRunResult result = Harness.RunWithOnly(new ThingsGenerator());

		Assert.AreEqual(1, result.Diagnostics.Length);
		Assert.AreEqual(TestDiagnostics.MetadataFileMissing.Id, result.Diagnostics[0].Id);
	}
}
