# Failure

Generate `Polyester.Error.IError` implementations for
classes, structs, records, and C# unions. The solution targets .NET 11 and requires
a compiler with union support when using unions.

```csharp
using Failure.CompilerServices;

[Failure("Can not find file {Filename} with size {LimitSize}")]
public readonly record struct NotFound(string Filename, int LimitSize);

[Failure("Disconnected from {Host}")]
public readonly record struct Disconnect(string Host);

[FailureImpl]
public readonly partial union HyperError(Disconnect);

[FailureImpl]
public readonly partial union FileFailure(NotFound, HyperError);

[Failure("Could not read {Filename}")]
[FailureImpl]
public partial record ReadFailure(string Filename);

[Failure("App crashed: {AppName}")]
[FailureImpl]
public readonly partial record struct AppError(string AppName, FileFailure Source);

[Failure("Could not run {Operation}")]
[FailureImpl]
public readonly partial record struct OperationError(
    string Operation,
    FileFailure Cause
)
{
    Polyester.Error.IError? Polyester.Error.IError.Source => Cause;
}

[FailureImpl(Transparent = true)]
public readonly partial record struct ErrorAlias(FileFailure Source);
```

Only `[FailureImpl]` triggers source generation. `[Failure("...")]` supplies a
message template and is independently validated: a bad placeholder reports
`FAILURE003` even when no `[FailureImpl]` uses it. It does not generate members
on its own. For example, `NotFound` is a plain record struct, not an `IError`;
`FileFailure` formats it using its `[Failure]` template.

On a class or struct with both attributes, the generator adds `Message` and
normally overrides `ToString()`. Set `OverrideString = false` to generate only
`Message`. On a union case, the union uses the case's template regardless of
that flag or the case's own `ToString()`; the case itself receives no generated
members and need not be partial. Existing user-defined members are preserved.

Placeholders name readable instance fields or properties, including record
constructor properties. For union cases, those members must also be accessible
from the union. Alignment and format specifiers work as in C# interpolation
(`{LimitSize,8:D4}`); use `{{` and `}}` for literal braces. Formatting uses the
current culture.

`[FailureImpl]` adds `IError` to any partial class or struct, including records
and unions, unless it already implements it. `IError` requires `ToString()` and
provides a default null `Source`. Ordinary classes and structs can define their
own `ToString()` and `Source`, or combine `[FailureImpl]` with `[Failure]` to
generate formatted `ToString()`. A nontransparent type with a readable `IError`
field or property named `Source` exposes that member as `IError.Source`. For a
differently named cause, implement `IError.Source` explicitly as in
`OperationError` above. The type's own `ToString()` provides its message. A
member named `Source` must be a readable instance `IError` member.

For nontransparent unions, `[FailureImpl]` generates:

- `Message`: the case's `[Failure]` template when present, otherwise the
  contained value's `ToString()`; an empty string for null.
- `Source`: the contained value if it implements `IError`, otherwise null.
- `ToString()`: the union's `Message`, so nested unions format consistently.

Use `[FailureImpl(Transparent = true)]` for a wrapper that adds no message of
its own. Do not combine it with `[Failure]`. Transparent wrappers forward
`ToString()` and `IError.Source` through the inner error, so they do not add a
link to the cause chain. A transparent union does not generate `Message`, and
every case must implement `IError` (`FAILURE006` otherwise). On classes and
structs, the generator chooses a readable `IError` member named `Source`, or
the only readable `IError` member. Missing or ambiguous inner errors report
`FAILURE005`.

In this example a `NotFound` case has no source, while a `HyperError` case
exposes that error as its immediate source.
An inner union can receive its `IError` implementation in the same generator run.
Default nontransparent unions produce an empty message and a null source.
Custom `Message`, `Source`, and `ToString()` members take precedence for
nontransparent unions; transparent wrappers must let the generator forward
`ToString()` and `IError.Source`.

Types with `[FailureImpl]` and their containing types must be partial. Nested types,
generics, and escaped identifiers are supported. The generator reports
`FAILURE001` for non-partial or file-local declarations, `FAILURE002` for
unsupported targets, `FAILURE003` for invalid templates, and `FAILURE004` when
`Polyester.Error.IError` cannot be resolved. `FAILURE005` reports an invalid
transparent member selection, and `FAILURE006` reports a non-error transparent
union case. `FAILURE007` reports an invalid `Source` member.

The `Failure` NuGet package bundles the generator as an analyzer. When using
project references, also reference the generator explicitly:

```xml
<ProjectReference Include="path/to/Failure/Failure.csproj" />
<ProjectReference Include="path/to/Failure.SourceGenerator/Failure.SourceGenerator.csproj"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

Run the tests with `dotnet test failure.slnx`.
