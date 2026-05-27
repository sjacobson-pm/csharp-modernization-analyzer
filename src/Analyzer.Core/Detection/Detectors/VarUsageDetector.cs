using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Analyzer.Core.Detection.Detectors;

/// <summary>
/// MOD001: Detects explicit local variable types that can be replaced with var.
/// </summary>
public sealed class VarUsageDetector : IPatternDetector
{
    public string RuleId => "MOD001";
    public string RuleName => "var-usage";
    public Version MinimumLangVersion => new(3, 0);

    public Task<IReadOnlyList<DetectionResult>> DetectAsync(DetectionContext context, CancellationToken cancellationToken = default)
    {
        var results = new List<DetectionResult>();
        var prefersBuiltIn = DetectorUtilities.PrefersEnabled(context.Standards, "csharp_style_var_for_built_in_types");
        var prefersApparent = DetectorUtilities.PrefersEnabled(context.Standards, "csharp_style_var_when_type_is_apparent");
        var prefersElsewhere = DetectorUtilities.PrefersEnabled(context.Standards, "csharp_style_var_elsewhere");

        if (!prefersBuiltIn && !prefersApparent && !prefersElsewhere)
            return Task.FromResult<IReadOnlyList<DetectionResult>>(results);

        var root = context.SyntaxTree.GetRoot(cancellationToken);

        foreach (var localDeclaration in root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
        {
            if (localDeclaration.IsConst || localDeclaration.UsingKeyword != default)
                continue;

            var declaration = localDeclaration.Declaration;
            if (declaration.Type.IsVar || declaration.Variables.Count != 1)
                continue;

            var variable = declaration.Variables[0];
            if (variable.Initializer is null)
                continue;

            var initializer = DetectorUtilities.Unwrap(variable.Initializer.Value);
            if (DetectorUtilities.IsNullLiteral(initializer))
                continue;

            var declaredType = context.SemanticModel.GetTypeInfo(declaration.Type, cancellationToken).Type;
            var initializerType = DetectorUtilities.GetExpressionType(context.SemanticModel, initializer, cancellationToken);

            if (declaredType is null || initializerType is null)
                continue;

            if (!SymbolEqualityComparer.Default.Equals(declaredType, initializerType))
                continue;

            if (declaredType.TypeKind == TypeKind.Error || initializerType.TypeKind == TypeKind.Error)
                continue;

            if (!ShouldReport(declaration.Type, initializer, declaredType, prefersBuiltIn, prefersApparent, prefersElsewhere, context.SemanticModel, cancellationToken, out var description))
                continue;

            var suggestedCode = $"var {variable.Identifier.Text} = {variable.Initializer.Value};";
            results.Add(DetectorUtilities.CreateResult(context, this, localDeclaration, description, suggestedCode));
        }

        return Task.FromResult<IReadOnlyList<DetectionResult>>(results);
    }

    private static bool ShouldReport(
        TypeSyntax declaredTypeSyntax,
        ExpressionSyntax initializer,
        ITypeSymbol declaredType,
        bool prefersBuiltIn,
        bool prefersApparent,
        bool prefersElsewhere,
        SemanticModel semanticModel,
        CancellationToken cancellationToken,
        out string description)
    {
        if (IsBuiltInType(declaredTypeSyntax, declaredType))
        {
            description = "Use 'var' for built-in type declarations";
            return prefersBuiltIn;
        }

        if (IsTypeApparent(initializer, declaredType, semanticModel, cancellationToken))
        {
            description = "Use 'var' when the type is apparent from the initializer";
            return prefersApparent;
        }

        description = "Use 'var' to simplify explicit local declarations";
        return prefersElsewhere;
    }

    private static bool IsBuiltInType(TypeSyntax declaredTypeSyntax, ITypeSymbol declaredType)
    {
        if (declaredTypeSyntax is PredefinedTypeSyntax)
            return true;

        return declaredType.SpecialType is SpecialType.System_Boolean
            or SpecialType.System_Byte
            or SpecialType.System_Char
            or SpecialType.System_Decimal
            or SpecialType.System_Double
            or SpecialType.System_Int16
            or SpecialType.System_Int32
            or SpecialType.System_Int64
            or SpecialType.System_Object
            or SpecialType.System_SByte
            or SpecialType.System_Single
            or SpecialType.System_String
            or SpecialType.System_UInt16
            or SpecialType.System_UInt32
            or SpecialType.System_UInt64;
    }

    private static bool IsTypeApparent(
        ExpressionSyntax initializer,
        ITypeSymbol declaredType,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        initializer = DetectorUtilities.Unwrap(initializer);

        if (initializer is ObjectCreationExpressionSyntax or ArrayCreationExpressionSyntax or ImplicitArrayCreationExpressionSyntax)
            return true;

        if (initializer is CastExpressionSyntax castExpression)
        {
            var castType = semanticModel.GetTypeInfo(castExpression.Type, cancellationToken).Type;
            return SymbolEqualityComparer.Default.Equals(castType, declaredType);
        }

        return false;
    }
}
