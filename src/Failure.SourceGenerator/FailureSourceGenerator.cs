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

    private static readonly DiagnosticDescriptor InvalidTransparentMember = new(
        "FAILURE005",
        "Transparent failure needs one inner error",
        "Type '{0}' needs exactly one readable IError field or property for Transparent, or a member named Source to disambiguate",
        "Failure",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    private static readonly DiagnosticDescriptor InvalidTransparentCase = new(
        "FAILURE006",
        "Transparent union case must be an error",
        "Union case '{0}' must implement Polyester.Error.IError to use Transparent",
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
        )
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    InvalidTarget,
                    declaration.Identifier.GetLocation(),
                    "Failure",
                    type.Name,
                    "use a non-static class, record, or struct"
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

        bool transparent = type.GetAttributes().Any(static candidateAttribute =>
            candidateAttribute.AttributeClass?.ToDisplayString() == FailureImplAttributeName
            && candidateAttribute.NamedArguments.Any(static argument =>
                argument.Key == "Transparent" && argument.Value.Value is true
            )
        );

        if (transparent)
        {
            // GenerateError reports the incompatible attribute combination.
            return;
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

        if (type.IsStatic || type.IsRefLikeType)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    InvalidTarget,
                    declaration.Identifier.GetLocation(),
                    "FailureImpl",
                    type.Name,
                    "use a non-static, non-ref-like class or struct"
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

        bool transparent = candidate.Attributes[0].NamedArguments.Any(static argument =>
            argument.Key == "Transparent" && argument.Value.Value is true
        );
        if (transparent && type.GetAttributes().Any(static attribute =>
            attribute.AttributeClass?.ToDisplayString() == FailureAttributeName
        ))
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    InvalidTarget,
                    declaration.Identifier.GetLocation(),
                    "FailureImpl",
                    type.Name,
                    "Transparent cannot be combined with Failure because it forwards ToString()"
                )
            );
            return;
        }

        var members = new StringBuilder();
        if (declaration is UnionDeclarationSyntax)
        {
            if (!transparent && !HasUserMember(type, "Message"))
            {
                members.AppendLine("public string Message => Value?.ToString() ?? string.Empty;");
            }

            if (transparent)
            {
                var union = (UnionDeclarationSyntax)declaration;
                if (union.ParameterList is null)
                {
                    // Leave malformed union syntax to the compiler's diagnostics.
                    return;
                }

                foreach (var parameter in union.ParameterList.Parameters)
                {
                    var caseType = parameter.Type is null
                        ? null
                        : candidate.SemanticModel.GetTypeInfo(parameter.Type).Type;
                    if (caseType is null || !IsErrorType(caseType, errorInterface))
                    {
                        context.ReportDiagnostic(
                            Diagnostic.Create(
                                InvalidTransparentCase,
                                parameter.Type?.GetLocation() ?? parameter.GetLocation(),
                                parameter.Type?.ToString() ?? "<missing>"
                            )
                        );
                        return;
                    }
                }

                if (HasToString(type)
                    || HasExplicitErrorMember(type, errorInterface, "Source")
                    || HasExplicitErrorMember(type, errorInterface, "ToString"))
                {
                    ReportTransparentConflict(context, declaration, type);
                    return;
                }

                members.AppendLine("global::Polyester.Error.IError? global::Polyester.Error.IError.Source => (Value as global::Polyester.Error.IError)?.Source;");
                members.AppendLine("public override string ToString() => (Value as global::Polyester.Error.IError)?.ToString() ?? string.Empty;");
            }
            else
            {
                if (!HasUserMember(type, "Source") && !HasExplicitErrorMember(type, errorInterface, "Source"))
                {
                    // The runtime cast recognizes IError implementations generated
                    // for other union cases in this compilation.
                    members.AppendLine("public global::Polyester.Error.IError? Source => Value as global::Polyester.Error.IError;");
                }

                if (!HasToString(type))
                {
                    members.AppendLine("public override string ToString() => Message;");
                }
            }
        }
        else
        {
            if (transparent)
            {
                var inner = FindTransparentMember(type, errorInterface);
                if (inner is null)
                {
                    context.ReportDiagnostic(
                        Diagnostic.Create(
                            InvalidTransparentMember,
                            declaration.Identifier.GetLocation(),
                            type.Name
                        )
                    );
                    return;
                }

                if (HasToString(type) || HasExplicitErrorMember(type, errorInterface, "Source")
                    || HasExplicitErrorMember(type, errorInterface, "ToString"))
                {
                    ReportTransparentConflict(context, declaration, type);
                    return;
                }

                string innerValue = "((object?)this.@" + inner.Name + " as global::Polyester.Error.IError)";
                members.Append("global::Polyester.Error.IError? global::Polyester.Error.IError.Source => ")
                    .Append(innerValue)
                    .AppendLine("?.Source;");
                members.Append("public override string ToString() => ")
                    .Append(innerValue)
                    .AppendLine("?.ToString() ?? string.Empty;");
            }
            else
            {
                // A Source field or property is the immediate cause, including
                // a struct case that gains IError in this generator run.
                var sourceMember = FindSourceMember(type, errorInterface);
                if (sourceMember is not null && !HasExplicitErrorMember(type, errorInterface, "Source"))
                {
                    members.Append("global::Polyester.Error.IError? global::Polyester.Error.IError.Source => (object?)this.@")
                        .Append(sourceMember.Name)
                        .AppendLine(" as global::Polyester.Error.IError;");
                }

                if (!HasToString(type) && !HasExplicitErrorMember(type, errorInterface, "ToString"))
                {
                    // object.ToString() and ValueType.ToString() are nullable in
                    // their annotations. IError.ToString() promises non-null.
                    members.AppendLine(
                        "string global::Polyester.Error.IError.ToString() => ToString() ?? string.Empty;"
                    );
                }
            }
        }

        bool implementsError = type.AllInterfaces.Any(@interface =>
            SymbolEqualityComparer.Default.Equals(@interface, errorInterface)
        );
        if (members.Length > 0 || !implementsError)
        {
            AddSource(
                context,
                type,
                declaration,
                "Error",
                members.ToString(),
                implementsError ? null : "global::" + ErrorTypeName
            );
        }
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

    private static bool HasExplicitErrorMember(
        INamedTypeSymbol type,
        INamedTypeSymbol errorInterface,
        string name
    ) => type.GetMembers().Any(member =>
        (member as IPropertySymbol)?.ExplicitInterfaceImplementations.Any(implementation =>
            implementation.Name == name
            && SymbolEqualityComparer.Default.Equals(implementation.ContainingType, errorInterface)
        ) == true
        || (member as IMethodSymbol)?.ExplicitInterfaceImplementations.Any(implementation =>
            implementation.Name == name
            && SymbolEqualityComparer.Default.Equals(implementation.ContainingType, errorInterface)
        ) == true
    );

    private static void ReportTransparentConflict(
        SourceProductionContext context,
        TypeDeclarationSyntax declaration,
        INamedTypeSymbol type
    ) => context.ReportDiagnostic(
        Diagnostic.Create(
            InvalidTarget,
            declaration.Identifier.GetLocation(),
            "FailureImpl",
            type.Name,
            "Transparent must forward ToString() and IError.Source; remove custom implementations"
        )
    );

    private static ISymbol? FindSourceMember(
        INamedTypeSymbol type,
        INamedTypeSymbol errorInterface
    ) => type.GetMembers("Source").FirstOrDefault(member =>
        IsReadableErrorMember(member, errorInterface)
    );

    private static ISymbol? FindTransparentMember(
        INamedTypeSymbol type,
        INamedTypeSymbol errorInterface
    )
    {
        var candidates = type.GetMembers()
            .Where(member => IsReadableErrorMember(member, errorInterface))
            .ToArray();

        return candidates.Length == 1
            ? candidates[0]
            : candidates.SingleOrDefault(static member => member.Name == "Source");
    }

    private static bool IsReadableErrorMember(ISymbol member, INamedTypeSymbol errorInterface) =>
        !member.IsStatic
        && !(member is IFieldSymbol && member.IsImplicitlyDeclared)
        && (member is IFieldSymbol || member is IPropertySymbol { IsIndexer: false, GetMethod: not null })
        && GetMemberType(member) is { } memberType
        && IsErrorType(memberType, errorInterface);

    private static bool IsErrorType(ITypeSymbol type, INamedTypeSymbol errorInterface) =>
        SymbolEqualityComparer.Default.Equals(type, errorInterface)
        || type is INamedTypeSymbol namedType
            && (namedType.AllInterfaces.Any(@interface =>
                    SymbolEqualityComparer.Default.Equals(@interface, errorInterface)
                )
                || namedType.GetAttributes().Any(static attribute =>
                    attribute.AttributeClass?.ToDisplayString() == FailureImplAttributeName
                )
                || namedType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
                    && IsErrorType(namedType.TypeArguments[0], errorInterface))
        || type is ITypeParameterSymbol parameter
            && parameter.ConstraintTypes.Any(constraint => IsErrorType(constraint, errorInterface));

    private static ITypeSymbol? GetMemberType(ISymbol member) => member switch
    {
        IFieldSymbol field => field.Type,
        IPropertySymbol property => property.Type,
        _ => null,
    };

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
