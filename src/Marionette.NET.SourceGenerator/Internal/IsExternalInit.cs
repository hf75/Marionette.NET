// Polyfill for `System.Runtime.CompilerServices.IsExternalInit` on
// netstandard2.0. Same shim as Marionette.NET.Abstractions/Internal — duplicated
// here because source generators target ns2.0 and CANNOT reference the
// Abstractions assembly (the Roslyn host loads them in different contexts).
//
// `init`-only setters are required for record types' default codegen to
// preserve immutability. The compiler emits a reference to this type whenever
// it sees an `init` setter; the type ships in net5.0+ but is missing from
// netstandard2.0. Declaring our own marker satisfies the compiler at no
// runtime cost.

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
