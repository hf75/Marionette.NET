// Polyfill for `System.Runtime.CompilerServices.IsExternalInit` on
// netstandard2.0. The C# compiler emits a reference to this type whenever
// it sees an `init`-only setter; the type ships in net5.0+ but is missing
// from netstandard2.0. Declaring our own marker satisfies the compiler at
// no runtime cost — the type is purely a compile-time signalling shim.
//
// On net10.0 the framework type is present and the compiler binds against
// that one; this internal copy only matters for the netstandard2.0 build.

#if NETSTANDARD2_0
namespace System.Runtime.CompilerServices;

/// <summary>
/// Reserved by the C# compiler to mark <c>init</c>-only setters.
/// Polyfill for netstandard2.0 — see file header.
/// </summary>
internal static class IsExternalInit
{
}
#endif
