using System.Collections.Generic;
using Analyzer.Core.Configuration;

namespace Analyzer.Core.Tests;

public class ModernizationConfigTests
{
    [Fact]
    public void LoadFromFile_Returns_Defaults_When_File_Missing()
    {
        var config = ModernizationConfig.LoadFromFile("nonexistent.yml");

        Assert.Equal("changed-files", config.Scope);
        Assert.True(config.Ai.Enabled);
        Assert.NotEmpty(config.IncludePatterns);
    }

    [Fact]
    public void LoadFromYaml_Parses_Valid_Config()
    {
        const string Yaml = """
                            version: 1

                            scan:
                              scope: full-repo
                              include:
                                - "src/**/*.cs"
                              exclude:
                                - "**/Migrations/**"

                            standards:
                              external:
                                - url: "https://example.com/standards"

                            rules:
                              MOD008:
                                enabled: true
                                severity: warning
                              MOD012:
                                enabled: false

                            ai:
                              enabled: false
                              provider: azure-openai
                              model: gpt-4
                              explain: false
                            """;

        var config = ModernizationConfig.LoadFromYaml(Yaml);

        Assert.Equal("full-repo", config.Scope);
        Assert.Contains("src/**/*.cs", config.IncludePatterns);
        Assert.Contains("**/Migrations/**", config.ExcludePatterns);
        Assert.Contains("https://example.com/standards", config.ExternalStandardUrls);
        Assert.True(config.IsRuleEnabled("MOD008"));
        Assert.False(config.IsRuleEnabled("MOD012"));
        Assert.False(config.Ai.Enabled);
        Assert.Equal("azure-openai", config.Ai.Provider);
    }

    [Fact]
    public void IsExcluded_Matches_Migration_Files()
    {
        var config = new ModernizationConfig { ExcludePatterns = ["**/Migrations/**"] };

        Assert.True(config.IsExcluded("src/Data/Migrations/20240101_Initial.cs"));
        Assert.False(config.IsExcluded("src/Services/UserService.cs"));
    }

    [Fact]
    public void IsRuleEnabled_Defaults_To_True()
    {
        var config = new ModernizationConfig();

        Assert.True(config.IsRuleEnabled("MOD001"));
        Assert.True(config.IsRuleEnabled("MOD999"));
    }

    [Fact]
    public void IsRuleEnabled_Respects_Override()
    {
        var config = new ModernizationConfig { RuleOverrides = new Dictionary<string, RuleOverride> { ["MOD012"] = new() { Enabled = false } }, };

        Assert.True(config.IsRuleEnabled("MOD001"));
        Assert.False(config.IsRuleEnabled("MOD012"));
    }

    [Fact]
    public void GetRuleSeverity_Returns_Null_For_Default()
    {
        var config = new ModernizationConfig();

        Assert.Null(config.GetRuleSeverity("MOD001"));
    }

    [Fact]
    public void GetRuleSeverity_Returns_Override()
    {
        var config = new ModernizationConfig
        {
            RuleOverrides = new Dictionary<string, RuleOverride> { ["MOD008"] = new() { Enabled = true, SeverityOverride = "Warning" }, },
        };

        Assert.Equal(Detection.Severity.Warning, config.GetRuleSeverity("MOD008"));
    }
}
