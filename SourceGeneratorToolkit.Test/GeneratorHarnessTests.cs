// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.SourceGeneratorToolkit.Test;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ktsu.SourceGeneratorToolkit.Testing;

[TestClass]
public sealed class GeneratorHarnessTests
{
	private static string MetadataDirectory => Path.Join(AppContext.BaseDirectory, "Metadata");

	private static GeneratorHarness Harness => new(MetadataDirectory);

	[TestMethod]
	public void AHeaderForACSharp73ConsumerParsesWithoutCS8370()
	{
		GeneratorHarness harness = new(MetadataDirectory) { LanguageVersion = LanguageVersion.CSharp7_3 };

		GeneratorRunResult result = harness.Run(new PairGenerator());

		SyntaxTree tree = result.GeneratedSources[0].SyntaxTree;
		Assert.IsFalse(tree.GetText().ToString().Contains("#nullable", StringComparison.Ordinal), tree.GetText().ToString());
		Assert.AreEqual(0, tree.GetDiagnostics().Count(), string.Join("\n", tree.GetDiagnostics()));
	}

	[TestMethod]
	[DataRow(LanguageVersion.CSharp7_3)]
	[DataRow(LanguageVersion.CSharp8)]
	[DataRow(LanguageVersion.CSharp9)]
	[DataRow(LanguageVersion.CSharp10)]
	[DataRow(LanguageVersion.Latest)]
	public void ANamespacedSourceFileCompilesForTheConsumersLanguageVersion(LanguageVersion languageVersion)
	{
		GeneratorHarness harness = new(MetadataDirectory) { LanguageVersion = languageVersion };

		GeneratorRunResult result = harness.Run(new ThingsGenerator());

		SyntaxTree tree = result.GeneratedSources[0].SyntaxTree;
		string text = tree.GetText().ToString();
		CSharpCompilation compilation = CompileAsConsumer(tree);
		Diagnostic[] errors = [.. compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)];

		Assert.AreEqual(0, errors.Length, $"{string.Join("\n", errors)}\n{text}");
		Assert.IsNotNull(compilation.GetTypeByMetadataName("Generated.Alpha"), text);
	}

	/// <summary>
	/// Compiles a generated tree the way the consumer's build would, at the tree's own language version.
	/// </summary>
	/// <remarks>
	/// A parse alone is not enough: a file-scoped namespace below C# 10 parses cleanly and only fails
	/// to compile (CS8370/CS8773).
	/// </remarks>
	private static CSharpCompilation CompileAsConsumer(SyntaxTree tree) =>
		CSharpCompilation.Create(
			"Consumer",
			[tree],
			[MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

	[TestMethod]
	[DataRow(LanguageVersion.CSharp7_3)]
	[DataRow(LanguageVersion.CSharp9)]
	public void ANamespacedSourceFileForAConsumerBeforeCSharp10IsBlockScoped(LanguageVersion languageVersion)
	{
		GeneratorHarness harness = new(MetadataDirectory) { LanguageVersion = languageVersion };

		string text = harness.Run(new ThingsGenerator()).GeneratedSources[0].SourceText.ToString();

		StringAssert.Contains(text, "namespace Generated\n{\n", text);
		Assert.IsFalse(text.Contains("namespace Generated;", StringComparison.Ordinal), text);
	}

	[TestMethod]
	[DataRow(LanguageVersion.CSharp7_3)]
	[DataRow(LanguageVersion.CSharp9)]
	[DataRow(LanguageVersion.CSharp10)]
	[DataRow(LanguageVersion.Latest)]
	public void AFilesAssemblyAttributesAndCommentsAreKeptAtEveryLanguageVersion(LanguageVersion languageVersion)
	{
		GeneratorHarness harness = new(MetadataDirectory) { LanguageVersion = languageVersion };

		SyntaxTree tree = harness.Run(new AttributedThingsGenerator()).GeneratedSources[0].SyntaxTree;
		string text = tree.GetText().ToString();
		CSharpCompilation compilation = CompileAsConsumer(tree);
		Diagnostic[] errors = [.. compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)];

		Assert.AreEqual(0, errors.Length, $"{string.Join("\n", errors)}\n{text}");
		Assert.IsTrue(
			compilation.Assembly.GetAttributes().Any(attribute => attribute.AttributeClass?.Name == nameof(CLSCompliantAttribute)),
			text);
		StringAssert.Contains(text, AttributedThingsGenerator.FileComment, text);
		Assert.IsNotNull(compilation.GetTypeByMetadataName("Generated.Alpha"), text);
	}

	[TestMethod]
	[DataRow(LanguageVersion.CSharp7_3)]
	[DataRow(LanguageVersion.CSharp9)]
	public void AFilesAssemblyAttributesAndCommentsPrecedeABlockScopedNamespace(LanguageVersion languageVersion)
	{
		GeneratorHarness harness = new(MetadataDirectory) { LanguageVersion = languageVersion };

		string text = harness.Run(new AttributedThingsGenerator()).GeneratedSources[0].SourceText.ToString();
		int namespaceAt = text.IndexOf("namespace Generated\n{\n", StringComparison.Ordinal);

		Assert.IsGreaterThan(-1, namespaceAt, text);
		foreach (string beforeNamespace in new[] { AttributedThingsGenerator.FileComment, "using System;", "[assembly: CLSCompliant(true)]" })
		{
			int at = text.IndexOf(beforeNamespace, StringComparison.Ordinal);
			Assert.IsTrue(at >= 0 && at < namespaceAt, $"'{beforeNamespace}' should be written before the namespace block.\n{text}");
		}
	}

	[TestMethod]
	[DataRow(LanguageVersion.CSharp10)]
	[DataRow(LanguageVersion.Default)]
	[DataRow(LanguageVersion.Latest)]
	public void ANamespacedSourceFileForACSharp10OrLaterConsumerIsFileScoped(LanguageVersion languageVersion)
	{
		GeneratorHarness harness = new(MetadataDirectory) { LanguageVersion = languageVersion };

		string text = harness.Run(new ThingsGenerator()).GeneratedSources[0].SourceText.ToString();

		StringAssert.Contains(text, "namespace Generated;\n", text);
	}

	[TestMethod]
	[DataRow(LanguageVersion.CSharp8)]
	[DataRow(LanguageVersion.Default)]
	[DataRow(LanguageVersion.Latest)]
	public void AHeaderForACSharp8OrLaterConsumerEnablesNullable(LanguageVersion languageVersion)
	{
		GeneratorHarness harness = new(MetadataDirectory) { LanguageVersion = languageVersion };

		GeneratorRunResult result = harness.Run(new PairGenerator());

		StringAssert.Contains(result.GeneratedSources[0].SourceText.ToString(), "// <auto-generated />\n#nullable enable\n");
	}

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
