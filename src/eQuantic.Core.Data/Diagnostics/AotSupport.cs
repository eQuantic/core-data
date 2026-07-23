using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace eQuantic.Core.Data.Diagnostics;

/// <summary>
///     Roots, for the trimmer and NativeAOT, the user-defined operators of the value types filters compare on.
///     A filter like <c>x =&gt; x.Price &lt; 50m</c> — or its wire-format equivalent — is realized through
///     <c>Expression.MakeBinary(LessThan, …)</c>, which looks up <c>decimal.op_LessThan</c> by reflection at
///     runtime; without this root the trimmer removes those operators (nothing references them statically) and
///     the comparison throws under AOT. Money and dates are the common cases, so their operators are preserved
///     here via a module initializer (always a trim root). Comparisons on primitives (int, long, …) use IL
///     opcodes, not operator methods, and need no rooting.
/// </summary>
internal static class AotSupport
{
    [ModuleInitializer]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(decimal))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(DateTime))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(DateTimeOffset))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(TimeSpan))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(TimeOnly))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(DateOnly))]
    internal static void PreserveComparisonOperators()
    {
    }
}
