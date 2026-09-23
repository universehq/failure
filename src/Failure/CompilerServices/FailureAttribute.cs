namespace Failure.CompilerServices;

/// <summary>Triggers generation of a Polyester.Error.IError implementation.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false)]
public sealed class FailureImplAttribute : Attribute
{
    /// <summary>Forwards display and source to an inner error.</summary>
    public bool Transparent { get; set; }
}

/// <summary>
/// Supplies a message template consumed by FailureImpl on this type or a union that includes it.
/// This attribute does not trigger source generation by itself.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false)]
public sealed class FailureAttribute(string format) : Attribute
{
    public string Format { get; } = format;
    public bool OverrideString { get; set; } = true;
}
