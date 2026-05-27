using System;
using System.Collections.Generic;
using System.Linq;

namespace Analyzer.Extension;

internal sealed record ModernizationRule(
    string Id,
    string Name,
    string Category,
    string Description,
    string MinimumCSharpVersion,
    string? BeforeExample = null,
    string? AfterExample = null);

internal static class ModernizationRuleCatalog
{
    public static IReadOnlyList<ModernizationRule> All { get; } =
    [
        new("MOD001", "Prefer var", "Typing", "Prefer `var` where the type is obvious from the right-hand side.", "3.0"),
        new("MOD002", "Null-conditional operators", "Null handling", "Use `?.` and `??` instead of explicit null checks.", "6.0"),
        new("MOD003", "String interpolation", "Strings", "Use string interpolation instead of `string.Format(...)`.", "6.0"),
        new(
            "MOD004",
            "Pattern matching",
            "Pattern matching",
            "Use pattern matching instead of `is` with cast or `as` with null check.",
            "7.0",
            """
            if (shape is Circle)
            {
                var circle = (Circle)shape;
                return circle.Radius * Math.PI;
            }
            """,
            """
            if (shape is Circle circle)
            {
                return circle.Radius * Math.PI;
            }
            """),
        new("MOD005", "Switch expressions", "Control flow", "Use switch expressions instead of long `if`/`else` chains on the same value.", "8.0"),
        new("MOD006", "Using declarations", "Resources", "Use `using` declarations instead of `using` statement blocks where appropriate.", "8.0"),
        new("MOD007", "Null-coalescing assignment", "Null handling", "Use `??=` for initialize-if-null patterns.", "8.0"),
        new(
            "MOD008",
            "File-scoped namespaces",
            "Structure",
            "Use file-scoped namespace declarations to reduce one level of nesting.",
            "10.0",
            """
            namespace MyApp.Services
            {
                public class UserService
                {
                    public void DoWork()
                    {
                    }
                }
            }
            """,
            """
            namespace MyApp.Services;

            public class UserService
            {
                public void DoWork()
                {
                }
            }
            """),
        new("MOD009", "Target-typed new", "Typing", "Use `new()` when the target type is already known from context.", "9.0"),
        new("MOD010", "Collection expressions", "Collections", "Use collection expressions (`[...]`) when targeting C# 12.", "12.0"),
        new("MOD011", "Raw string literals", "Strings", "Use raw string literals for multi-line or heavily escaped strings.", "11.0"),
        new("MOD012", "Primary constructors", "Constructors", "Use primary constructors for straightforward dependency capture.", "12.0"),
    ];

    public static bool TryGet(string ruleId, out ModernizationRule? rule)
    {
        rule = All.FirstOrDefault(candidate => string.Equals(candidate.Id, ruleId, StringComparison.OrdinalIgnoreCase));

        return rule is not null;
    }
}
