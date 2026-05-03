// Marionette.NET — loop-protection service
//
// Implements Spielregel 3 from MASTERPLAN ("Hop-Counter / Loop-Protection"):
//
//   * Default depth limit: 5.
//   * Override via env var MARIONETTE_MAX_DEPTH.
//   * Decay window: 30 seconds. If no `invoke_method` and no `Ai.Trigger`
//     happens within 30 s, the counter resets to zero — long-running
//     conversations don't false-positive on the limit.
//   * On exceed: invoke_method returns a structured
//     `{success:false, errorCode:"loop_limit_exceeded"}` error.
//
// The hop counter is process-global rather than per-root so a chain like
//   Claude → invoke_method on Root A → Ai.Trigger from Root A → invoke_method on Root B
// is correctly caught. Root-A and Root-B in this scenario share the same
// trace; per-root counting would let the loop hide between them.

using System;
using System.Threading;

namespace Marionette.Runtime.Loop;

/// <summary>
/// Tracks the current call-chain depth for Marionette loop-protection. One
/// singleton per host. Both <c>invoke_method</c> and <c>Ai.Trigger</c>
/// increment via <see cref="TryEnterHop"/> / <see cref="RecordChannelHop"/>.
/// </summary>
public sealed class LoopProtectionService
{
    /// <summary>
    /// Default depth limit per Marionette MASTERPLAN Spielregel 3. Adopters
    /// override via the <c>MARIONETTE_MAX_DEPTH</c> environment variable.
    /// </summary>
    public const int DefaultMaxDepth = 5;

    /// <summary>
    /// Decay window. If no hop activity is observed within this window, the
    /// counter resets to zero so unrelated future calls don't carry over a
    /// stale chain.
    /// </summary>
    public static readonly TimeSpan DecayWindow = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private int _hops;
    private DateTime _lastActivityUtc = DateTime.MinValue;

    /// <summary>
    /// The configured maximum depth. Read at construction from
    /// <c>MARIONETTE_MAX_DEPTH</c> or defaulted to <see cref="DefaultMaxDepth"/>.
    /// </summary>
    public int MaxDepth { get; }

    public LoopProtectionService()
        : this(ResolveMaxDepthFromEnvironment())
    {
    }

    /// <summary>
    /// Construct with an explicit depth (used by tests; production code uses
    /// the parameterless ctor that reads the env var).
    /// </summary>
    public LoopProtectionService(int maxDepth)
    {
        if (maxDepth < 1) throw new ArgumentOutOfRangeException(nameof(maxDepth), "Must be >= 1.");
        MaxDepth = maxDepth;
    }

    /// <summary>
    /// Increment the hop counter for an incoming <c>invoke_method</c> call
    /// and return the new value, OR a sentinel indicating the limit was
    /// exceeded.
    /// </summary>
    /// <returns>
    /// A <see cref="HopOutcome"/> with the new depth. If
    /// <see cref="HopOutcome.Exceeded"/> is <see langword="true"/>, the caller
    /// must surface a <c>loop_limit_exceeded</c> error to the LLM and skip
    /// dispatch.
    /// </returns>
    public HopOutcome TryEnterHop()
    {
        lock (_gate)
        {
            DecayIfStale();
            _hops += 1;
            _lastActivityUtc = DateTime.UtcNow;
            var exceeded = _hops > MaxDepth;
            return new HopOutcome(_hops, exceeded);
        }
    }

    /// <summary>
    /// Record a channel-push hop (<c>Ai.Trigger</c> / <c>Ai.ScheduleTrigger</c>)
    /// and return the new depth. Channel pushes never block — they only
    /// contribute to the counter for the LLM-visible <c>hops</c> field.
    /// </summary>
    public int RecordChannelHop()
    {
        lock (_gate)
        {
            DecayIfStale();
            _hops += 1;
            _lastActivityUtc = DateTime.UtcNow;
            return _hops;
        }
    }

    /// <summary>
    /// Current depth without recording a new hop. Used for read-only telemetry.
    /// </summary>
    public int CurrentDepth
    {
        get
        {
            lock (_gate)
            {
                DecayIfStale();
                return _hops;
            }
        }
    }

    /// <summary>
    /// Force-reset the counter. Tests use this between scenarios; production
    /// code never calls this — the decay window handles the natural reset.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            _hops = 0;
            _lastActivityUtc = DateTime.MinValue;
        }
    }

    private void DecayIfStale()
    {
        if (_lastActivityUtc == DateTime.MinValue) return;
        if (DateTime.UtcNow - _lastActivityUtc > DecayWindow)
        {
            _hops = 0;
        }
    }

    private static int ResolveMaxDepthFromEnvironment()
    {
        var raw = Environment.GetEnvironmentVariable("MARIONETTE_MAX_DEPTH");
        if (!string.IsNullOrEmpty(raw) &&
            int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) &&
            parsed >= 1)
        {
            return parsed;
        }
        return DefaultMaxDepth;
    }
}

/// <summary>
/// Outcome of <see cref="LoopProtectionService.TryEnterHop"/>.
/// </summary>
/// <param name="Hops">The new hop counter value after recording the call.</param>
/// <param name="Exceeded"><see langword="true"/> if <see cref="Hops"/> is greater than the configured limit.</param>
public readonly record struct HopOutcome(int Hops, bool Exceeded);
