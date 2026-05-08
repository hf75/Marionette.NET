// Phase 15 Spike A harness — WinForms adapter foundation
//
// Verifies three load-bearing claims that gate the full WinForms adapter:
//   Claim 1: Control.BeginInvoke marshals from a background thread to the UI
//            thread, returning a result via Task<T>.
//   Claim 2: Form.DrawToBitmap produces a valid PNG.
//   Claim 3: Win32InputInjector (Phase 14) drives a WinForms control through
//            the OS input pipeline (focused button receives Click via Space,
//            focused TextBox receives typed text, mouse-move + click hits a
//            screen-coordinate button).
//
// Output: stderr verdict line + "spike-a-result.txt" written next to the .exe
// containing per-claim pass/fail + measured evidence. Process exit code:
//   0 — all three pass
//   1 — at least one failed (verdict file holds details)

using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

using Marionette.Runtime.Internal;

namespace SpikeWinForms;

internal static class Program
{
    private static readonly List<(string Claim, bool Pass, string Detail)> s_results = new();

    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Build the harness form. Buttons / textbox are placed at fixed
        // screen-relative coords that the input injection can target.
        var form = new SpikeForm();

        // Once the form is shown and the message loop is running, fire the
        // verification tasks on a background thread so we can prove the
        // marshalling claim from the right thread context.
        form.Shown += (_, _) =>
        {
            // Run the verification on a Task so we're definitively NOT on
            // the UI thread when calling BeginInvoke. The form stays
            // responsive — the message loop pumps while we measure.
            _ = Task.Run(async () =>
            {
                try
                {
                    s_results.Add(await VerifyDispatchAsync(form));
                    s_results.Add(VerifyScreenshot(form));
                    s_results.Add(await VerifyWin32InputAsync(form));
                }
                catch (Exception ex)
                {
                    s_results.Add(("UNHANDLED", false, ex.ToString()));
                }
                finally
                {
                    // Marshal exit back to UI thread so Application.Exit is clean.
                    form.BeginInvoke(new Action(() =>
                    {
                        WriteResultsFile();
                        Application.Exit();
                    }));
                }
            });
        };

        Application.Run(form);

        // Determine exit code from collected results.
        var allPass = s_results.Count == 3 && s_results.All(r => r.Pass);
        Console.Error.WriteLine(allPass
            ? "[spike-a] VERDICT: PASS (3/3 claims)"
            : $"[spike-a] VERDICT: FAIL ({s_results.Count(r => r.Pass)}/{s_results.Count} claims)");
        return allPass ? 0 : 1;
    }

    // -----------------------------------------------------------------------
    // Claim 1: Control.BeginInvoke marshalling
    // -----------------------------------------------------------------------

    private static async Task<(string, bool, string)> VerifyDispatchAsync(SpikeForm form)
    {
        var sb = new StringBuilder();
        var bgId = Environment.CurrentManagedThreadId;
        sb.Append("bg-thread=").Append(bgId).Append(", ");

        // Action variant
        var actionRanOnId = -1;
        var tcs = new TaskCompletionSource();
        form.BeginInvoke(new Action(() =>
        {
            actionRanOnId = Environment.CurrentManagedThreadId;
            tcs.SetResult();
        }));
        await tcs.Task.ConfigureAwait(false);
        sb.Append("action-thread=").Append(actionRanOnId).Append(", ");

        // Func<T> variant via Task<T> wrapper
        var funcResult = await DispatchAsync(form, () =>
        {
            // We're on the UI thread now; reading form.Text is safe.
            return form.Text + "/" + Environment.CurrentManagedThreadId;
        }).ConfigureAwait(false);
        sb.Append("func-result=").Append(funcResult);

        // InvokeRequired check
        var invokeRequiredFromBg = form.InvokeRequired;
        sb.Append(", invokeRequired-from-bg=").Append(invokeRequiredFromBg);

        var pass =
            actionRanOnId != -1 &&
            actionRanOnId != bgId &&
            funcResult.StartsWith("Spike", StringComparison.Ordinal) &&
            invokeRequiredFromBg;

        return ("Claim 1: Control.BeginInvoke marshalling", pass, sb.ToString());
    }

    private static Task<T> DispatchAsync<T>(Control control, Func<T> func)
    {
        if (!control.InvokeRequired)
        {
            try { return Task.FromResult(func()); }
            catch (Exception ex) { return Task.FromException<T>(ex); }
        }
        var tcs = new TaskCompletionSource<T>();
        control.BeginInvoke(new Action(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }));
        return tcs.Task;
    }

    // -----------------------------------------------------------------------
    // Claim 2: Form.DrawToBitmap → PNG
    // -----------------------------------------------------------------------

    private static (string, bool, string) VerifyScreenshot(SpikeForm form)
    {
        var sb = new StringBuilder();
        byte[]? formPng = null;
        byte[]? buttonPng = null;
        Size formSize = Size.Empty, buttonSize = Size.Empty;

        // Must run on UI thread.
        form.Invoke(new Action(() =>
        {
            formSize = form.ClientSize;
            using var fbmp = new Bitmap(formSize.Width, formSize.Height);
            form.DrawToBitmap(fbmp, new Rectangle(Point.Empty, formSize));
            using var fms = new MemoryStream();
            fbmp.Save(fms, ImageFormat.Png);
            formPng = fms.ToArray();

            var btn = form.Controls.Find("TestButton", true).FirstOrDefault();
            if (btn is not null)
            {
                buttonSize = btn.Size;
                using var bbmp = new Bitmap(buttonSize.Width, buttonSize.Height);
                btn.DrawToBitmap(bbmp, new Rectangle(Point.Empty, buttonSize));
                using var bms = new MemoryStream();
                bbmp.Save(bms, ImageFormat.Png);
                buttonPng = bms.ToArray();
            }
        }));

        var formMagic = formPng is { Length: >= 4 } &&
                        formPng[0] == 0x89 && formPng[1] == 0x50 &&
                        formPng[2] == 0x4E && formPng[3] == 0x47;
        var buttonMagic = buttonPng is { Length: >= 4 } &&
                          buttonPng[0] == 0x89 && buttonPng[1] == 0x50 &&
                          buttonPng[2] == 0x4E && buttonPng[3] == 0x47;

        sb.Append("form-png=").Append(formPng?.Length ?? 0).Append("B (")
          .Append(formSize.Width).Append('x').Append(formSize.Height).Append("), ");
        sb.Append("button-png=").Append(buttonPng?.Length ?? 0).Append("B (")
          .Append(buttonSize.Width).Append('x').Append(buttonSize.Height).Append("), ");
        sb.Append("magic-form=").Append(formMagic).Append(", magic-button=").Append(buttonMagic);

        var pass = formMagic && buttonMagic && formPng!.Length > 100 && buttonPng!.Length > 50;
        return ("Claim 2: Form.DrawToBitmap → PNG", pass, sb.ToString());
    }

    // -----------------------------------------------------------------------
    // Claim 3: Win32InputInjector against WinForms controls
    // -----------------------------------------------------------------------

    private static async Task<(string, bool, string)> VerifyWin32InputAsync(SpikeForm form)
    {
        var sb = new StringBuilder();

        if (!Win32InputInjector.IsAvailable)
        {
            return ("Claim 3: Win32InputInjector reuse", false,
                "Win32InputInjector.IsAvailable == false (not on Windows?)");
        }

        // 3a — focus button, send Space, expect Click handler to bump counter.
        var beforeClicks = 0;
        var afterSpaceClicks = 0;
        await DispatchAsync(form, () =>
        {
            beforeClicks = form.ClickCount;
            var btn = form.Controls.Find("TestButton", true).First();
            btn.Focus();
            return 0;
        }).ConfigureAwait(false);
        // Allow the focus to take effect.
        await Task.Delay(100).ConfigureAwait(false);
        Win32InputInjector.SendKeyPress(VirtualKey.Space);
        await Task.Delay(200).ConfigureAwait(false);
        await DispatchAsync(form, () =>
        {
            afterSpaceClicks = form.ClickCount;
            return 0;
        }).ConfigureAwait(false);
        var spacePass = afterSpaceClicks == beforeClicks + 1;
        sb.Append("space-click=").Append(beforeClicks).Append("→").Append(afterSpaceClicks)
          .Append(" (").Append(spacePass ? "OK" : "FAIL").Append("), ");

        // 3b — focus textbox, send Unicode "hello", expect TestText.Text == "hello".
        await DispatchAsync(form, () =>
        {
            var tb = (TextBox)form.Controls.Find("TestText", true).First();
            tb.Text = string.Empty;
            tb.Focus();
            return 0;
        }).ConfigureAwait(false);
        await Task.Delay(100).ConfigureAwait(false);
        Win32InputInjector.SendUnicodeText("hello");
        await Task.Delay(200).ConfigureAwait(false);
        var textValue = await DispatchAsync(form, () =>
        {
            var tb = (TextBox)form.Controls.Find("TestText", true).First();
            return tb.Text;
        }).ConfigureAwait(false);
        var textPass = textValue == "hello";
        sb.Append("text=\"").Append(textValue).Append("\" (")
          .Append(textPass ? "OK" : "FAIL").Append("), ");

        // 3c — mouse-move to button screen position, then click.
        var (btnScreenX, btnScreenY, btnW, btnH) = await DispatchAsync(form, () =>
        {
            var btn = form.Controls.Find("TestButton", true).First();
            var screenPt = btn.PointToScreen(new Point(btn.Width / 2, btn.Height / 2));
            return (screenPt.X, screenPt.Y, btn.Width, btn.Height);
        }).ConfigureAwait(false);

        var beforeMouseClicks = afterSpaceClicks;  // last known counter
        // Bring focus back to form to ensure window receives input.
        await DispatchAsync(form, () => { form.Activate(); return 0; }).ConfigureAwait(false);
        await Task.Delay(100).ConfigureAwait(false);
        Win32InputInjector.SendMouseMoveAbsolute(btnScreenX, btnScreenY);
        await Task.Delay(50).ConfigureAwait(false);
        Win32InputInjector.SendMouseClick(MouseButton.Left);
        await Task.Delay(200).ConfigureAwait(false);
        var afterMouseClicks = await DispatchAsync(form, () => form.ClickCount).ConfigureAwait(false);
        var mousePass = afterMouseClicks == beforeMouseClicks + 1;
        sb.Append("mouse-click=").Append(beforeMouseClicks).Append("→").Append(afterMouseClicks)
          .Append(" @(").Append(btnScreenX).Append(',').Append(btnScreenY).Append(") (")
          .Append(mousePass ? "OK" : "FAIL").Append(')');

        var pass = spacePass && textPass && mousePass;
        return ("Claim 3: Win32InputInjector reuse", pass, sb.ToString());
    }

    private static void WriteResultsFile()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "spike-a-result.txt");
            using var sw = new StreamWriter(path, append: false, Encoding.UTF8);
            sw.WriteLine("Phase 15 Spike A — results");
            sw.WriteLine("================================");
            sw.WriteLine();
            foreach (var (claim, pass, detail) in s_results)
            {
                sw.WriteLine($"[{(pass ? "PASS" : "FAIL")}] {claim}");
                sw.WriteLine($"        {detail}");
                sw.WriteLine();
            }
            var allPass = s_results.Count == 3 && s_results.All(r => r.Pass);
            sw.WriteLine($"VERDICT: {(allPass ? "PASS" : "FAIL")}");
            Console.Error.WriteLine($"[spike-a] results written to {path}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[spike-a] could not write results file: {ex}");
        }
    }
}

internal sealed class SpikeForm : Form
{
    public int ClickCount { get; private set; }

    public SpikeForm()
    {
        Text = "Spike A";
        ClientSize = new Size(420, 220);
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;  // keep focused during the spike

        var button = new Button
        {
            Name = "TestButton",
            Text = "Click me",
            Location = new Point(20, 20),
            Size = new Size(120, 40),
        };
        button.Click += (_, _) => ClickCount++;
        Controls.Add(button);

        var textbox = new TextBox
        {
            Name = "TestText",
            Location = new Point(20, 80),
            Width = 240,
        };
        Controls.Add(textbox);

        var label = new Label
        {
            Name = "Status",
            Location = new Point(20, 120),
            Size = new Size(380, 80),
            Text = "Spike running... (auto-exits)",
        };
        Controls.Add(label);
    }
}
