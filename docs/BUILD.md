# Build

Install the .NET 9 SDK and Visual Studio 2022 Community or Build Tools with the Desktop development with C++ workload. Then run:

```powershell
.\build.ps1
.\Launch.ps1
```

If Windows blocks unsigned scripts, run `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`. This applies only to that process.

Output is in `dist/MCCTheaterCapture/`. A development build uses the installed .NET Desktop Runtime and WebView2 Runtime. Dependencies under `deps/` are checked against `deps/vendor-identities.json` before building.

## Standalone package

Supply a complete Microsoft Fixed Version x64 WebView2 runtime directory:

```powershell
.\package.ps1 -Build -WebView2Runtime 'C:\Dependencies\WebView2Runtime'
```

The pinned runtime download and SHA-256 are recorded in `deps/WebView2Fixed/manifest.json`. Its extracted browser files stay local. If placed at the default versioned path beneath `deps/WebView2Fixed/`, omit `-WebView2Runtime`.

Packaging creates `MCCTheaterCapture.zip` and `FFmpeg-source.zip` in a new folder under `dist/releases/`. Distribute both downloads together; only the application ZIP is needed at runtime. Required notices are retained. Use `-DotNetPath` to select the SDK for publishing; `-Build` also requires `dotnet` on PATH.

FFmpeg rebuild instructions, source identities, and configuration are in `deps/FFmpeg/`. The matching source archives are retained for reproducible source bundles.

Only the standard app and required build inputs are shared. Local data and generated output are excluded by `.gitignore`.
