# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy only the project files needed for restore first (improves cache)
# This Dockerfile is intended to be used with the build context set to the repository's `src` folder
# so referenced projects (AspireAsbEmulatorUi.Hosting) are available during restore.
COPY ["AspireAsbEmulatorUi.App/AspireAsbEmulatorUi.App.csproj", "AspireAsbEmulatorUi.App/"]
COPY ["AspireAsbEmulatorUi.Hosting/AspireAsbEmulatorUi.Hosting.csproj", "AspireAsbEmulatorUi.Hosting/"]

# Restore dependencies for the app project (will resolve project references)
RUN dotnet restore "AspireAsbEmulatorUi.App/AspireAsbEmulatorUi.App.csproj"

# Copy all source files from the context (the `src` folder)
COPY . .

# Build the application
RUN dotnet build "AspireAsbEmulatorUi.App/AspireAsbEmulatorUi.App.csproj" -c Release -o /app/build

# Publish stage
FROM build AS publish
RUN dotnet publish "AspireAsbEmulatorUi.App/AspireAsbEmulatorUi.App.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
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
