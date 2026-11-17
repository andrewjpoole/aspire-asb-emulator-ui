# Build stage
FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src

# Set NuGet package folder to a Linux path to avoid Windows fallback-folder issues
ENV NUGET_PACKAGES=/root/.nuget/packages

# Copy only the project files needed for restore first (improves cache)
# This Dockerfile is intended to be used with the build context set to the repository's `src` folder
# so referenced projects (AspireAsbEmulatorUi.Hosting) are available during restore.
COPY ["AspireAsbEmulatorUi.App/AspireAsbEmulatorUi.App.csproj", "AspireAsbEmulatorUi.App/"]
COPY ["AspireAsbEmulatorUi.Hosting/AspireAsbEmulatorUi.Hosting.csproj", "AspireAsbEmulatorUi.Hosting/"]

# Restore dependencies for the app project (will resolve project references)
RUN dotnet restore "AspireAsbEmulatorUi.App/AspireAsbEmulatorUi.App.csproj"

# Copy all source files from the context (the `src` folder)
COPY . .

# Publish in one step (let publish handle restore to avoid restore/build mismatch inside container)
FROM build AS publish
# Publish for the target Linux RID so only Linux native assets are included. Without a RID,
# transitive packages (e.g. Hex1b via Aspire.Hosting) drop Windows/macOS natives such as
# conpty.dll and OpenConsole.exe into runtimes/, which are useless here and get flagged by scanners.
# TARGETARCH is supplied by BuildKit (amd64 / arm64).
ARG TARGETARCH
RUN RID="linux-$( [ "$TARGETARCH" = "arm64" ] && echo arm64 || echo x64 )" && \
    dotnet publish "AspireAsbEmulatorUi.App/AspireAsbEmulatorUi.App.csproj" -c Release -r "$RID" --self-contained false -o /app/publish /p:UseAppHost=false

# Runtime stage
# Chiseled image: no shell, package manager, perl, etc. -> far smaller CVE surface.
# "-extra" keeps ICU + tzdata so globalization behaviour matches the full image.
FROM mcr.microsoft.com/dotnet/aspnet:11.0-resolute-chiseled-extra AS final
WORKDIR /app

# Expose port 8080 (standard port for non-root containers)
EXPOSE 8080

# Copy published application
COPY --from=publish /app/publish .

# Set environment variable for ASP.NET Core to listen on port 8080
ENV ASPNETCORE_URLS=http://+:8080

# Run as non-root user for security
USER app

ENTRYPOINT ["dotnet", "AspireAsbEmulatorUi.App.dll"]
