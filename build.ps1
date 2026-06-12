# Build helper script for the Low-Latency Voice Chat project
# Builds the .NET server, MockClient, and (optionally) native plugins
# Usage:
#   .\build.ps1                    # Build .NET projects only
#   .\build.ps1 -BuildNative       # Also build native DLLs (requires CMake + MSVC)
#   .\build.ps1 -RunTests          # Build + run xUnit tests
#   .\build.ps1 -LaunchServer      # Build + launch server
#   .\build.ps1 -RunMock -Clients 20 -Rooms 4 -Duration 120  # Build + run mock

param(
    [switch]$BuildNative,
    [switch]$RunTests,
    [switch]$LaunchServer,
    [switch]$RunMock,
    [int]$ServerPort = 50005,
    [int]$Clients = 10,
    [int]$Rooms = 2,
    [int]$Duration = 60
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

Write-Host "=========================================" -ForegroundColor Cyan
Write-Host " Low-Latency Voice Chat Build Script" -ForegroundColor Cyan
Write-Host "=========================================" -ForegroundColor Cyan

# --- .NET Build ---
Write-Host "`n[1/4] Building .NET solution (Release)..." -ForegroundColor Yellow
dotnet build "$root\voice chat.sln" -c Release --nologo
if ($LASTEXITCODE -ne 0) { Write-Error "dotnet build failed!"; exit 1 }
Write-Host "  .NET build OK." -ForegroundColor Green

# --- Native DLLs (optional) ---
if ($BuildNative) {
    Write-Host "`n[2/4] Building native Windows WASAPI DLL..." -ForegroundColor Yellow
    $nativeDir = "$root\Native\Windows"
    cmake -B "$nativeDir\build" -S "$nativeDir" -DCMAKE_BUILD_TYPE=Release
    cmake --build "$nativeDir\build" --config Release
    Copy-Item "$nativeDir\build\Release\VoiceCapture.dll" "$root\Client\Packages\com.goodvoice.voicechat\Plugins\x64\" -Force
    Write-Host "  VoiceCapture.dll built OK." -ForegroundColor Green
} else {
    Write-Host "`n[2/4] Skipping native build (use -BuildNative to build WASAPI/Opus DLLs)." -ForegroundColor DarkGray
}

# --- Tests ---
if ($RunTests) {
    Write-Host "`n[3/4] Running xUnit tests..." -ForegroundColor Yellow
    dotnet test "$root\Tests\Tests.csproj" -c Release --no-build --logger "console;verbosity=normal"
    if ($LASTEXITCODE -ne 0) { Write-Error "Tests failed!"; exit 1 }
    Write-Host "  All tests passed." -ForegroundColor Green
} else {
    Write-Host "`n[3/4] Skipping tests (use -RunTests to run xUnit tests)." -ForegroundColor DarkGray
}

# --- Server Launch ---
if ($LaunchServer) {
    Write-Host "`n[4/4] Launching server on port $ServerPort..." -ForegroundColor Yellow
    Start-Process powershell -ArgumentList "-NoExit", `
        "-Command", "dotnet run --project `"$root\Server`" -c Release --no-build -- --port $ServerPort"
    Start-Sleep 2
}

# --- MockClient ---
if ($RunMock) {
    if (-not $LaunchServer) {
        Write-Host "`n[4/4] Launching server on port $ServerPort (for mock test)..." -ForegroundColor Yellow
        Start-Process powershell -ArgumentList "-NoExit", `
            "-Command", "dotnet run --project `"$root\Server`" -c Release --no-build -- --port $ServerPort"
        Start-Sleep 2
    }
    Write-Host "`nRunning MockClient: $Clients clients, $Rooms rooms, ${Duration}s..." -ForegroundColor Yellow
    dotnet run --project "$root\MockClient" -c Release --no-build -- `
        --server 127.0.0.1 --port $ServerPort `
        --clients $Clients --rooms $Rooms --duration $Duration
}

Write-Host "`n=========================================" -ForegroundColor Cyan
Write-Host " Build complete!" -ForegroundColor Green
Write-Host "=========================================`n" -ForegroundColor Cyan
