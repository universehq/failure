namespace Failure.UnitTests;

public class GeneratorTests
{
    [Test]
    public void TestErrorFormat()
    {
        NotFound notFound = new() { Filename = "Program.cs", LimitSize = 20 };
        TestFailure error = notFound;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(error.ToString(), Is.EqualTo("Can not find file Program.cs with size 20"));
            Assert.That(error.Message, Is.EqualTo(error.ToString()));
        }
    }

    [Test]
    public void LeafCasesHaveNoSource()
    {
        TestFailure notFound = new NotFound { Filename = "Program.cs", LimitSize = 20 };
        TestFailure incomplete = new IncompleteData { Filename = "missing.cs" };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(notFound.Message, Is.EqualTo("Can not find file Program.cs with size 20"));
            Assert.That(notFound.Source, Is.Null);
            Assert.That(incomplete.Message, Is.EqualTo("Can not find file missing.cs"));
            Assert.That(incomplete.Source, Is.Null);
        }
    }

    [Test]
    public void NestedErrorsExposeTheirImmediateCause()
    {
        HyperError inner = new Disconnect { Filename = "remote.cs" };
        TestFailure outer = inner;
        Polyester.Error.IError error = outer;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outer.Message, Is.EqualTo("Can not find file remote.cs"));
            Assert.That(outer.ToString(), Is.EqualTo(outer.Message));
            Assert.That(error.Source, Is.TypeOf<HyperError>());
            Assert.That(error.Source!.ToString(), Is.EqualTo(inner.Message));
            Assert.That(error.Source.Source, Is.Null);
        }
    }

    [Test]
    public void SourcesIncludeInterfacesGeneratedInTheSameCompilation()
    {
        GeneratedInner inner = new Disconnect { Filename = "remote.cs" };
        GeneratedOuter outer = inner;
        Assert.That(outer.Source, Is.TypeOf<GeneratedInner>());
        Assert.That(outer.Source!.Source, Is.Null);
    }

    [Test]
    public void DefaultUnionsAreSafeToInspect()
    {
        TestFailure error = default;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(error.Message, Is.Empty);
            Assert.That(error.ToString(), Is.Empty);
            Assert.That(error.Source, Is.Null);
        }
    }

    [Test]
    public void TemplatesEscapeLiteralTextAndSupportAlignmentAndFormats()
    {
        FormattedFailure error = new("input.cs", 12);
        Assert.That(error.ToString(), Is.EqualTo("\"input.cs\" at C:\\temp\n{size= 0012}"));
    }

    [Test]
    public void UnionFormatsMetadataOnlyCases()
    {
        FormattedUnion error = new FormattedCase("go", 7);
        Assert.That(error.Message, Is.EqualTo("Code go:  007"));
    }

    [Test]
    public void UnionTemplateDoesNotChangeCaseToString()
    {
        CustomDisplayCase leaf = new("file.txt");
        CustomDisplayUnion error = leaf;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(leaf.ToString(), Is.EqualTo("manual display"));
            Assert.That(error.Message, Is.EqualTo("Missing file.txt"));
        }
    }

    [Test]
    public void OverrideStringCanBeDisabled()
    {
        MessageOnly error = new("input.cs");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(error.Message, Is.EqualTo("Missing input.cs"));
            Assert.That(error.ToString(), Is.EqualTo("MessageOnly { Filename = input.cs, Message = Missing input.cs }"));
        }
    }

    [Test]
    public void UserDefinedMembersArePreserved()
    {
        CustomFailure leaf = new();
        CustomError union = leaf;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(leaf.ToString(), Is.EqualTo("custom leaf"));
            Assert.That(union.Message, Is.EqualTo("custom message"));
            Assert.That(union.ToString(), Is.EqualTo("custom union"));
            Assert.That(union.Source, Is.Null);
        }
    }

    [Test]
    public void NestedGenericTypesAndEscapedIdentifiersCompile()
    {
        Container<string>.NestedFailure<int> error = new("input", 42);
        Container<string>.NestedError<int> union = error;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(union.Message, Is.EqualTo("input: 42"));
            Assert.That(new KeywordFailure("event").ToString(), Is.EqualTo("event"));
        }
    }

    [Test]
    public void ExternalErrorsPreserveTheirSourceChain()
    {
        ExternalError cause = new(null);
        ExternalError inner = new(cause);
        ExternalFailure outer = inner;
        Assert.That(outer.Source, Is.SameAs(inner));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(outer.Source!.Source, Is.SameAs(cause));
            Assert.That(outer.Message, Is.EqualTo("external error"));
        }
    }

    [Test]
    public void FailureImplSupportsFormattedClassesAndStructs()
    {
        Polyester.Error.IError record = new FormattedError("settings.json");
        Polyester.Error.IError value = new FormattedStructError("Worker");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(record.ToString(), Is.EqualTo("Missing settings.json"));
            Assert.That(record.Source, Is.Null);
            Assert.That(value.ToString(), Is.EqualTo("App crashed: Worker"));
            Assert.That(value.Source, Is.Null);
        }
    }

    [Test]
    public void FailureImplPreservesHandWrittenToStringAndSource()
    {
        Polyester.Error.IError cause = new PlainClassError("cause");
        Polyester.Error.IError wrapper = new PlainClassError("wrapper", cause);
        Polyester.Error.IError value = new PlainStructError(7);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(wrapper.ToString(), Is.EqualTo("wrapper"));
            Assert.That(wrapper.Source, Is.SameAs(cause));
            Assert.That(value.ToString(), Is.EqualTo("code 7"));
            Assert.That(value.Source, Is.Null);
        }
    }

    [Test]
    public void FailureImplRequiresNoOtherMembersOnClassesAndStructs()
    {
        Polyester.Error.IError emptyClass = new EmptyClassError();
        Polyester.Error.IError emptyStruct = new EmptyStructError();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(emptyClass.ToString(), Does.Contain(nameof(EmptyClassError)));
            Assert.That(emptyClass.Source, Is.Null);
            Assert.That(emptyStruct.ToString(), Does.Contain(nameof(EmptyStructError)));
            Assert.That(emptyStruct.Source, Is.Null);
        }
    }

    [Test]
    public void TransparentUnionForwardsItsInnerSource()
    {
        Polyester.Error.IError cause = new PlainClassError("cause");
        ExternalError inner = new(cause);
        Polyester.Error.IError wrapper = (TransparentFailure)inner;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(wrapper.ToString(), Is.EqualTo(inner.ToString()));
            Assert.That(wrapper.Source, Is.SameAs(cause));
        }
    }

    [Test]
    public void NonTransparentStructAddsItsSourceToTheChain()
    {
        TestFailure inner = new NotFound { Filename = "settings.json", LimitSize = 32 };
        AppError error = new() { AppName = "Worker", Source = inner };
        Polyester.Error.IError asError = error;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(error.Message, Is.EqualTo("App crashed: Worker"));
            Assert.That(error.ToString(), Is.EqualTo("App crashed: Worker"));
            Assert.That(asError.ToString(), Is.EqualTo("App crashed: Worker"));
            Assert.That(asError.Source, Is.TypeOf<TestFailure>());
            Assert.That(asError.Source!.ToString(), Is.EqualTo(inner.ToString()));
            Assert.That(error.Source, Is.EqualTo(inner));
        }
    }

    [Test]
    public void TransparentStructForwardsErrorGeneratedInTheSameCompilation()
    {
        TestFailure inner = new NotFound { Filename = "settings.json", LimitSize = 32 };
        Polyester.Error.IError error = new TransparentStructError(inner);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(error.ToString(), Is.EqualTo(inner.ToString()));
            Assert.That(error.Source, Is.Null);
        }
    }

    [Test]
    public void TransparentClassForwardsItsInnerError()
    {
        Polyester.Error.IError cause = new PlainClassError("cause");
        Polyester.Error.IError inner = new PlainClassError("inner", cause);
        Polyester.Error.IError wrapper = new TransparentClassError(inner);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(wrapper.ToString(), Is.EqualTo("inner"));
            Assert.That(wrapper.Source, Is.SameAs(cause));
        }
    }

    [Test]
    public void ExplicitSourceGetterAddsAnImmediateSourceLink()
    {
        Polyester.Error.IError cause = new PlainClassError("network");
        Polyester.Error.IError wrapper = new ExplicitCauseError(cause);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(wrapper.ToString(), Is.EqualTo("request failed"));
            Assert.That(wrapper.Source, Is.SameAs(cause));
        }
    }

    [Test]
    public void SourceGetterDisambiguatesTransparentWrapper()
    {
        Polyester.Error.IError cause = new PlainClassError("root");
        Polyester.Error.IError inner = new PlainClassError("inner", cause);
        Polyester.Error.IError wrapper = new GetterTransparentError(inner, new PlainClassError("other"));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(wrapper.ToString(), Is.EqualTo("inner"));
            Assert.That(wrapper.Source, Is.SameAs(cause));
        }
    }
}

[CompilerServices.FailureImpl]
public partial record ExplicitCauseError(Polyester.Error.IError Cause)
{
    Polyester.Error.IError? Polyester.Error.IError.Source => Cause;
    public override string ToString() => "request failed";
}

[CompilerServices.FailureImpl(Transparent = true)]
public partial record GetterTransparentError(
    Polyester.Error.IError Inner,
    Polyester.Error.IError Other
)
{
    public Polyester.Error.IError Source => Inner;
}

[CompilerServices.Failure("Missing {Path}")]
[CompilerServices.FailureImpl]
public partial record FormattedError(string Path);

[CompilerServices.Failure("App crashed: {AppName}")]
[CompilerServices.FailureImpl]
public readonly partial record struct FormattedStructError(string AppName);

[CompilerServices.FailureImpl(Transparent = true)]
public partial class TransparentClassError(Polyester.Error.IError inner)
{
    public Polyester.Error.IError Inner => inner;
}

[CompilerServices.FailureImpl(Transparent = true)]
public readonly partial record struct TransparentStructError(TestFailure Source);

[CompilerServices.FailureImpl]
public partial class PlainClassError(string text, Polyester.Error.IError? cause = null)
{
    public Polyester.Error.IError? Source => cause;
    public override string ToString() => text;
}

[CompilerServices.FailureImpl]
public readonly partial record struct PlainStructError(int Code)
{
    public override string ToString() => $"code {Code}";
}

[CompilerServices.FailureImpl]
public partial class EmptyClassError;

[CompilerServices.FailureImpl]
public partial struct EmptyStructError;

[CompilerServices.FailureImpl(Transparent = true)]
public readonly partial union TransparentFailure(ExternalError);

[CompilerServices.FailureImpl]
public readonly partial union GeneratedInner(Disconnect);

[CompilerServices.FailureImpl]
public readonly partial union GeneratedOuter(GeneratedInner);

[CompilerServices.Failure("\"{Filename}\" at C:\\temp\n{{size={Size,5:D4}}}")]
[CompilerServices.FailureImpl]
public readonly partial record struct FormattedFailure(string Filename, int Size);

[CompilerServices.Failure("Code {@Event}: {Size,4:D3}")]
public readonly record struct FormattedCase(string @Event, int Size);

[CompilerServices.FailureImpl]
public readonly partial union FormattedUnion(FormattedCase);

[CompilerServices.Failure("Missing {Name}", OverrideString = false)]
public record CustomDisplayCase(string Name)
{
    public override string ToString() => "manual display";
}

[CompilerServices.FailureImpl]
public readonly partial union CustomDisplayUnion(CustomDisplayCase);

[CompilerServices.Failure("Missing {Filename}", OverrideString = false)]
[CompilerServices.FailureImpl]
public partial record MessageOnly(string Filename);

[CompilerServices.Failure("Generated message")]
public partial class CustomFailure
{
    public override string ToString() => "custom leaf";
}

[CompilerServices.FailureImpl]
public readonly partial union CustomError(CustomFailure)
{
    public string Message => "custom message";
    public Polyester.Error.IError? Source => null;
    public override string ToString() => "custom union";
}

public partial class Container<T> where T : class
{
    [CompilerServices.Failure("{Name}: {Data}")]
    public readonly record struct NestedFailure<TData>(T Name, TData Data) where TData : struct;

    [CompilerServices.FailureImpl]
    public readonly partial union NestedError<TData>(NestedFailure<TData>) where TData : struct;
}

[CompilerServices.Failure("{@Event}")]
[CompilerServices.FailureImpl]
public readonly partial record struct KeywordFailure(string @Event);

public sealed record ExternalError(Polyester.Error.IError? Source) : Polyester.Error.IError
{
    public string Message => "external error";
    public override string ToString() => "external error";
}

[CompilerServices.FailureImpl]
public readonly partial union ExternalFailure(ExternalError);

[CompilerServices.FailureImpl]
public readonly partial union TestFailure(NotFound, IncompleteData, HyperError);

[CompilerServices.FailureImpl]
public readonly partial union HyperError(Disconnect);

[CompilerServices.Failure("Can not find file {Filename} with size {LimitSize}")]
public readonly record struct NotFound
{
    public readonly required string Filename { get; init; }
    public readonly required int LimitSize { get; init; }
}

[CompilerServices.Failure("Can not find file {Filename}")]
public readonly record struct IncompleteData
{
    public readonly required string Filename { get; init; }
}

[CompilerServices.Failure("Can not find file {Filename}")]
public readonly record struct Disconnect
{
    public readonly required string Filename { get; init; }
}

[CompilerServices.FailureImpl]
[CompilerServices.Failure("App crashed: {AppName}")]
public readonly partial record struct AppError
{
    public readonly string AppName { get; init; }
    public readonly TestFailure Source { get; init; }
}
