namespace Failure.UnitTests;

public class GeneratorTests
{
    [Test]
    public void TestErrorFormat()
    {
        NotFound notFound = new() { Filename = "Program.cs", LimitSize = 20 };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(notFound.ToString(), Is.EqualTo("Can not find file Program.cs with size 20"));
            Assert.That(notFound.Message, Is.EqualTo(notFound.ToString()));
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
            Assert.That(leaf.Message, Is.EqualTo("generated message"));
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
}

[CompilerServices.FailureImpl]
public readonly partial union GeneratedInner(Disconnect);

[CompilerServices.FailureImpl]
public readonly partial union GeneratedOuter(GeneratedInner);

[CompilerServices.Failure("\"{Filename}\" at C:\\temp\n{{size={Size,5:D4}}}")]
public readonly partial record struct FormattedFailure(string Filename, int Size);

[CompilerServices.Failure("Missing {Filename}", OverrideString = false)]
public partial record MessageOnly(string Filename);

[CompilerServices.Failure("generated message")]
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
    public readonly partial record struct NestedFailure<TData>(T Name, TData Data) where TData : struct;

    [CompilerServices.FailureImpl]
    public readonly partial union NestedError<TData>(NestedFailure<TData>) where TData : struct;
}

[CompilerServices.Failure("{@Event}")]
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
public readonly partial union HyperError(Disconnect) : Polyester.Error.IError;

[CompilerServices.Failure("Can not find file {Filename} with size {LimitSize}")]
public readonly partial record struct NotFound
{
    public readonly required string Filename { get; init; }
    public readonly required int LimitSize { get; init; }
}

[CompilerServices.Failure("Can not find file {Filename}")]
public readonly partial record struct IncompleteData
{
    public readonly required string Filename { get; init; }
}

[CompilerServices.Failure("Can not find file {Filename}")]
public readonly partial record struct Disconnect
{
    public readonly required string Filename { get; init; }
}
