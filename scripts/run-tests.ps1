param([string]$Filter = 'Category!=Kernel&Category!=Stress')
$ErrorActionPreference = 'Stop'
dotnet test (Join-Path $PSScriptRoot '..\ZfsWindows.sln') --filter $Filter
