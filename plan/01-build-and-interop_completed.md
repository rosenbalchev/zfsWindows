# Step 01 — Build and C# interop foundation

Status: `complete`

Objective: make native C and .NET 10 projects build from Visual Studio/MSBuild
and provide a stable opaque-handle boundary.

Implementation:

- maintain `ZfsCore.vcxproj` and `ZfsNativeApi.vcxproj` plus CMake;
- build `ZfsCore.dll` and `ZfsNativeApi.dll` for x64 Debug/Release;
- build `Zfs.Interop` and `Zfs.Core.Tests` for `net10.0`;
- keep native tests read-only and safe by default.

Verification:

```powershell
msbuild ZfsWindows.sln /m /t:Build /p:Configuration=Debug /p:Platform="Any CPU"
dotnet test ZfsWindows.sln
```

Required evidence: native build passes; managed build passes; tests report
actual pass/skip/fail counts. The genuine-image test may remain skipped until
Step 05 provides a fixture.

Commit: `build: add Visual Studio native and managed foundation`

