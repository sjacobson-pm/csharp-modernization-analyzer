# C# Modernization Analyzer

Hybrid Roslyn + AI analyzer that identifies outdated C# language constructs and suggests modern equivalents. Works at PR time (GitHub Action), development time (CLI tool), and conversationally (MCP Server / Copilot Extension).

## Features

- **12 modernization rules** covering typing, null handling, pattern matching, string interpolation, switch expressions, namespaces, and more
- **LangVersion-aware** — only suggests patterns your project can actually use
- **Standards-first** — reads `.editorconfig`, `stylecop.json`, and external standards before producing suggestions
- **AI-enhanced** — optional OpenAI integration for explanations and refined diffs
- **Human review required** — never auto-applies changes

## Delivery Surfaces

| Surface | Use Case | Status |
|---------|----------|--------|
| CLI (`pm-modernize`) | Local development scanning | 🚧 In Progress |
| GitHub Action | PR-time automated review | 🚧 In Progress |
| MCP Server | AI agent tool integration | 📋 Planned |
| Copilot Extension | `@modernize` in Copilot Chat | 📋 Planned |

## Quick Start

### CLI Tool

```bash
# Install
dotnet tool install -g CSharpModernizer.Cli

# Scan current directory
pm-modernize scan .

# Scan only uncommitted changes
pm-modernize scan --diff

# Full repo scan with JSON output
pm-modernize scan --full --output json

# Generate config template
pm-modernize config init
```

### GitHub Action

Add to your repository's `.github/workflows/modernization.yml`:

```yaml
name: C# Modernization Review

on:
  pull_request:
    branches: [develop, main]

jobs:
  modernize:
    runs-on: ubuntu-latest
    permissions:
      contents: read
      pull-requests: write

    steps:
      - uses: actions/checkout@v4
        with:
          fetch-depth: 0

      - uses: sjacobson-pm/csharp-modernization-analyzer@v1
        with:
          scope: changed-files
          github-token: ${{ secrets.GITHUB_TOKEN }}
          openai-api-key: ${{ secrets.OPENAI_API_KEY }}
```

### MCP Server (for Copilot/AI agents)

Add to VS Code `settings.json`:

```json
{
  "mcp": {
    "servers": {
      "csharp-modernize": {
        "command": "pm-modernize",
        "args": ["mcp-serve"]
      }
    }
  }
}
```

## Configuration

Create a `.modernization.yml` in your repository root:

```yaml
version: 1

scan:
  scope: changed-files
  include:
    - "src/**/*.cs"
  exclude:
    - "**/Migrations/**"
    - "**/Generated/**"

standards:
  external:
    - url: "https://your-org.github.io/coding-standards/"
      cache-ttl: 24h

rules:
  MOD008:
    enabled: true
    severity: suggestion
  MOD012:
    enabled: false

ai:
  enabled: true
  provider: openai
  model: gpt-4o
  explain: true
```

## Modernization Rules

| Rule | Category | Description | Min C# |
|------|----------|-------------|--------|
| MOD001 | Typing | Prefer `var` where type is apparent | 3.0 |
| MOD002 | Null handling | Use `?.` and `??` operators | 6.0 |
| MOD003 | Strings | Use string interpolation | 6.0 |
| MOD004 | Pattern matching | Use `is Type name` patterns | 7.0 |
| MOD005 | Control flow | Use switch expressions | 8.0 |
| MOD006 | Resources | Use `using` declarations | 8.0 |
| MOD007 | Null handling | Use `??=` assignment | 8.0 |
| MOD008 | Structure | Use file-scoped namespaces | 10.0 |
| MOD009 | Typing | Use target-typed `new()` | 9.0 |
| MOD010 | Collections | Use collection expressions `[...]` | 12.0 |
| MOD011 | Strings | Use raw string literals | 11.0 |
| MOD012 | Constructors | Use primary constructors | 12.0 |

## Architecture

```
Analyzer.Core         Shared detection engine (Roslyn + standards + patching)
    ↑
    ├── Analyzer.Cli       .NET global tool
    ├── Analyzer.Action    GitHub Action entry point
    ├── Analyzer.Mcp       MCP Server (stdio + HTTP/SSE)
    └── Analyzer.Extension Copilot Extension (GitHub App)
```

## Standards Resolution Order

1. `.editorconfig` (repo-local) — style preferences
2. `stylecop.json` (repo-local) — naming/documentation rules
3. `*.ruleset` / `.globalconfig` (repo-local) — analyzer severities
4. `.modernization.yml` (repo-local) — tool-specific overrides
5. External URLs (configured) — org-wide standards pages

**Conflict resolution:** Repo-local configuration always takes precedence. If a suggestion conflicts with `.editorconfig` preferences, it is silently suppressed.

## Development

```bash
# Build
dotnet build CSharpModernizer.sln

# Test
dotnet test CSharpModernizer.sln

# Run CLI locally
dotnet run --project src/Analyzer.Cli -- scan .
```

## License

MIT
