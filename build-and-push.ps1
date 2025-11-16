# Docker Build and Push Script for ASB Emulator UI
# Usage: .\build-and-push.ps1 -DockerHubUsername "yourusername" [-Tag "latest"] [-AdditionalTags @("v1.0", "stable")]

param(
    [Parameter(Mandatory=$true)]
    [string]$DockerHubUsername,
    
    [Parameter(Mandatory=$false)]
    [string]$Tag = "latest",
    
    [Parameter(Mandatory=$false)]
    [string[]]$AdditionalTags = @(),
    
    [Parameter(Mandatory=$false)]
    [switch]$SkipBuild = $false,
    
    [Parameter(Mandatory=$false)]
    [switch]$SkipPush = $false
)

$ErrorActionPreference = "Stop"

$ImageName = "aspireasbemulatorui"
$FullImageName = "$DockerHubUsername/$ImageName"

# Navigate to the project directory
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectDir = Join-Path $ScriptDir "src\AspireAsbEmulatorUi.App"

if (-not (Test-Path $ProjectDir)) {
    Write-Error "Project directory not found: $ProjectDir"
    exit 1
}

Write-Host "=====================================" -ForegroundColor Cyan
Write-Host "ASB Emulator UI - Docker Build & Push" -ForegroundColor Cyan
Write-Host "=====================================" -ForegroundColor Cyan
Write-Host ""

# Build the Docker image
if (-not $SkipBuild) {
    Write-Host "Building Docker image: $FullImageName`:$Tag" -ForegroundColor Green
    Write-Host "Project directory: $ProjectDir" -ForegroundColor Yellow
    Write-Host ""
    
    Push-Location $ProjectDir
    try {
        docker build -t "$FullImageName`:$Tag" -f Dockerfile .
        
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Docker build failed!"
            exit 1
        }
        
        Write-Host ""
        Write-Host "? Docker image built successfully!" -ForegroundColor Green
        Write-Host ""
        
        # Tag additional versions
        foreach ($additionalTag in $AdditionalTags) {
            Write-Host "Tagging as: $FullImageName`:$additionalTag" -ForegroundColor Yellow
            docker tag "$FullImageName`:$Tag" "$FullImageName`:$additionalTag"
        }
    }
    finally {
        Pop-Location
    }
} else {
    Write-Host "Skipping build step..." -ForegroundColor Yellow
    Write-Host ""
}

# Push to Docker Hub
if (-not $SkipPush) {
    Write-Host "Pushing to Docker Hub..." -ForegroundColor Green
    
    # Check if logged in to Docker Hub
    $dockerInfo = docker info 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "? Not logged in to Docker Hub. Please run: docker login" -ForegroundColor Yellow
        $loginNow = Read-Host "Would you like to login now? (y/n)"
        if ($loginNow -eq 'y') {
            docker login
            if ($LASTEXITCODE -ne 0) {
                Write-Error "Docker login failed!"
                exit 1
            }
        } else {
            Write-Error "Cannot push without Docker Hub authentication"
            exit 1
        }
    }
    
    Write-Host ""
    Write-Host "Pushing: $FullImageName`:$Tag" -ForegroundColor Yellow
    docker push "$FullImageName`:$Tag"
    
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Docker push failed!"
        exit 1
    }
    
    # Push additional tags
    foreach ($additionalTag in $AdditionalTags) {
        Write-Host "Pushing: $FullImageName`:$additionalTag" -ForegroundColor Yellow
        docker push "$FullImageName`:$additionalTag"
        
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Docker push failed for tag: $additionalTag"
            exit 1
        }
    }
    
    Write-Host ""
    Write-Host "? Successfully pushed to Docker Hub!" -ForegroundColor Green
} else {
    Write-Host "Skipping push step..." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "=====================================" -ForegroundColor Cyan
Write-Host "Summary" -ForegroundColor Cyan
Write-Host "=====================================" -ForegroundColor Cyan
Write-Host "Image: $FullImageName`:$Tag" -ForegroundColor White
if ($AdditionalTags.Count -gt 0) {
    Write-Host "Additional tags: $($AdditionalTags -join ', ')" -ForegroundColor White
}
Write-Host ""
Write-Host "To pull this image, run:" -ForegroundColor Yellow
Write-Host "  docker pull $FullImageName`:$Tag" -ForegroundColor White
Write-Host ""
Write-Host "To run locally:" -ForegroundColor Yellow
Write-Host "  docker run -p 8080:8080 -e ASB_RESOURCE_NAME=myservicebus -e ASB_SQL_PORT=1433 -e ASB_SQL_PASSWORD=yourpassword $FullImageName`:$Tag" -ForegroundColor White
Write-Host ""
