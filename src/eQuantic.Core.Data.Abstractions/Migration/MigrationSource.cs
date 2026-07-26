using System;
using System.Collections.Generic;

namespace eQuantic.Core.Data.Migration;

/// <summary>
///     Migrations registered <b>explicitly</b>, instead of discovered by scanning an assembly. Assembly
///     scanning is reflection: it finds nothing under NativeAOT, because a type reachable only through
///     reflection has its constructor trimmed. Naming the migration at the call site
///     (<c>source.Add&lt;WidgetsSetup&gt;()</c>) constructs it with a plain <c>new</c>, which the AOT compiler
///     roots — so the explicit form is both the AOT-safe registration and a faster startup (no scan).
/// </summary>
public sealed class MigrationSource
{
    private readonly List<Migration> _migrations = [];

    /// <summary>The registered migrations, in registration order (the runner orders them by timestamp).</summary>
    public IReadOnlyList<Migration> Migrations => _migrations;

    /// <summary>Registers a migration by type — constructed with <c>new</c>, so nothing is reflected.</summary>
    /// <typeparam name="TMigration">The migration type.</typeparam>
    /// <returns>The same source for chaining.</returns>
    public MigrationSource Add<TMigration>() where TMigration : Migration, new() => Add(new TMigration());

    /// <summary>Registers a migration instance (for migrations that take constructor arguments).</summary>
    /// <param name="migration">The migration.</param>
    /// <returns>The same source for chaining.</returns>
    public MigrationSource Add(Migration migration)
    {
        _migrations.Add(migration ?? throw new ArgumentNullException(nameof(migration)));
        return this;
    }
}
