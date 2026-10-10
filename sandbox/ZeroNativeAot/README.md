# ZeroNativeAot

A self-checking MemoryPack NativeAOT smoke test targeting .NET 11. The project
references the local core library and runs the local source generator as an
analyzer, so it validates this checkout rather than a released NuGet package.

The executable checks primitives, unmanaged structs, generated objects with
UTF-8/UTF-16 strings, closed generics, nested collections, both union cases
(including an extended tag), version tolerance in both directions, buffer writer
and segmented sequence APIs, overwrite deserialization, null/empty values,
truncated input rejection, asynchronous stream serialization, recursive/circular
graphs, collection roots, managed nullable structs, external generic unions,
custom formatter attribute arguments, explicit formatter precedence, bounded
`System.Type` resolution, missing-registration recovery, and baseline wire fixtures.
It also verifies instance context metadata, typed/untyped root dispatch, custom
unmanaged formatters, conflicting nested formatter mappings, context options,
concurrency, and computed priority-queue/lookup/interface-list dependencies.
It also checks C# 15 declaration-based unions, explicit wide tags, empty union
values, generic case dependencies, and closed record hierarchies through both
static registration and instance serialization contexts.

Instance serialization uses generated metadata without global registration:

```csharp
var context = AppSerializerContext.Default;
var bytes = MemoryPackSerializer.Serialize(item, context.Item);
var restored = MemoryPackSerializer.Deserialize(bytes, context.Item);
```

`AppSerializerContext` demonstrates the new instance API. `AppMemoryPackContext`
remains a static registration context to keep exercising legacy global APIs.

From the repository root, publish and run on Windows x64:

```powershell
./sandbox/ZeroNativeAot/Validate-NativeAot.ps1
```

On Linux x64, publish on a Linux host with the NativeAOT toolchain installed:

```sh
dotnet publish sandbox/ZeroNativeAot/ZeroNativeAot.csproj -c Release -r linux-x64 -o artifacts/ZeroNativeAot/linux-x64 '-bl:artifacts/native-aot-linux-{}.binlog'
./artifacts/ZeroNativeAot/linux-x64/ZeroNativeAot --require-aot
```

Use a .NET SDK supporting `net11.0` and the native compiler/linker prerequisites
for the host platform. No runtime identifier is hardcoded in the project.
`PublishAot` is enabled; the native publish is the actual AOT validation.

If Windows SDK tool discovery captures shell startup output and produces an
invalid linker path, supply the Visual Studio C++ environment script explicitly:

```powershell
./sandbox/ZeroNativeAot/Validate-NativeAot.ps1 -VcVarsAll 'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvarsall.bat'
```

The validation script imports the required tool paths into its process, captures
a unique publish log and MSBuild binlog under `artifacts/`, rejects IL warnings,
then runs the native executable and separate deserialize-first model/union
processes. Adjust the Visual Studio path for the installed version.

For a quick managed check:

```sh
dotnet build sandbox/ZeroNativeAot/ZeroNativeAot.csproj -c Release '-bl:artifacts/native-aot-managed-{}.binlog'
dotnet sandbox/ZeroNativeAot/bin/Release/net11.0/ZeroNativeAot.dll
```

Every scenario prints `PASS` or `FAIL`, followed by a summary. The process exits
with code 1 if any check fails. `--require-aot` additionally rejects a managed/JIT
run, preventing it from being mistaken for native validation. The tests use
the generated `AppMemoryPackContext.Register()` startup method without linker
roots. External formatter and `System.Type` mapping scenarios intentionally
exercise the supported explicit registration APIs.

The native publish is required to produce zero IL trimming/AOT warnings. Native
execution uses registered formatters and does not fall back to runtime generic
construction or name-based type discovery. Passing these scenarios validates
their declared graphs; application-owned formatter dependencies must also be
registered explicitly. See the repository README for the context contract and
the optional allocation fast paths bypassed in Native AOT.
