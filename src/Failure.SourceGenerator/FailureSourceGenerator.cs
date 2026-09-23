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

    private static readonly DiagnosticDescriptor InvalidSourceMember = new(
        "FAILURE007",
        "Failure source must be an error",
        "Member '{0}' on '{1}' must be a readable instance IError field or property",
        "Failure",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var formats = context.SyntaxProvider.ForAttributeWithMetadataName(
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
            formats,
            static (output, candidate) => ValidateFailureFormat(output, candidate)
        );
        context.RegisterSourceOutput(
            errors,
            static (output, candidate) => GenerateError(output, candidate)
        );
    }

    private static void ValidateFailureFormat(
        SourceProductionContext context,
        GeneratorAttributeSyntaxContext candidate
    )
    {
        var type = (INamedTypeSymbol)candidate.TargetSymbol;
        var declaration = (TypeDeclarationSyntax)candidate.TargetNode;
        string? format = candidate.Attributes[0].ConstructorArguments.FirstOrDefault().Value as string;
        CreateMessageExpression(
            context,
            type,
            type,
            declaration.Identifier.GetLocation(),
            format,
            candidate.SemanticModel.Compilation
        );
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

        var failureAttribute = type.GetAttributes().FirstOrDefault(static attribute =>
            attribute.AttributeClass?.ToDisplayString() == FailureAttributeName
        );
        bool transparent = candidate.Attributes[0].NamedArguments.Any(static argument =>
            argument.Key == "Transparent" && argument.Value.Value is true
        );
        if (transparent && failureAttribute is not null)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                InvalidTarget,
                declaration.Identifier.GetLocation(),
                "FailureImpl",
                type.Name,
                "Transparent cannot be combined with Failure because it forwards ToString()"
            ));
            return;
        }

        if (failureAttribute is not null && declaration is UnionDeclarationSyntax)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                InvalidTarget,
                declaration.Identifier.GetLocation(),
                "Failure",
                type.Name,
                "put Failure on union case types, not on the union"
            ));
            return;
        }

        var members = new StringBuilder();
        if (declaration is UnionDeclarationSyntax)
        {
            if (!transparent && !HasUserMember(type, "Message"))
            {
                string? messageExpression = CreateUnionMessageExpression(
                    context, type, (UnionDeclarationSyntax)declaration, candidate.SemanticModel
                );
                if (messageExpression is null)
                {
                    return;
                }

                members.Append("public string Message => ")
                    .Append(messageExpression)
                    .AppendLine(";");
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
            if (failureAttribute is not null)
            {
                string? format = failureAttribute.ConstructorArguments.FirstOrDefault().Value as string;
                string? expression = CreateMessageExpression(
                    context,
                    type,
                    type,
                    declaration.Identifier.GetLocation(),
                    format,
                    candidate.SemanticModel.Compilation,
                    reportDiagnostic: false
                );
                if (expression is null)
                {
                    return;
                }

                if (!HasUserMember(type, "Message"))
                {
                    members.Append("public string Message => ").Append(expression).AppendLine(";");
                }

                if (ShouldOverrideString(failureAttribute) && !HasToString(type))
                {
                    members.Append("public override string ToString() => ")
                        .Append(expression)
                        .AppendLine(";");
                }
            }

            bool hasExplicitSource = HasExplicitErrorMember(type, errorInterface, "Source");
            if (!TryFindSourceMember(context, type, declaration, errorInterface, transparent,
                    hasExplicitSource, out var sourceMember))
            {
                return;
            }

            if (transparent)
            {
                var inner = sourceMember;
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

                if (HasToString(type) || hasExplicitSource
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
                if (sourceMember is not null && !hasExplicitSource)
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

    private static bool ShouldOverrideString(AttributeData attribute) =>
        !attribute.NamedArguments.Any(static argument =>
            argument.Key == "OverrideString" && argument.Value.Value is false
        );

    private static string? CreateUnionMessageExpression(
        SourceProductionContext context,
        INamedTypeSymbol unionType,
        UnionDeclarationSyntax union,
        SemanticModel semanticModel
    )
    {
        if (union.ParameterList is null)
        {
            // Leave malformed union syntax to the compiler's diagnostics.
            return "Value?.ToString() ?? string.Empty";
        }

        var cases = new StringBuilder();
        foreach (var parameter in union.ParameterList.Parameters)
        {
            if (parameter.Type is null
                || semanticModel.GetTypeInfo(parameter.Type).Type is not INamedTypeSymbol caseType)
            {
                continue;
            }

            var attribute = caseType.GetAttributes().FirstOrDefault(static candidate =>
                candidate.AttributeClass?.ToDisplayString() == FailureAttributeName
            );
            if (attribute is null)
            {
                continue;
            }

            string? format = attribute.ConstructorArguments.FirstOrDefault().Value as string;
            // The diagnostic-only Failure path owns template errors. A union
            // reports only the additional error of an inaccessible case member.
            if (CreateMessageExpression(
                    context,
                    caseType,
                    caseType,
                    parameter.Type.GetLocation(),
                    format,
                    semanticModel.Compilation,
                    reportDiagnostic: false
                ) is null)
            {
                return null;
            }

            string? expression = CreateMessageExpression(
                context,
                caseType,
                unionType,
                parameter.Type.GetLocation(),
                format,
                semanticModel.Compilation,
                "@error"
            );
            if (expression is null)
            {
                return null;
            }

            cases.Append(caseType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                .Append(" @error => ")
                .Append(expression)
                .AppendLine(",");
        }

        if (cases.Length == 0)
        {
            return "Value?.ToString() ?? string.Empty";
        }

        return "Value switch\n{\n"
            + cases
            + "_ => Value?.ToString() ?? string.Empty,\n}";
    }

    private static string? CreateMessageExpression(
        SourceProductionContext context,
        INamedTypeSymbol type,
        INamedTypeSymbol accessWithin,
        Location location,
        string? format,
        Compilation compilation,
        string? receiver = null,
        bool reportDiagnostic = true
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
                    if (interpolation.Expression is not IdentifierNameSyntax identifier
                        || !HasReadableMember(type, accessWithin, identifier.Identifier.ValueText, compilation))
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
            if (reportDiagnostic)
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        InvalidFormat,
                        location,
                        type.Name,
                        error
                    )
                );
            }
            return null;
        }

        if (receiver is not null)
        {
            var parsedExpression = expression!;
            expression = parsedExpression.ReplaceNodes(
                parsedExpression.Contents.OfType<InterpolationSyntax>(),
                (original, _) => original.WithExpression(
                    SyntaxFactory.ParseExpression(
                        receiver + "." + ((IdentifierNameSyntax)original.Expression).Identifier.Text
                    )
                )
            );
        }

        return expression!.ToFullString();
    }

    private static bool HasReadableMember(
        INamedTypeSymbol type,
        INamedTypeSymbol accessWithin,
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
                && compilation.IsSymbolAccessibleWithin(member, accessWithin)
                && (
                    member is IFieldSymbol
                    || member is IPropertySymbol { IsIndexer: false, GetMethod: not null } property
                        && compilation.IsSymbolAccessibleWithin(property.GetMethod, accessWithin)
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

    private static bool TryFindSourceMember(
        SourceProductionContext context,
        INamedTypeSymbol type,
        TypeDeclarationSyntax declaration,
        INamedTypeSymbol errorInterface,
        bool transparent,
        bool hasExplicitSource,
        out ISymbol? sourceMember
    )
    {
        sourceMember = null;
        ISymbol? selected = transparent || !hasExplicitSource
            ? type.GetMembers("Source").FirstOrDefault(member =>
                member is IFieldSymbol or IPropertySymbol
            )
            : null;

        if (selected is not null)
        {
            if (!IsReadableErrorMember(selected, errorInterface))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    InvalidSourceMember,
                    selected.Locations.FirstOrDefault() ?? declaration.Identifier.GetLocation(),
                    selected.Name,
                    type.Name
                ));
                return false;
            }

            sourceMember = selected;
            return true;
        }

        if (transparent)
        {
            var candidates = type.GetMembers()
                .Where(member => IsReadableErrorMember(member, errorInterface))
                .ToArray();
            sourceMember = candidates.Length == 1 ? candidates[0] : null;
        }

        return true;
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
