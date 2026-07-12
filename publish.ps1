# Builds a portable, self-contained single-file SentryNet.exe into .\Compiled.
# Usage:  powershell -ExecutionPolicy Bypass -File .\publish.ps1
# The exe bundles the .NET 9 runtime, so it runs on any 64-bit Windows 10/11 PC
# with nothing installed. First launch self-extracts to %TEMP%\.net (TraceEvent
# ships native DLLs that need real files on disk), so it takes a few extra
# seconds once per machine.
$ErrorActionPreference = 'Stop'
$proj = $PSScriptRoot

dotnet publish "$proj\SentryNet.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:IncludeAllContentForSelfExtract=true `
    -o "$proj\bin\publish"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

New-Item -ItemType Directory -Force "$proj\Compiled" | Out-Null
Copy-Item "$proj\bin\publish\SentryNet.exe" "$proj\Compiled\SentryNet.exe" -Force
$size = (Get-Item "$proj\Compiled\SentryNet.exe").Length / 1MB
"Compiled\SentryNet.exe ({0:N1} MB) ready - portable, no .NET install needed" -f $size
