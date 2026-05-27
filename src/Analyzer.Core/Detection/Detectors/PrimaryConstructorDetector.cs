using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Analyzer.Core.Detection.Detectors;

/// <summary>
/// MOD012: Detects simple DI-style constructors that can become primary constructors.
/// </summary>
public sealed class PrimaryConstructorDetector : IPatternDetector
{
    public string RuleId => "MOD012";
    public string RuleName => "primary-constructor";
    public Version MinimumLangVersion => new(12, 0);

    public Task<IReadOnlyList<DetectionResult>> DetectAsync(DetectionContext context, CancellationToken cancellationToken = default)
    {
        var results = new List<DetectionResult>();
        var root = context.SyntaxTree.GetRoot(cancellationToken);

        foreach (var classDeclaration in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            var constructors = classDeclaration.Members.OfType<ConstructorDeclarationSyntax>().ToList();
            if (constructors.Count != 1)
                continue;

            var constructor = constructors[0];
            if (constructor.ParameterList.Parameters.Count == 0
                || constructor.Body is null
                || constructor.ExpressionBody is not null
                || constructor.Initializer is not null)
            {
                continue;
            }

            if (!TryMatchAssignments(classDeclaration, constructor, context, cancellationToken, out var fieldAssignments))
                continue;

            var classHeader = BuildClassHeader(classDeclaration, constructor.ParameterList);
            var fieldLines = fieldAssignments.Select(static assignment => $"    {assignment.FieldText} = {assignment.ParameterName};");
            var suggestedCode = $"{classHeader}{Environment.NewLine}{{{Environment.NewLine}{string.Join(Environment.NewLine, fieldLines)}{Environment.NewLine}}}";

            results.Add(DetectorUtilities.CreateResult(
                context,
                this,
                constructor,
                "Use a primary constructor for simple dependency-assignment constructors",
                suggestedCode));
        }

        return Task.FromResult<IReadOnlyList<DetectionResult>>(results);
    }

    private static bool TryMatchAssignments(
        ClassDeclarationSyntax classDeclaration,
        ConstructorDeclarationSyntax constructor,
        DetectionContext context,
        CancellationToken cancellationToken,
        out IReadOnlyList<FieldAssignment> fieldAssignments)
    {
        var assignments = new List<FieldAssignment>();
        fieldAssignments = assignments;

        if (constructor.Body is null || constructor.Body.Statements.Count != constructor.ParameterList.Parameters.Count)
            return false;

        var assignedFields = new HashSet<IFieldSymbol>(SymbolEqualityComparer.Default);
        var assignedParameters = new HashSet<IParameterSymbol>(SymbolEqualityComparer.Default);

        foreach (var statement in constructor.Body.Statements)
        {
            if (!DetectorUtilities.TryGetSimpleAssignment(statement, out var assignment))
                return false;

            var fieldSymbol = context.SemanticModel.GetSymbolInfo(assignment.Left, cancellationToken).Symbol as IFieldSymbol;
            var parameterSymbol = context.SemanticModel.GetSymbolInfo(assignment.Right, cancellationToken).Symbol as IParameterSymbol;

            if (fieldSymbol is null || parameterSymbol is null)
                return false;

            if (!SymbolEqualityComparer.Default.Equals(fieldSymbol.ContainingType, context.SemanticModel.GetDeclaredSymbol(classDeclaration, cancellationToken)))
                return false;

            if (!fieldSymbol.IsReadOnly)
                return false;

            if (!SymbolEqualityComparer.Default.Equals(fieldSymbol.Type, parameterSymbol.Type))
                return false;

            if (!assignedFields.Add(fieldSymbol) || !assignedParameters.Add(parameterSymbol))
                return false;

            if (fieldSymbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken) is not VariableDeclaratorSyntax declarator
                || declarator.Parent is not VariableDeclarationSyntax { Variables.Count: 1 } variableDeclaration
                || variableDeclaration.Parent is not FieldDeclarationSyntax fieldDeclaration)
            {
                return false;
            }

            var modifiers = string.Join(" ", fieldDeclaration.Modifiers.Select(static token => token.Text));
            var modifierPrefix = string.IsNullOrWhiteSpace(modifiers) ? string.Empty : modifiers + " ";
            var fieldText = $"{modifierPrefix}{variableDeclaration.Type} {declarator.Identifier.Text}";

            assignments.Add(new FieldAssignment(fieldText, parameterSymbol.Name));
        }

        return assignedParameters.Count == constructor.ParameterList.Parameters.Count;
    }

    private static string BuildClassHeader(ClassDeclarationSyntax classDeclaration, ParameterListSyntax parameterList)
    {
        var modifiers = string.Join(" ", classDeclaration.Modifiers.Select(static token => token.Text));
        var modifierPrefix = string.IsNullOrWhiteSpace(modifiers) ? string.Empty : modifiers + " ";
        var typeParameters = classDeclaration.TypeParameterList?.ToString() ?? string.Empty;
        var baseList = classDeclaration.BaseList is null ? string.Empty : $" {classDeclaration.BaseList}";
        return $"{modifierPrefix}class {classDeclaration.Identifier}{typeParameters}{parameterList}{baseList}";
    }

    private sealed record FieldAssignment(string FieldText, string ParameterName);
}
