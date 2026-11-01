$ErrorActionPreference = "Stop"

$project = Join-Path $PSScriptRoot "DualCopy.Windows.csproj"
$output = Join-Path $PSScriptRoot "dist"

if (Test-Path $output) {
    Remove-Item -Recurse -Force $output
}

# Produces a self-contained Windows 11 x64 executable with the .NET runtime bundled.
dotnet publish $project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $output `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false

Write-Host "Created $output\\DualCopy.exe"
