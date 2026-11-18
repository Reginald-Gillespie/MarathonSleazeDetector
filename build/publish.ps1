# Builds the release exe: one self-contained file, no .NET install needed on the user's PC.
# Output: publish\SleazeDetector.exe
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
dotnet publish "$root\src\SleazeDetector\SleazeDetector.csproj" -c Release -o "$root\publish"
# ONNX Runtime's package also drops its import libraries here; they are only for C++ linking.
Remove-Item "$root\publish\*.lib" -ErrorAction SilentlyContinue
Get-Item "$root\publish\SleazeDetector.exe" | Select-Object Name, @{n='MB';e={[math]::Round($_.Length/1MB,1)}}
