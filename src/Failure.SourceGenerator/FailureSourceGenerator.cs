using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Failure.SourceGenerator;

[Generator(LanguageNames.CSharp)]
public sealed class FailureSourceGenerator : IIncrementalGenerator
{
    private const string FailureAttributeName = "Failure.CompilerServices.FailureAttribute";
    private const string FailureImplAttributeName = "Failure.CompilerServices.FailureImplAttribute";
    private const string ErrorTypeName = "Polyester.Error.IError";

    private static readonly DiagnosticDescriptor PartialTypeRequired = new(
        "FAILURE001",
        "A partial type is required",
        "Type '{0}' and its containing types must be partial and cannot be file-local",
        "Failure",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    private static readonly DiagnosticDescriptor InvalidTarget = new(
        "FAILURE002",
        "Unsupported failure type",
        "Attribute '{0}' is not supported on '{1}': {2}",
        "Failure",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    private static readonly DiagnosticDescriptor InvalidFormat = new(
        "FAILURE003",
        "Invalid failure message format",
        "Invalid message format for '{0}': {1}",
        "Failure",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    private static readonly DiagnosticDescriptor MissingErrorInterface = new(
        "FAILURE004",
        "Polyester error interface is missing",
        "Type '{0}' requires a reference to '{1}'",
        "Failure",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var messages = context.SyntaxProvider.ForAttributeWithMetadataName(
            FailureAttributeName,
            static (node, _) => node is TypeDeclarationSyntax,
            static (attributeContext, _) => attributeContext
        );

        var errors = context.SyntaxProvider.ForAttributeWithMetadataName(
            FailureImplAttributeName,
            static (node, _) => node is TypeDeclarationSyntax,
            static (attributeContext, _) => attributeContext
        );

        context.RegisterSourceOutput(
            messages,
            static (output, candidate) => GenerateMessage(output, candidate)
        );
        context.RegisterSourceOutput(
            errors,
            static (output, candidate) => GenerateError(output, candidate)
        );
    }

    private static void GenerateMessage(
        SourceProductionContext context,
        GeneratorAttributeSyntaxContext candidate
    )
    {
        var type = (INamedTypeSymbol)candidate.TargetSymbol;
        var declaration = (TypeDeclarationSyntax)candidate.TargetNode;
        if (!ValidatePartialType(context, type, declaration))
        {
            return;
        }

        if (
            type.IsStatic
            || type.IsRefLikeType
            || declaration is UnionDeclarationSyntax
            || type.GetAttributes()
                .Any(static attribute =>
                    attribute.AttributeClass?.ToDisplayString() == FailureImplAttributeName
                )
        )
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    InvalidTarget,
                    declaration.Identifier.GetLocation(),
                    "Failure",
                    type.Name,
                    "use a non-static class, record, or struct without FailureImpl"
                )
            );
            return;
        }

        var attribute = candidate.Attributes[0];
        string? format = attribute.ConstructorArguments.FirstOrDefault().Value as string;
        var expression = CreateMessageExpression(
            context,
            type,
            declaration,
            format,
            candidate.SemanticModel.Compilation
        );
        if (expression is null)
        {
            return;
        }

        bool overrideString = !attribute.NamedArguments.Any(static argument =>
            argument.Key == "OverrideString" && argument.Value.Value is false
        );

        var members = new StringBuilder();
        if (!HasUserMember(type, "Message"))
        {
            members.Append("public string Message => ").Append(expression).AppendLine(";");
        }

        if (overrideString && !HasToString(type))
        {
            members
                .Append("public override string ToString() => ")
                .Append(expression)
                .AppendLine(";");
        }

        AddSource(context, type, declaration, "Message", members.ToString());
    }

    private static void GenerateError(
        SourceProductionContext context,
        GeneratorAttributeSyntaxContext candidate
    )
    {
        var type = (INamedTypeSymbol)candidate.TargetSymbol;
        var declaration = (TypeDeclarationSyntax)candidate.TargetNode;
        if (!ValidatePartialType(context, type, declaration))
        {
            return;
        }

        if (declaration is not UnionDeclarationSyntax)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    InvalidTarget,
                    declaration.Identifier.GetLocation(),
                    "FailureImpl",
                    type.Name,
                    "use a union declaration"
                )
            );
            return;
        }

        var errorInterface = candidate.SemanticModel.Compilation.GetTypeByMetadataName(
            ErrorTypeName
        );
        if (errorInterface is null)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    MissingErrorInterface,
                    declaration.Identifier.GetLocation(),
                    type.Name,
                    ErrorTypeName
                )
            );
            return;
        }

        var members = new StringBuilder();
        if (!HasUserMember(type, "Message"))
        {
            members.AppendLine("public string Message => Value?.ToString() ?? string.Empty;");
        }

        if (
            !HasUserMember(type, "Source")
            && !type.GetMembers()
                .OfType<IPropertySymbol>()
                .Any(property =>
                    property.ExplicitInterfaceImplementations.Any(implementation =>
                        implementation.Name == "Source"
                        && SymbolEqualityComparer.Default.Equals(
                            implementation.ContainingType,
                            errorInterface
                        )
                    )
                )
        )
        {
            // This cast also recognizes IError implementations added to other unions in
            // this generator run, which are not yet visible in the input compilation.
            members.AppendLine(
                "public global::Polyester.Error.IError? Source => Value as global::Polyester.Error.IError;"
            );
        }

        if (!HasToString(type))
        {
            members.AppendLine("public override string ToString() => Message;");
        }

        bool implementsError = type.AllInterfaces.Any(@interface =>
            SymbolEqualityComparer.Default.Equals(@interface, errorInterface)
        );
        AddSource(
            context,
            type,
            declaration,
            "Error",
            members.ToString(),
            implementsError ? null : "global::" + ErrorTypeName
        );
    }

    private static bool ValidatePartialType(
        SourceProductionContext context,
        INamedTypeSymbol type,
        TypeDeclarationSyntax declaration
    )
    {
        for (
            INamedTypeSymbol? current = type;
            current is not null;
            current = current.ContainingType
        )
        {
            foreach (var reference in current.DeclaringSyntaxReferences)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                if (
                    reference.GetSyntax(context.CancellationToken)
                        is not TypeDeclarationSyntax syntax
                    || !syntax.Modifiers.Any(SyntaxKind.PartialKeyword)
                    || syntax.Modifiers.Any(SyntaxKind.FileKeyword)
                )
                {
                    context.ReportDiagnostic(
                        Diagnostic.Create(
                            PartialTypeRequired,
                            declaration.Identifier.GetLocation(),
                            current.Name
                        )
                    );
                    return false;
                }
            }
        }

        return true;
    }

    private static string? CreateMessageExpression(
        SourceProductionContext context,
        INamedTypeSymbol type,
        TypeDeclarationSyntax declaration,
        string? format,
        Compilation compilation
    )
    {
        string? error = null;
        InterpolatedStringExpressionSyntax? expression = null;
        if (format is null)
        {
            error = "the format cannot be null";
        }
        else
        {
            expression =
                SyntaxFactory.ParseExpression(
                    "$" + SymbolDisplay.FormatLiteral(format, quote: true)
                ) as InterpolatedStringExpressionSyntax;
            if (expression is null || expression.ContainsDiagnostics)
            {
                error =
                    "use member placeholders such as {Filename}, {Size,8}, or {Size:D4}, and {{ or }} for literal braces";
            }
            else
            {
                foreach (var interpolation in expression.Contents.OfType<InterpolationSyntax>())
                {
                    if (
                        interpolation.Expression is not IdentifierNameSyntax identifier
                        || !HasReadableMember(type, identifier.Identifier.ValueText, compilation)
                    )
                    {
                        error =
                            $"'{interpolation.Expression}' must name a readable instance field or property";
                        break;
                    }

                    if (
                        interpolation.AlignmentClause is { } alignment
                        && !(
                            alignment.Value is LiteralExpressionSyntax literal
                            && literal.IsKind(SyntaxKind.NumericLiteralExpression)
                            && literal.Token.Value is int
                        )
                        && !(
                            alignment.Value is PrefixUnaryExpressionSyntax unary
                            && unary.IsKind(SyntaxKind.UnaryMinusExpression)
                            && unary.Operand is LiteralExpressionSyntax operand
                            && operand.Token.Value is int
                        )
                    )
                    {
                        error = "alignment must be an integer constant";
                        break;
                    }
                }
            }
        }

        if (error is not null)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    InvalidFormat,
                    declaration.Identifier.GetLocation(),
                    type.Name,
                    error
                )
            );
            return null;
        }

        return expression!.ToFullString();
    }

    private static bool HasReadableMember(
        INamedTypeSymbol type,
        string name,
        Compilation compilation
    )
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            var members = current.GetMembers(name);
            if (members.Length == 0)
            {
                continue;
            }

            return members.Any(member =>
                !member.IsStatic
                && compilation.IsSymbolAccessibleWithin(member, type)
                && (
                    member is IFieldSymbol
                    || member is IPropertySymbol { IsIndexer: false, GetMethod: not null } property
                        && compilation.IsSymbolAccessibleWithin(property.GetMethod, type)
                )
            );
        }

        return false;
    }

    private static bool HasUserMember(INamedTypeSymbol type, string name) =>
        type.GetMembers(name).Any(static member => !member.IsImplicitlyDeclared);

    private static bool HasToString(INamedTypeSymbol type) =>
        type.GetMembers("ToString")
            .OfType<IMethodSymbol>()
            .Any(static method =>
                !method.IsImplicitlyDeclared && method.Parameters.Length == 0 && method.Arity == 0
            );

    private static void AddSource(
        SourceProductionContext context,
        INamedTypeSymbol type,
        TypeDeclarationSyntax declaration,
        string suffix,
        string members,
        string? baseType = null
    )
    {
        var source = new StringBuilder("// <auto-generated/>\n#nullable enable\n");
        int scopeCount = 0;
        if (!type.ContainingNamespace.IsGlobalNamespace)
        {
            source
                .Append("namespace ")
                .Append(type.ContainingNamespace.ToDisplayString())
                .AppendLine("\n{");
            scopeCount++;
        }

        foreach (var container in declaration.Ancestors().OfType<TypeDeclarationSyntax>().Reverse())
        {
            AppendDeclaration(source, container, null);
            scopeCount++;
        }

        AppendDeclaration(source, declaration, baseType);
        source.AppendLine(members).AppendLine("}");
        for (int index = 0; index < scopeCount; index++)
        {
            source.AppendLine("}");
        }

        // Hash the full identity for unique hints that stay short for nested generics.
        string identity = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        using var hash = SHA256.Create();
        string hintName =
            type.Name
            + "."
            + string.Concat(
                hash.ComputeHash(Encoding.UTF8.GetBytes(identity))
                    .Select(static value => value.ToString("X2"))
            );
        context.AddSource(
            hintName + "." + suffix + ".g.cs",
            SourceText.From(source.ToString(), Encoding.UTF8)
        );
    }

    private static void AppendDeclaration(
        StringBuilder source,
        TypeDeclarationSyntax declaration,
        string? baseType
    )
    {
        foreach (var modifier in declaration.Modifiers)
        {
            source.Append(modifier.Text).Append(' ');
        }

        if (declaration is RecordDeclarationSyntax record)
        {
            source.Append("record ");
            if (!record.ClassOrStructKeyword.IsKind(SyntaxKind.None))
            {
                source.Append(record.ClassOrStructKeyword.Text).Append(' ');
            }
        }
        else
        {
            source.Append(declaration.Keyword.Text).Append(' ');
        }

        source.Append(declaration.Identifier.Text);
        if (declaration.TypeParameterList is { } parameters)
        {
            // Attributes and constraints belong to the original declaration; copying
            // them could duplicate attributes or depend on that file's using directives.
            source
                .Append('<')
                .Append(
                    string.Join(
                        ", ",
                        parameters.Parameters.Select(static parameter =>
                            (
                                parameter.VarianceKeyword.IsKind(SyntaxKind.None)
                                    ? ""
                                    : parameter.VarianceKeyword.Text + " "
                            ) + parameter.Identifier.Text
                        )
                    )
                )
                .Append('>');
        }
        if (baseType is not null)
        {
            source.Append(" : ").Append(baseType);
        }

        source.AppendLine("\n{");
    }
}
