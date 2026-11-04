## v1.3.0 (minor)

Changes since v1.2.0:

- Add a SessionStart hook for Claude Code cloud sessions (#52) (matt-edmondson)
- Report same-named metadata files whose contents differ [patch] (#64) (matt-edmondson)
- Report any exception from model code instead of crashing the generator [patch] (#65) (matt-edmondson)
- Find a whole JSON string before a substring in FindLocation [patch] (#63) (matt-edmondson)
- Locate a parse failure in a CR-only metadata file on its real line [patch] (#62) (matt-edmondson)
- Leave #nullable enable out of headers for consumers below C# 8 [minor] (#61) (matt-edmondson)
- Say what a null copyright leaves out of the header (Claude)
- Enable nullable in every generated file's header [patch] (Claude)
- Split a copyright at NEL, LINE SEPARATOR and PARAGRAPH SEPARATOR too [patch] (Claude)
- Report malformed metadata JSON at the line it breaks on [patch] (Claude)
- Opt the analyzer assembly out of KTSU0008 (Claude)
- Move GuardedPairGenerator beside ResilientPairGenerator (matt-edmondson)
- fix: give MetadataFile value equality so unchanged metadata stays cached [patch] (matt-edmondson)
- fix: report a model constructor's ArgumentException as a parse failure [patch] (matt-edmondson)
- fix: comment every line of a multi-line copyright in the file header [patch] (matt-edmondson)
- Move CI onto the shared ci-shared.yml pipeline (Claude)
- fix: report an empty metadata file as unparseable, not missing [patch] (Claude)

