; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
EQD001  | eQuantic.Modeling | Warning | Concurrency token type is not versionable
EQD002  | eQuantic.Modeling | Warning | Composite partition key has ambiguous member order
EQD003  | eQuantic.Modeling | Warning | Clustering keys have ambiguous member order
EQD004  | eQuantic.Modeling | Warning | Multiple [EntityKey] members
EQD005  | eQuantic.Modeling | Warning | Time-to-live must be positive
EQD006  | eQuantic.Modeling | Warning | Invalid [Facet]
EQD007  | eQuantic.Modeling | Warning | [Unmapped] member carries mapping annotations
EQD008  | eQuantic.Modeling | Warning | [SearchIndex] requires a string member
EQD009  | eQuantic.Modeling | Warning | [Counter] requires an integral member
EQD010  | eQuantic.Modeling | Warning | Generated key type is not identity-capable
EQD011  | eQuantic.Modeling | Warning | Empty storage name
