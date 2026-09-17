# Failure

Generate formatted failure values and `Polyester.Error.IError` implementations for
C# unions. The solution targets .NET 11 and requires a compiler with union support.

```csharp
using Failure.CompilerServices;

[Failure("Can not find file {Filename} with size {LimitSize}")]
public readonly partial record struct NotFound(string Filename, int LimitSize);

[Failure("Disconnected from {Host}")]
public readonly partial record struct Disconnect(string Host);

[FailureImpl]
public readonly partial union HyperError(Disconnect);

[FailureImpl]
public readonly partial union FileFailure(NotFound, HyperError);
```

`[Failure]` generates `Message` and overrides `ToString()` with the formatted
message. Placeholders name readable instance fields or properties, including
record constructor properties. Alignment and format specifiers work as in C#
interpolation (`{LimitSize,8:D4}`); use `{{` and `}}` for literal braces.
Formatting uses the current culture. Set `OverrideString = false` to generate
only `Message`, preserving the normal `ToString()` behavior. Existing explicit
members are preserved.

`[FailureImpl]` adds `IError` to a partial union unless it already implements it,
and generates:

- `Message`: the contained value's `ToString()`, or an empty string for null.
- `Source`: the contained value if it implements `IError`, otherwise null.
- `ToString()`: the union's `Message`, so nested unions format consistently.

`[Failure]` alone does not add `IError`. In this example a `NotFound` case has no
source, while a `HyperError` case exposes that error as its immediate source.
An inner union can receive its `IError` implementation in the same generator run.
Default unions produce an empty message and a null source. Custom `Message`,
`Source`, and `ToString()` members take precedence over generation.

Annotated types and their containing types must be partial. Nested types,
generics, and escaped identifiers are supported. The generator reports
`FAILURE001` for non-partial or file-local declarations, `FAILURE002` for
unsupported targets, `FAILURE003` for invalid templates, and `FAILURE004` when
`Polyester.Error.IError` cannot be resolved.

The `Failure` NuGet package bundles the generator as an analyzer. When using
project references, also reference the generator explicitly:

```xml
<ProjectReference Include="path/to/Failure/Failure.csproj" />
<ProjectReference Include="path/to/Failure.SourceGenerator/Failure.SourceGenerator.csproj"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

Run the tests with `dotnet test failure.slnx`.
