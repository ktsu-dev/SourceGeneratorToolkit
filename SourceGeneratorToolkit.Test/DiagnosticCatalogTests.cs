// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.SourceGeneratorToolkit.Test;

using System;
using Microsoft.CodeAnalysis;

[TestClass]
public sealed class DiagnosticCatalogTests
{
	private static readonly string[] ExpectedAllocationOrder = ["ABC003", "ABC001", "ABC002"];

	[TestMethod]
	public void WarningFormatsIdentifierToThreeDigits()
	{
		DiagnosticCatalog catalog = new("ABC", "Category");

		DiagnosticDescriptor descriptor = catalog.Warning(7, "Title", "Message");

		Assert.AreEqual("ABC007", descriptor.Id);
	}

	[TestMethod]
	public void IdentifierDoesNotTruncateBeyondThreeDigits()
	{
		DiagnosticCatalog catalog = new("ABC", "Category");

		DiagnosticDescriptor descriptor = catalog.Warning(1234, "Title", "Message");

		Assert.AreEqual("ABC1234", descriptor.Id);
	}

	[TestMethod]
	public void EveryDescriptorCarriesTheCatalogueCategory()
	{
		DiagnosticCatalog catalog = new("ABC", "Some.Category");

		DiagnosticDescriptor warning = catalog.Warning(1, "Title", "Message");
		DiagnosticDescriptor error = catalog.Error(2, "Title", "Message");

		Assert.AreEqual("Some.Category", catalog.Category);
		Assert.AreEqual("Some.Category", warning.Category);
		Assert.AreEqual("Some.Category", error.Category);
	}

	[TestMethod]
	public void SeverityFollowsTheAllocatingMethod()
	{
		DiagnosticCatalog catalog = new("ABC", "Category");

		Assert.AreEqual(DiagnosticSeverity.Warning, catalog.Warning(1, "T", "M").DefaultSeverity);
		Assert.AreEqual(DiagnosticSeverity.Error, catalog.Error(2, "T", "M").DefaultSeverity);
	}

	[TestMethod]
	public void DescriptorsAreRecordedInAllocationOrder()
	{
		DiagnosticCatalog catalog = new("ABC", "Category");

		catalog.Warning(3, "T", "M");
		catalog.Error(1, "T", "M");
		catalog.Warning(2, "T", "M");

		string[] ids = [.. catalog.Descriptors.Select(descriptor => descriptor.Id)];

		CollectionAssert.AreEqual(ExpectedAllocationOrder, ids);
	}

	[TestMethod]
	public void ReusingANumberAcrossSeveritiesThrows()
	{
		DiagnosticCatalog catalog = new("X", "c");
		catalog.Warning(1, "a", "a");

		ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(() => catalog.Error(1, "b", "b"));

		Assert.AreEqual("number", exception.ParamName);
		Assert.AreEqual(1, catalog.Descriptors.Count);
	}

	[TestMethod]
	public void ReusingANumberWithTheSameSeverityThrows()
	{
		DiagnosticCatalog catalog = new("X", "c");
		catalog.Warning(1, "a", "a");

		Assert.ThrowsExactly<ArgumentException>(() => catalog.Warning(1, "a", "a"));
	}

	[TestMethod]
	public void TheSameNumberIsAllowedInSeparateCatalogues()
	{
		DiagnosticCatalog first = new("X", "c");
		DiagnosticCatalog second = new("Y", "c");

		Assert.AreEqual("X001", first.Warning(1, "a", "a").Id);
		Assert.AreEqual("Y001", second.Warning(1, "a", "a").Id);
	}

	[TestMethod]
	public void DescriptorsIsEmptyBeforeAnythingIsAllocated()
	{
		DiagnosticCatalog catalog = new("ABC", "Category");

		Assert.AreEqual(0, catalog.Descriptors.Count);
	}

	[TestMethod]
	public void EveryDescriptorIsEnabledByDefault()
	{
		DiagnosticCatalog catalog = new("ABC", "Category");

		Assert.IsTrue(catalog.Warning(1, "T", "M").IsEnabledByDefault);
	}

	[TestMethod]
	public void ReportRejectsANullDescriptor()
	{
		SourceProductionContext context = default;

		Assert.ThrowsExactly<ArgumentNullException>(() => context.Report(null!, "argument"));
	}

	[TestMethod]
	public void ReportAtRejectsANullDescriptor()
	{
		SourceProductionContext context = default;

		Assert.ThrowsExactly<ArgumentNullException>(() => context.ReportAt(null!, Location.None, "argument"));
	}
}
