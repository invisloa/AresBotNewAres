# Build and test safety

- Use standard `dotnet build` and `dotnet test` output paths whenever possible. Do not put temporary build output inside a project directory: SDK globbing can pick it up on subsequent builds.
- If a separate output path is necessary, use an absolute path outside the repository and pass each option as a separate shell argument. Never append MSBuild options inside the output path, and avoid trailing backslashes in quoted Windows paths (they can escape the closing quote). For example, in PowerShell: `dotnet build DriverScanTester/DriverScanTester.csproj -o "$env:TEMP/DriverScanTester-build" -p:UseAppHost=false`.
- If MSBuild reports an invalid `Files` path containing a command-line option, inspect the relevant `obj/**/DriverScanTester.csproj.FileListAbsolute.txt` and remove only the malformed line; do not delete user files or unrelated changes.
