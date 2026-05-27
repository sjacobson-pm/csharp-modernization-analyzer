FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY CSharpModernizer.sln .
COPY src/Analyzer.Core/Analyzer.Core.csproj src/Analyzer.Core/
COPY src/Analyzer.Action/Analyzer.Action.csproj src/Analyzer.Action/
COPY src/Analyzer.Cli/Analyzer.Cli.csproj src/Analyzer.Cli/
COPY src/Analyzer.Mcp/Analyzer.Mcp.csproj src/Analyzer.Mcp/
COPY src/Analyzer.Extension/Analyzer.Extension.csproj src/Analyzer.Extension/
COPY tests/Analyzer.Core.Tests/Analyzer.Core.Tests.csproj tests/Analyzer.Core.Tests/

RUN dotnet restore CSharpModernizer.sln

COPY . .
RUN dotnet publish src/Analyzer.Action/Analyzer.Action.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .

ENTRYPOINT ["dotnet", "Analyzer.Action.dll"]
