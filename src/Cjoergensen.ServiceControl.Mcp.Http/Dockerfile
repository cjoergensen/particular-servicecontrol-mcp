# Build context is the repository root (needed for Directory.Build.props / Directory.Packages.props / NuGet.Config):
#   docker buildx build -f src/Cjoergensen.ServiceControl.Mcp.Http/Dockerfile --platform linux/amd64,linux/arm64 .

ARG DOTNET_VERSION=10.0

# --platform=$BUILDPLATFORM keeps the SDK stage on the builder's native architecture (never emulated), while
# `dotnet publish -a $TARGETARCH` below cross-compiles the app for the target architecture.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION}-noble AS build
ARG TARGETARCH
WORKDIR /src

# Restore first, from just the project files, so dependency layers are cached across source-only changes.
COPY Directory.Build.props Directory.Packages.props nuget.config ./
COPY src/Cjoergensen.ServiceControl.Mcp.Core/*.csproj src/Cjoergensen.ServiceControl.Mcp.Core/
COPY src/Cjoergensen.ServiceControl.Mcp.Http/*.csproj src/Cjoergensen.ServiceControl.Mcp.Http/
RUN dotnet restore src/Cjoergensen.ServiceControl.Mcp.Http -a $TARGETARCH

COPY src/Cjoergensen.ServiceControl.Mcp.Core/ src/Cjoergensen.ServiceControl.Mcp.Core/
COPY src/Cjoergensen.ServiceControl.Mcp.Http/ src/Cjoergensen.ServiceControl.Mcp.Http/
RUN dotnet publish src/Cjoergensen.ServiceControl.Mcp.Http -c Release -a $TARGETARCH --no-restore -o /app/publish

# Chiseled: no shell, no package manager, no root user — the smallest ASP.NET runtime with the least attack
# surface Microsoft ships. The non-"extra" variant has no ICU, which matches InvariantGlobalization=true above.
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-noble-chiseled AS final
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "Cjoergensen.ServiceControl.Mcp.Http.dll"]
