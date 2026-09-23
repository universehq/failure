using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Failure.UnitTests;

public class GeneratorDiagnosticTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);
    private static readonly ImmutableArray<MetadataReference> References =
    [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Append(typeof(CompilerServices.FailureAttribute).Assembly.Location)
            .Append(typeof(Polyester.Error.IError).Assembly.Location)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(static path => MetadataReference.CreateFromFile(path)),
    ];
    private static readonly string[] Expected = ["FAILURE004"];

    [TestCase("[FailureImpl][Failure(\"message\")] public class Error { }", "FAILURE001")]
    [TestCase(
        "public class Container { [FailureImpl][Failure(\"message\")] public partial class Error { } }",
        "FAILURE001"
    )]
    [TestCase("[FailureImpl][Failure(\"message\")] file partial class Error { }", "FAILURE001")]
    [TestCase("[FailureImpl] public class Error { }", "FAILURE001")]
    [TestCase("[FailureImpl] public static partial class Error { }", "FAILURE002")]
    [TestCase("[FailureImpl] public ref partial struct Error { }", "FAILURE002")]
    [TestCase("[FailureImpl(Transparent = true)] public partial class Error { }", "FAILURE005")]
    [TestCase("[FailureImpl(Transparent = true)] public partial class Error { public Polyester.Error.IError Left => null!; public Polyester.Error.IError Right => null!; }", "FAILURE005")]
    [TestCase("[FailureImpl(Transparent = true)][Failure(\"own message\")] public partial class Error { public Polyester.Error.IError Source => null!; }", "FAILURE002")]
    [TestCase("[FailureImpl(Transparent = true)] public partial class Error { public Polyester.Error.IError Source => null!; public override string ToString() => \"own message\"; }", "FAILURE002")]
    [TestCase("[FailureImpl(Transparent = true)] public readonly partial union Error(string);", "FAILURE006")]
    [TestCase("[FailureImpl] public partial class Error { public string Source => \"not an error\"; }", "FAILURE007")]
    [TestCase("[FailureImpl] public partial class Error { public static Polyester.Error.IError Source => null!; }", "FAILURE007")]
    [TestCase("[FailureImpl][Failure(\"message\")] public static partial class Error { }", "FAILURE002")]
    [TestCase("[FailureImpl][Failure(\"message\")] public ref partial struct Error { }", "FAILURE002")]
    [TestCase("[FailureImpl][Failure(\"{Missing}\")] public partial record Error(string Name);", "FAILURE003")]
    [TestCase("[FailureImpl][Failure(\"{Name\")] public partial record Error(string Name);", "FAILURE003")]
    [TestCase("[FailureImpl][Failure(\"bad }\")] public partial record Error;", "FAILURE003")]
    [TestCase("[FailureImpl][Failure(null)] public partial record Error;", "FAILURE003")]
    [TestCase(
        "[FailureImpl][Failure(\"{Name.ToString()}\")] public partial record Error(string Name);",
        "FAILURE003"
    )]
    [TestCase("[FailureImpl][Failure(\"{Name,Name}\")] public partial record Error(string Name);", "FAILURE003")]
    [TestCase(
        "[FailureImpl][Failure(\"{Name}\")] public partial class Error { public static string Name => \"name\"; }",
        "FAILURE003"
    )]
    [TestCase(
        "[FailureImpl][Failure(\"{Name}\")] public partial class Error { public string Name { set { } } }",
        "FAILURE003"
    )]
    [TestCase(
        "public class Base { public string Name { private get; set; } = \"\"; } [FailureImpl][Failure(\"{Name}\")] public partial class Error : Base { }",
        "FAILURE003"
    )]
    [TestCase("[Failure(\"{Missing}\")] public record Leaf(string Name); [FailureImpl] public readonly partial union Error(Leaf);", "FAILURE003")]
    public void InvalidDeclarationsReportActionableDiagnostics(string source, string expectedId)
    {
        var compilation = CreateCompilation("using Failure.CompilerServices;\n" + source);
        var result = CreateDriver().RunGenerators(compilation).GetRunResult();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                result.Diagnostics.Select(static diagnostic => diagnostic.Id),
                Is.EqualTo([expectedId])
            );
            Assert.That(result.GeneratedTrees, Is.Empty);
            Assert.That(result.Results[0].Exception, Is.Null);
        }
    }

    [Test]
    public void MissingPolyesterReferenceReportsDiagnostic()
    {
        var compilation = CreateCompilation(
            """
            using Failure.CompilerServices;
            [FailureImpl] public readonly partial union Error(string);
            """
        );
        compilation = compilation.RemoveReferences(
            compilation.References.Where(reference =>
                reference.Display == typeof(Polyester.Error.IError).Assembly.Location
            )
        );

        var result = CreateDriver().RunGenerators(compilation).GetRunResult();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                result.Diagnostics.Select(static diagnostic => diagnostic.Id),
                Is.EqualTo(Expected)
            );
            Assert.That(result.GeneratedTrees, Is.Empty);
        }
    }

    [Test]
    public void FailureAttributeAloneDoesNotGenerateSource()
    {
        var compilation = CreateCompilation(
            "[Failure.CompilerServices.Failure(\"Missing {Name}\")] public record Error(string Name);"
        );
        var result = CreateDriver().RunGenerators(compilation).GetRunResult();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Diagnostics, Is.Empty);
            Assert.That(result.GeneratedTrees, Is.Empty);
        }
    }

    [Test]
    public void InvalidFailureAttributeReportsWithoutGeneratingSource()
    {
        var compilation = CreateCompilation(
            """
            using Failure.CompilerServices;
            [Failure("Can not find file {Filenamex}")]
            public readonly record struct Disconnect
            {
                public readonly required string Filename { get; init; }
            }
            """
        );
        var result = CreateDriver().RunGenerators(compilation).GetRunResult();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Diagnostics.Select(static diagnostic => diagnostic.Id),
                Is.EqualTo(["FAILURE003"]));
            Assert.That(result.GeneratedTrees, Is.Empty);
        }
    }

    [Test]
    public void UnionOnlyReportsInaccessibleCaseMemberOnce()
    {
        var compilation = CreateCompilation(
            """
            using Failure.CompilerServices;
            [Failure("{Hidden}")]
            public record Leaf
            {
                private string Hidden => "private";
            }
            [FailureImpl]
            public readonly partial union Error(Leaf);
            """
        );
        var result = CreateDriver().RunGenerators(compilation).GetRunResult();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Diagnostics.Select(static diagnostic => diagnostic.Id),
                Is.EqualTo(["FAILURE003"]));
            Assert.That(result.GeneratedTrees, Is.Empty);
        }
    }

    [Test]
    public void NamespacesGenericAritiesAndPartialDeclarationsProduceDistinctValidSources()
    {
        var compilation = CreateCompilation(
            """
            using Failure.CompilerServices;
            [FailureImpl][Failure("global")] public partial class Error { }
            [FailureImpl][Failure("{Data}")] public partial class Error<T> { public T Data = default!; }
            namespace First
            {
                [FailureImpl][Failure("{Name}")] public partial record Error;
                public partial record Error { public string Name => "first"; }
            }
            namespace Second
            {
                [FailureImpl][Failure("second")] public partial record Error;
            }
            namespace @event
            {
                [FailureImpl][Failure("keyword")] public partial class @class { }
            }
            """
        );

        var driver = CreateDriver()
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(diagnostics, Is.Empty);
            Assert.That(
                output
                    .GetDiagnostics()
                    .Where(static diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning),
                Is.Empty
            );
            Assert.That(driver.GetRunResult().GeneratedTrees, Has.Length.EqualTo(5));
            Assert.That(
                driver
                    .GetRunResult()
                    .Results[0]
                    .GeneratedSources.Select(static source => source.HintName)
                    .Distinct()
                    .Count(),
                Is.EqualTo(5)
            );
        }
    }

    [Test]
    public void ExplicitInterfaceSourceIsPreserved()
    {
        var compilation = CreateCompilation(
            """
            using Failure.CompilerServices;
            [FailureImpl]
            public readonly partial union Error(string) : Polyester.Error.IError
            {
                Polyester.Error.IError? Polyester.Error.IError.Source => null;
            }
            """
        );
        CreateDriver()
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(diagnostics, Is.Empty);
            Assert.That(
                output
                    .GetDiagnostics()
                    .Where(static diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning),
                Is.Empty
            );
        }
    }

    [Test]
    public void TransparentUnionAcceptsACaseGeneratedAsAnErrorInTheSameCompilation()
    {
        var compilation = CreateCompilation(
            """
            using Failure.CompilerServices;
            [Failure("{Name}")]
            [FailureImpl]
            public partial record Cause(string Name);
            [FailureImpl(Transparent = true)]
            public readonly partial union Wrapper(Cause);
            """
        );
        CreateDriver()
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(diagnostics, Is.Empty);
            Assert.That(
                output.GetDiagnostics().Where(static diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning),
                Is.Empty
            );
        }
    }

    [Test]
    public void InheritedMembersAndGenericParameterAttributesCompile()
    {
        var compilation = CreateCompilation(
            """
            using Failure.CompilerServices;
            using System;
            [AttributeUsage(AttributeTargets.GenericParameter)]
            public class MarkerAttribute : Attribute { }
            public class Base { protected string Name => "inherited"; }
            [FailureImpl][Failure("{Name}")]
            public partial class Error<[Marker] T> : Base where T : class { }
            """
        );
        CreateDriver()
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(diagnostics, Is.Empty);
            Assert.That(
                output
                    .GetDiagnostics()
                    .Where(static diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning),
                Is.Empty
            );
        }
    }

    [Test]
    public void ReusingDriverUpdatesTemplatesAfterAnEdit()
    {
        var compilation = CreateCompilation(
            """
            [Failure.CompilerServices.FailureImpl]
            [Failure.CompilerServices.Failure("before {Name}")]
            public partial record Error(string Name);
            """
        );
        var driver = CreateDriver().RunGenerators(compilation);
        var originalTree = compilation.SyntaxTrees.Single();
        var updatedTree = CSharpSyntaxTree.ParseText(
            originalTree.ToString().Replace("before", "after"),
            ParseOptions
        );
        compilation = compilation.ReplaceSyntaxTree(originalTree, updatedTree);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out var output,
            out var diagnostics
        );

        using (Assert.EnterMultipleScope())
        {
            Assert.That(diagnostics, Is.Empty);
            Assert.That(
                output
                    .GetDiagnostics()
                    .Where(static diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning),
                Is.Empty
            );
            Assert.That(
                driver.GetRunResult().GeneratedTrees.Single().ToString(),
                Does.Contain("after {Name}").And.Not.Contain("before")
            );
        }
    }

    private static CSharpCompilation CreateCompilation(string source) =>
        CSharpCompilation.Create(
            "GeneratorTests",
            new[] { CSharpSyntaxTree.ParseText(source, ParseOptions) },
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable
            )
        );

    private static GeneratorDriver CreateDriver() =>
        CSharpGeneratorDriver.Create(
            [new SourceGenerator.FailureSourceGenerator().AsSourceGenerator()],
            parseOptions: ParseOptions
        );
}
