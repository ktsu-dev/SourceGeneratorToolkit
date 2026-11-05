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
	public void TwoFilesWithTheSameNamePathAndTextAreEqual()
	{
		// Roslyn compares incremental step outputs with default equality, so without value equality a
		// fresh AdditionalText with identical contents re-runs the whole generator.
		MetadataFile first = Create();
		MetadataFile second = Create();

		Assert.IsTrue(first.Equals(second));
		Assert.IsTrue(Equals(first, second));
		Assert.AreEqual(first.GetHashCode(), second.GetHashCode());
	}

	[TestMethod]
	public void FilesDifferingInTextNameOrPathAreNotEqual()
	{
		MetadataFile file = Create();

		Assert.IsFalse(file.Equals(Create(Json + " ")));
		Assert.IsFalse(file.Equals(new MetadataFile("others.json", Json, SourceText.From(Json), "/repo/Metadata/things.json")));
		Assert.IsFalse(file.Equals(new MetadataFile("things.json", Json, SourceText.From(Json), "/elsewhere/things.json")));
		Assert.IsFalse(file.Equals(null));
	}

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

	private const string CompoundNames = """
		{"units":[
		{"name":"SquareMeter","base":"Second"},
		{"name":"Meter","base":"Second"}
		]}
		""";

	[TestMethod]
	public void FindLocationSkipsALongerNameThatContainsTheNeedle()
	{
		MetadataFile file = new("units.json", CompoundNames, SourceText.From(CompoundNames), "/repo/units.json");

		Location location = file.FindLocation("Meter");

		Assert.AreEqual(2, location.GetLineSpan().StartLinePosition.Line);
		Assert.AreEqual("Meter", CompoundNames.Substring(location.SourceSpan.Start, location.SourceSpan.Length));
		Assert.AreEqual('"', CompoundNames[location.SourceSpan.Start - 1]);
	}

	[TestMethod]
	public void ScopedFindLocationSkipsALongerNameThatContainsTheAnchor()
	{
		MetadataFile file = new("units.json", CompoundNames, SourceText.From(CompoundNames), "/repo/units.json");

		Location location = file.FindLocation("Meter", "Second");

		Assert.AreEqual(2, location.GetLineSpan().StartLinePosition.Line);
		Assert.AreEqual("Second", CompoundNames.Substring(location.SourceSpan.Start, location.SourceSpan.Length));
	}

	[TestMethod]
	public void FindLocationStillFindsTextThatIsNotAWholeString()
	{
		const string text = """{"things":[{"name":"Alpha","size":12345}]}""";
		MetadataFile file = new("things.json", text, SourceText.From(text), "/repo/things.json");

		Location location = file.FindLocation("2345");

		Assert.AreEqual("2345", text.Substring(location.SourceSpan.Start, location.SourceSpan.Length));
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

	private const string NeedleFirst = """
		{"units":[
		{"base":"Meter","name":"Foot"},
		{"base":"Meter","name":"Yard"}
		]}
		""";

	[TestMethod]
	public void ScopedFindLocationPicksTheAnchorsEntryWhenTheNeedleIsWrittenFirst()
	{
		MetadataFile file = new("units.json", NeedleFirst, SourceText.From(NeedleFirst), "/repo/units.json");

		Location location = file.FindLocation("Foot", "Meter");

		Assert.AreEqual(1, location.GetLineSpan().StartLinePosition.Line);
		Assert.AreEqual("Meter", NeedleFirst.Substring(location.SourceSpan.Start, location.SourceSpan.Length));
	}

	[TestMethod]
	public void ScopedFindLocationPicksTheAnchorsEntryOverAnEarlierOne()
	{
		const string text = """
			{"units":[
			{"base":"Meter","name":"Inch"},
			{"base":"Meter","name":"Foot"}
			]}
			""";
		MetadataFile file = new("units.json", text, SourceText.From(text), "/repo/units.json");

		Location location = file.FindLocation("Foot", "Meter");

		Assert.AreEqual(2, location.GetLineSpan().StartLinePosition.Line);
	}

	[TestMethod]
	public void ScopedFindLocationSkipsNestedObjectsAndBracesInStringsBeforeTheAnchor()
	{
		const string text = """
			{"units":[
			{"base":"Meter","name":"Inch"},
			{"base":"Meter","note":"a } and a {","extra":{"k":"v"},"name":"Foot"}
			]}
			""";
		MetadataFile file = new("units.json", text, SourceText.From(text), "/repo/units.json");

		Location location = file.FindLocation("Foot", "Meter");

		Assert.AreEqual(2, location.GetLineSpan().StartLinePosition.Line);
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
