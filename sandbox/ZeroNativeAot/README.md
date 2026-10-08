# ZeroNativeAot

A self-checking MemoryPack NativeAOT smoke test targeting .NET 11. The project
references the local core library and runs the local source generator as an
analyzer, so it validates this checkout rather than a released NuGet package.

The executable checks primitives, unmanaged structs, generated objects with
UTF-8/UTF-16 strings, closed generics, nested collections, both union cases
(including an extended tag), version tolerance in both directions, buffer writer
and segmented sequence APIs, overwrite deserialization, null/empty values,
truncated input rejection, and asynchronous stream serialization.

From the repository root, publish and run on Windows x64:

```powershell
dotnet publish sandbox/ZeroNativeAot/ZeroNativeAot.csproj -c Release -r win-x64 -o artifacts/ZeroNativeAot/win-x64
& ./artifacts/ZeroNativeAot/win-x64/ZeroNativeAot.exe --require-aot
```

On Linux x64, publish on a Linux host with the NativeAOT toolchain installed:

```sh
dotnet publish sandbox/ZeroNativeAot/ZeroNativeAot.csproj -c Release -r linux-x64 -o artifacts/ZeroNativeAot/linux-x64
./artifacts/ZeroNativeAot/linux-x64/ZeroNativeAot --require-aot
```

Use a .NET SDK supporting `net11.0` and the native compiler/linker prerequisites
for the host platform. No runtime identifier is hardcoded in the project.
`PublishAot` is enabled; the native publish is the actual AOT validation.

If Windows SDK tool discovery captures shell startup output and produces an
invalid linker path, run the publish command from Visual Studio's Developer
PowerShell for x64 with `-p:IlcUseEnvironmentalTools=true`. This uses the C++
tools and library paths already configured in that shell.

For a quick managed check:

```sh
dotnet run --project sandbox/ZeroNativeAot/ZeroNativeAot.csproj -c Release
```

Every scenario prints `PASS` or `FAIL`, followed by a summary. The process exits
with code 1 if any check fails. `--require-aot` additionally rejects a managed/JIT
run, preventing it from being mistaken for native validation. The tests use
generated formatter registration without manual registration or linker roots.

Publishing can still report trimming/AOT warnings from the core library's
reflection-based formatter fallback and type metadata. Keep these warnings
visible: passing this smoke test validates the scenarios above, not every
possible formatter or runtime-discovered type.
