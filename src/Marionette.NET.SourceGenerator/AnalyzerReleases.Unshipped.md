; Unshipped analyzer rules.
; See https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category             | Severity | Notes
--------|----------------------|----------|------
MAR001  | Marionette.Generator | Error    | [McpRoot] requires a non-static, non-generic reference type.
MAR002  | Marionette.Generator | Error    | [McpCallable] method must be public.
MAR003  | Marionette.Generator | Warning  | [McpCallable] on un-rooted class is ignored.
MAR004  | Marionette.Generator | Error    | [McpCallable] parameter type not supported.
MAR005  | Marionette.Generator | Error    | [McpObservable] requires a public getter.
MAR006  | Marionette.Generator | Warning  | [McpObservable] property should be public.
MAR007  | Marionette.Generator | Error    | [McpTriggerable] only supports controls with a Click event in Phase 1.
MAR008  | Marionette.Generator | Info     | [McpRoot] declares no MCP entrypoints.
