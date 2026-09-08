// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.SourceGeneratorToolkit.Test;

using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

[TestClass]
public sealed class MetadataFileTests
{
	private const string Json = """
		{
		  "things": [
		    { "name": "Alpha" },
		    { "name": "Beta" }
		  ]
		}
		""";

	private static MetadataFile Create(string text = Json, bool withSourceText = true) =>
		new("things.json", text, withSourceText ? SourceText.From(text) : null, "/repo/Metadata/things.json");

	[TestMethod]
	public void FindLocationPointsAtTheFirstOccurrence()
	{
		MetadataFile file = Create();

		Location location = file.FindLocation("Beta");

		Assert.AreNotEqual(Location.None, location);
		Assert.AreEqual("/repo/Metadata/things.json", location.GetLineSpan().Path);
		Assert.AreEqual(3, location.GetLineSpan().StartLinePosition.Line);
	}

	[TestMethod]
	public void FindLocationSpansExactlyTheNeedle()
	{
		MetadataFile file = Create();

		Location location = file.FindLocation("Alpha");

		Assert.AreEqual("Alpha".Length, location.SourceSpan.Length);
		Assert.AreEqual("Alpha", Json.Substring(location.SourceSpan.Start, location.SourceSpan.Length));
	}

	[TestMethod]
	public void FindLocationReturnsNoneWhenTheNeedleIsAbsent()
	{
		MetadataFile file = Create();

		Assert.AreEqual(Location.None, file.FindLocation("Gamma"));
	}

	[TestMethod]
	public void FindLocationReturnsNoneForAnEmptyNeedle()
	{
		MetadataFile file = Create();

		Assert.AreEqual(Location.None, file.FindLocation(""));
	}

	[TestMethod]
	public void FindLocationReturnsNoneWithoutASourceText()
	{
		MetadataFile file = Create(withSourceText: false);

		Assert.AreEqual(Location.None, file.FindLocation("Alpha"));
	}

	[TestMethod]
	public void ScopedFindLocationPicksTheOccurrenceAfterTheAnchor()
	{
		const string text = """
			{
			  "a": { "unit": "meter" },
			  "b": { "unit": "meter" }
			}
			""";
		MetadataFile file = new("units.json", text, SourceText.From(text), "/repo/units.json");

		Location unscoped = file.FindLocation("meter");
		Location scoped = file.FindLocation("\"b\"", "meter");

		Assert.AreEqual(1, unscoped.GetLineSpan().StartLinePosition.Line);
		Assert.AreEqual(2, scoped.GetLineSpan().StartLinePosition.Line);
	}

	[TestMethod]
	public void ScopedFindLocationFallsBackWhenTheAnchorIsAbsent()
	{
		MetadataFile file = Create();

		Location scoped = file.FindLocation("no such anchor", "Alpha");

		Assert.AreEqual(file.FindLocation("Alpha"), scoped);
	}

	[TestMethod]
	public void ScopedFindLocationFallsBackWhenTheNeedleIsOnlyBeforeTheAnchor()
	{
		MetadataFile file = Create();

		Location scoped = file.FindLocation("Beta", "Alpha");

		Assert.AreEqual(file.FindLocation("Alpha"), scoped);
	}

	[TestMethod]
	public void ScopedFindLocationReturnsNoneForAnEmptyNeedle()
	{
		MetadataFile file = Create();

		Assert.AreEqual(Location.None, file.FindLocation("things", ""));
	}

	[TestMethod]
	public void DeserializeReadsPropertiesCaseInsensitively()
	{
		MetadataFile file = Create();

		ThingsMetadata? metadata = file.Deserialize<ThingsMetadata>(default, TestDiagnostics.MetadataParseFailed);

		Assert.IsNotNull(metadata);
		Assert.AreEqual(2, metadata.Things.Count);
		Assert.AreEqual("Alpha", metadata.Things[0].Name);
	}

	[TestMethod]
	public void FileNameAndTextAreWhatWasSupplied()
	{
		MetadataFile file = Create();

		Assert.AreEqual("things.json", file.FileName);
		Assert.AreEqual(Json, file.Text);
	}

	[TestMethod]
	public void MetadataSetReturnsNullForAFileItDoesNotHold()
	{
		MetadataSet set = new(new Dictionary<string, MetadataFile>());

		Assert.IsNull(set["absent.json"]);
	}

	[TestMethod]
	public void MetadataSetReturnsTheFileItHolds()
	{
		MetadataFile file = Create();
		MetadataSet set = new(new Dictionary<string, MetadataFile> { ["things.json"] = file });

		Assert.AreSame(file, set["things.json"]);
	}

	[TestMethod]
	public void MetadataSetRejectsNull() =>
		Assert.ThrowsExactly<ArgumentNullException>(() => new MetadataSet(null!));
}
