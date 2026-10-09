# MemoryPack metadata and serializer context plan

## Objective

Add explicit serialization metadata and generated context instances on top of the
existing NativeAOT registration support. Preserve the existing static registration
contexts and binary format. GPT-6.1 Sol / High implements the changes; the
coordinator reviews the API, source changes, and independently verifies them.

The current checkout is the baseline, including the user's generic attributes,
Failure/Polyester result types, Core edits, and linker-path normalization. Prior
verification passed 176 tests on each target framework and 21 native scenarios
plus two isolated deserialize-first checks, with zero native publish warnings.

## Application contract

```csharp
[MemoryPackSerializable<Order>]
[MemoryPackSerializable<List<Order>>]
internal partial class AppContext : MemoryPackSerializerContext { }

var bytes = MemoryPackSerializer.Serialize(order, AppContext.Default.Order);
var value = MemoryPackSerializer.Deserialize(bytes, AppContext.Default.Order);
var metadata = AppContext.Default.GetTypeInfo<Order>();
```

`MemoryPackTypeInfo<T>` carries a concrete formatter and its context; an untyped
base supports runtime type lookup and object APIs. Contexts expose options,
typed/untyped lookup, stable generated root properties, and a default instance.
Manual metadata can wrap an explicitly supplied formatter. Unknown types and
inconsistent metadata should produce actionable exceptions.

Generated context instances own their formatter tables. They resolve provider
dependencies within that table, without global registration or reflected generic
construction. Nested provider calls inherit the context through operation state.
Missing dependencies do not fall back to the global registry. Existing generated
direct primitive/packable calls retain their compiled behavior; root metadata
always dispatches through its formatter, including for unmanaged root types.

Root property names are deterministic. An optional `TypeInfoPropertyName` allows
explicit names and resolves collisions. Invalid context declarations, duplicate
names, and unsupported graphs produce source-generator diagnostics. The existing
generic graph termination safeguards remain in place.

## Implementation and verification

1. Review the exact public API and generated declaration shape before spreading
   implementation. Retain legacy static context output.
2. Add immutable metadata/context APIs, generated closed-type descriptors, and
   typed root properties. Reuse the validated dependency graph.
3. Add metadata/context serializer overloads for buffers, spans, sequences,
   overwrite, streams, and untyped object use. Bind context state per operation
   and restore/reset it reliably after success, failure, and cancellation.
4. Verify context isolation under concurrency, conflicting explicit formatters,
   nested external dependencies, options, unknown types, null-first reads,
   manual metadata, and binary compatibility. Document direct-call semantics.
5. Update the sandbox and README. Run the full suite on net8/net10/net11, compile
   dependent libraries, and publish/run actual native scenarios on win-x64.
6. Independently compare representative JIT/native bytes against captured baseline
   payloads and inspect publish binlogs for zero IL trimming/AOT diagnostics.

All implementation files have one writer. Preserve unrelated existing edits;
serialize builds that share outputs. Retain unique binlogs and logs under
`artifacts/`. Completion requires reviewed code, passing meaningful checks, and
no outstanding correctness findings or pending writes.

## Completed implementation and review

Implemented public `MemoryPackTypeInfo`, `MemoryPackTypeInfo<T>`, and
`MemoryPackSerializerContext`, plus typed/untyped metadata and context overloads
for buffers, spans, sequences, overwrite reads, and asynchronous streams.
Generated instance contexts expose `Default`, options constructors, root
properties, and a complete table of concrete formatter descriptors. Legacy
registration contexts remain available.

Review corrected pooled builder return, nullable formatter annotations,
ref callback compatibility, computed collection dependencies, and runtime type
identity. Named tuples and their underlying `ValueTuple` forms, including nested
generic arguments and formatter mappings, share one runtime metadata identity.
Structural graph bounds apply before canonicalization. Public named tuple root
properties retain their element names.

Final verification:

- 200/200 tests on each of net8.0, net10.0, and net11.0, with separate TRX files.
- The actual net11.0/win-x64 sandbox passed 24/24 native scenarios and both
  isolated deserialize-first processes. Its publish binlog contains zero
  diagnostics.
- Independent JIT and native probes matched all 26 captured baseline payloads
  exactly. They also passed conflicting nested contexts, options, manual
  metadata, unmanaged root overrides, missing-local-dependency errors despite
  global registration, reentrant legacy serialization, segmented input,
  deserialize-first/null-first models and unions, named tuple collections, and
  generic circular buffer/stream checks.
- The final independent native publish binlog contains zero diagnostics. Its
  copied Core and Generator DLLs match the final checkout binaries by SHA-256.
- Facade and ASP.NET Core formatter projects built across all three frameworks
  with zero warnings/errors; Streaming built through the test suite.

Evidence: `artifacts/typeinfo-review/`, `artifacts/typeinfo-tests/`,
`artifacts/typeinfo-final-normalized-tests.log`, and the sandbox log set
`artifacts/native-aot-20261009-064801-e34c2d1bd8f7474b85db6dfa62f962fb.*`.

The README explains context ownership, immutable operation options, provider
dependency scope, generated direct member dispatch, explicit opaque
formatter/callback dependencies, graph bounds, older union factories, and
`options: null` for legacy null-options calls affected by overload ambiguity.
Typed asynchronous reads currently use the untyped adapter. Native verification
was performed on Windows x64/.NET 11; other frameworks passed managed checks.
All implementation writes and builds have finished.
