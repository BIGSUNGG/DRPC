namespace DRPC;

/// <summary>
/// Pre-declares the allowed types for each type-parameter slot of a <see cref="RemoteProcedure"/> generic method.
/// Calling with an undeclared type argument fails both at compile time (DRPCGEN008) and at runtime.
/// </summary>
/// <example>
/// <code>
/// [RemoteProcedure(methodId: 7)]
/// [GenericProcedure(typeof(int), typeof(string))]          // slot 0 (T)
/// T GetDefault&lt;T&gt;();
///
/// [RemoteProcedure(methodId: 8)]
/// [GenericProcedure(0, typeof(int), typeof(string))]       // T1 slot
/// [GenericProcedure(1, typeof(float), typeof(double))]     // T2 slot
/// T1 Pick&lt;T1, T2&gt;(T2 low, T2 high);
///
/// [RemoteProcedure(methodId: 9)]
/// void Deliver&lt;T&gt;(Package&lt;T&gt; box);   // if Package&lt;T&gt; declares a [GenericMessage] configuration,
///                                     // T inherits its type set without [GenericProcedure].
/// </code>
/// </example>
/// <remarks>
/// The allowed combinations are the Cartesian product of the per-slot lists, and the wire configuration
/// index is derived deterministically from declaration order (odometer order, slot 0 slowest-moving) —
/// reordering the lists breaks compatibility with existing peers.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class GenericProcedureAttribute : System.Attribute
{
    /// <summary>The zero-based index of the type parameter this declaration targets (the Nth type parameter of the method).</summary>
    public int Slot { get; }

    /// <summary>The types allowed for this slot. The list order directly determines the wire configuration index.</summary>
    public System.Type[] Types { get; }

    /// <summary>Declares slot 0. Example: <c>[GenericProcedure(typeof(int), typeof(string))]</c>.</summary>
    public GenericProcedureAttribute(params System.Type[] types)
        : this(0, types)
    {
    }

    /// <summary>Declares the given slot. Example: <c>[GenericProcedure(1, typeof(float))]</c>.</summary>
    public GenericProcedureAttribute(int slot, params System.Type[] types)
    {
        if (types is null || types.Length == 0)
        {
            throw new System.ArgumentException("At least one type must be declared.", nameof(types));
        }

        Slot = slot;
        Types = types;
    }
}
