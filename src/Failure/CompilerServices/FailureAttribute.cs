namespace Failure.CompilerServices;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false)]
public sealed class FailureImplAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false)]
public sealed class FailureAttribute(string format) : Attribute
{
    public string Format { get; } = format;
    public bool OverrideString { get; set; } = true;
}
