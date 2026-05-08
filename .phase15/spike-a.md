# Spike A — Phase 15 WinForms Adapter Foundation

**Status:** Planned
**Date:** 2026-05-08
**SDK:** .NET 10.0.20x
**Goal:** Verify the three load-bearing claims that gate the full WinForms adapter implementation. If any fails, escalate before writing the production adapter.

## Why Phase 15 (and why now)

The "tollkühn idea" conversation surfaced that the most strategic adopter target for Marionette is *Legacy LOB apps where the source is gone* — and those are dominantly WinForms, not WPF. WinForms has no Marionette adapter today; the `Marionette.NET.Adapter.WinForms` package is the prerequisite for both:

1. Hand-decorated WinForms adopters (the existing Marionette pattern, just on a different framework).
2. The future `Marionette.NET.Inject` IL-rewriter — without a WinForms adapter, the inject tool would have nothing to attach into for its biggest target market.

Phase 14's `Win32InputInjector` is the lucky head start: it's framework-agnostic by design, so simulate_input is mostly free.

## The three load-bearing claims

Same pattern as Phase 0 (Spike A: stripping; Spike B: AOT; Spike C: stdio). One spike, three claims. If all three pass cleanly the full adapter implementation is mechanical. If any fails, we redesign before writing production code.

### Claim 1 — UI-thread marshalling via `Control.Invoke`

The runtime calls `IUiAutomationAdapter.DispatchAsync(action, ct)` from a background thread (the MCP request handler). The adapter must marshal the action onto the WinForms UI thread. WPF uses `Dispatcher.InvokeAsync`; WinForms equivalent is `Control.Invoke` / `Control.BeginInvoke`.

**What we verify:**
- Calling `Control.BeginInvoke` from a background thread successfully runs an action on the UI thread (where `Form.Handle` was created).
- `InvokeRequired` reports `true` from the background and `false` from the UI thread.
- A `Task<T>` wrapper around `BeginInvoke` returns the action's result correctly.
- Cancellation via `CancellationToken` is honoured before scheduling.

**Risk if failed:** Adapter design changes — would need a custom message-pump or different threading model. Material redesign.

### Claim 2 — Screenshot via `Form.DrawToBitmap` → PNG

WPF uses `RenderTargetBitmap` + `PngBitmapEncoder`. WinForms has `Control.DrawToBitmap(Bitmap, Rectangle)` which paints the control onto a GDI bitmap. We then save as PNG via `Bitmap.Save(stream, ImageFormat.Png)`.

**What we verify:**
- `Form.DrawToBitmap` produces a bitmap whose pixel dimensions match the form's `ClientSize`.
- Saving as PNG produces a non-empty byte array starting with the PNG magic bytes (`89 50 4E 47`).
- Capture works for both the form root AND a named child control (`Form.Controls.Find(name, recursive: true)`).
- DPI awareness: under high-DPI Windows, the captured bitmap reflects the logical control size — we document the exact behaviour and note any deviation from WPF's pixel-accurate path.

**Known limitation we flag explicitly:** `DrawToBitmap` does NOT capture overlay popups (tooltips, dropdown menus, modal dialogs that are top-level windows). Documented in adapter, fallback can be `Graphics.CopyFromScreen` of the form's screen rectangle if needed later.

**Risk if failed:** Adapter ships with no screenshot support, falling back to `screenshot_not_supported` structured error like NoOpAdapter. Material reduction in feature parity but not blocking.

### Claim 3 — `Win32InputInjector` against a WinForms control

Phase 14's Win32 SendInput-based injector is OS-level — Windows turns synthetic input into the framework's native event regardless of which framework owns the focused window. We verify that flow on a WinForms form.

**What we verify:**
- A WinForms `Button` placed on a visible form, focused, then targeted with `Win32InputInjector.SendKeyPress(VirtualKey.Space)` raises its `Click` event (Space activates focused button in WinForms).
- A WinForms `TextBox`, focused, then targeted with `Win32InputInjector.SendUnicodeText("hello")` ends up with `text == "hello"` (verifying TextChanged fires through the real framework pipeline).
- Mouse position via `SendMouseMoveAbsolute(screenX, screenY)` followed by `SendMouseClick(MouseButton.Left)` raises `Click` on a button at that screen position.

**Risk if failed:** Would mean Phase 14's reuse story is broken — extremely unlikely since SendInput is OS-level. If it fails, the cause is almost certainly a focus / window-Z-order issue specific to the spike harness, not a SendInput defect.

## Spike harness shape

`.phase15/SpikeWinForms/SpikeWinForms.csproj` — minimal `net10.0-windows`, `UseWindowsForms=true`, `WinExe`. Single `Program.cs`:

```
[STAThread]
static int Main(string[] args)
{
    var form = new Form { Text = "Spike", ClientSize = new Size(400, 200) };
    var button = new Button { Name = "TestButton", Text = "Click me", Location = new Point(20, 20) };
    int clickCount = 0;
    button.Click += (_, _) => clickCount++;
    var textbox = new TextBox { Name = "TestText", Location = new Point(20, 60), Width = 200 };
    form.Controls.Add(button);
    form.Controls.Add(textbox);

    form.Shown += async (_, _) =>
    {
        var results = new List<(string Claim, bool Pass, string Detail)>();
        // Claim 1: BeginInvoke marshalling
        results.Add(await VerifyDispatchAsync(form));
        // Claim 2: DrawToBitmap PNG
        results.Add(VerifyScreenshot(form, button));
        // Claim 3: Win32InputInjector against this form
        results.Add(VerifyWin32Input(form, button, textbox, () => clickCount));

        // Print to stderr (stdout is reserved for future stdio handshakes).
        // Spike harness writes verdict and exits.
        ReportAndExit(results);
    };
    Application.Run(form);
    return 0;
}
```

The harness does NOT depend on Marionette runtime/adapter projects — it's a pure verification of the underlying mechanisms. It DOES depend on `Marionette.NET.Runtime` for `Win32InputInjector` (the reuse claim is the whole point of Claim 3).

## Pass criteria

All three claims report `Pass`. Spike findings document goes to `.phase15/spike-a-findings.md` with:
- Build command output (0 warnings, 0 errors)
- Per-claim verdict + measured evidence (PNG byte length, click count, BeginInvoke thread IDs)
- Any gotchas discovered for the production adapter to handle

If any claim is `Partial` or `Fail`, the findings doc captures the exact reproduction and the redesign required before the full adapter work begins.

## Out of scope for the spike

- Multi-window tracking (Phase 3.3 contract; production adapter problem)
- Visual-tree finder (production adapter problem)
- raise_event reflection on WinForms events (production adapter problem; CLR events on Controls)
- AOT publish smoke (Phase 15 close-out, after full adapter exists)
- Source-gen integration (already framework-agnostic)
