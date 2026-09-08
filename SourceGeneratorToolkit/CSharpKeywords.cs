// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.SourceGeneratorToolkit;

/// <summary>
/// C# vocabulary a generator emits, named so a typo in a keyword is a compile error rather than
/// malformed generated source.
/// </summary>
/// <remarks>
/// Only the words a generator writes without deciding anything belong here. Documentation is not
/// among them: it is written through the template model's <c>DocComment</c>, which owns the tags
/// and escapes their content.
/// </remarks>
public static class CSharpKeywords
{
	/// <summary>The <c>public</c> modifier.</summary>
	public const string Public = "public";

	/// <summary>The <c>static</c> modifier.</summary>
	public const string Static = "static";
}
