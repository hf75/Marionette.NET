# Marionette.NET

> AI-driven UI automation for every C# desktop framework.

Drop a NuGet into your **WPF / Avalonia / WinUI / Uno / MAUI** app, decorate methods and properties with attributes, and your app becomes an MCP server — controllable, testable, and observable by Claude Code or any other MCP-aware agent.

```csharp
[McpCallable("Apply category filter to product list.")]
public void ApplyFilter(string category, decimal minPrice) { … }

[McpObservable("Currently visible product count.", Watchable = true)]
public int VisibleCount => _filtered.Count;
```

Ship it. Run `MyApp.exe --mcp`. Claude can now drive your app.

---

## Why this exists

Existing UI-automation tools are out-of-process (UIA/Appium), language-locked (Playwright), abandoned (Coded UI), or commercial (UiPath). None of them are AI-native. Marionette is **in-process**, **attribute-driven**, **cross-framework**, and **MCP-native** — and it can compile out completely in production builds when you don't need it.

See [MASTERPLAN.md](MASTERPLAN.md) for the full vision, architecture, and roadmap.

## Status

**Pre-alpha · Planning phase.** No code yet. The masterplan is locked; implementation starts with a 3-day Phase 0 foundation spike.

## License

MIT — see [LICENSE](LICENSE).
