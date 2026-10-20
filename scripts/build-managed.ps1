$ErrorActionPreference = 'Stop'
dotnet build (Join-Path $PSScriptRoot '..\ZfsWindows.sln')
