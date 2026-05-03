// Marionette.NET — handler disposable utility (Phase 1.6)
//
// Tiny IDisposable wrapper around an Action used by the source-generator-emitted
// EventDescriptor.Subscribe lambdas to detach event handlers on disposal:
//
//   Subscribe: static (instance, callback) =>
//   {
//       var typed = (T)instance;
//       EventHandler<TArgs> handler = (s, e) => callback(e);
//       typed.SomeEvent += handler;
//       return new HandlerDisposable(() => typed.SomeEvent -= handler);
//   }
//
// Living under Marionette.NET.Runtime.Internal so adopters do not see it on
// auto-complete; the source generator emits a fully-qualified reference
// (`global::Marionette.NET.Runtime.Internal.HandlerDisposable`) so the type is
// always resolvable from generated code.

using System;
using System.Threading;

namespace Marionette.NET.Runtime.Internal;

/// <summary>
/// Internal disposable used by the source-generator-emitted
/// <c>EventDescriptor.Subscribe</c> lambdas to detach <c>EventHandler</c> /
/// <c>EventHandler&lt;T&gt;</c> handlers on disposal.
/// </summary>
public sealed class HandlerDisposable : IDisposable
{
    private Action? _detach;

    /// <summary>
    /// Initializes a new <see cref="HandlerDisposable"/> with the
    /// <paramref name="detach"/> callback to invoke on first <see cref="Dispose"/>.
    /// </summary>
    /// <param name="detach">
    /// The detach callback (typically <c>() => instance.Event -= handler;</c>).
    /// Subsequent <see cref="Dispose"/> calls are no-ops.
    /// </param>
    public HandlerDisposable(Action detach)
    {
        _detach = detach ?? throw new ArgumentNullException(nameof(detach));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        var d = Interlocked.Exchange(ref _detach, null);
        d?.Invoke();
    }
}
