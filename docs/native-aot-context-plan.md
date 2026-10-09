# NativeAOT serialization context implementation plan

## Goal and baseline

Provide a supported, warning-free NativeAOT path through MemoryPack using an
explicit source-generated registration context. Reuse the existing generated
serialization methods and formatter implementations, and preserve serialized
bytes and normal JIT behavior for supported existing APIs.

Baseline: commit `230e73431ec9e1d0b23e4773a226d613dfed9e3e`, clean working tree,
targets net8.0/net10.0/net11.0. The net11.0 ZeroNativeAot sandbox publishes and
passes 12 native scenarios, but emits 38 AOT/trimming warnings. The detailed
investigation is in `artifacts/ZeroNativeAot-warning-analysis.md`.

## Chosen design

Start with a generated registration context and the existing global generic
formatter caches. Per-call, independently configured context instances are a
separate future feature; they would require changing nested reader/writer and
cache resolution throughout the library.

This initial registration-only phase is complete. The subsequent user-requested
instance metadata/context APIs are implemented and reviewed in
[the type-info/context plan](memorypack-typeinfo-context-plan.md).

Proposed application contract:

```csharp
[MemoryPackSerializable<Order>]
[MemoryPackSerializable<List<Order>>]
[MemoryPackSerializable<IOrderEvent>]
internal static partial class AppMemoryPackContext
{
}

AppMemoryPackContext.Register();
var bytes = MemoryPackSerializer.Serialize(order);
var result = MemoryPackSerializer.Deserialize<Order>(bytes);
```

The new attribute identifies context roots. The generator supplies an explicit,
repeatable, thread-safe `Register()` method. A base class is unnecessary for a
static registration context. New naming or shape changes must be reviewed before
dependent implementation spreads.

An optional `FormatterType = typeof(MyFormatter)` named attribute argument is
approved for explicit external mappings. It must name an accessible closed
`MemoryPackFormatter<Root>` with an accessible parameterless constructor.
Treat its implementation as opaque: dependencies invoked inside a custom
formatter must be declared separately by the application. Explicit mappings
must take effect even when the provider has a built-in/default registration;
repeat calls must have documented, deterministic precedence.

Register the concrete formatter graph reachable from the roots: nested models,
arrays/collections, closed generic substitutions, union implementations and
custom formatters. Handle recursive graphs without infinite generation.
Report diagnostics for invalid context declarations, open generic roots and
unsupported dependencies that can be established at compile time. Do not
silently omit an unsupported type or assume arbitrary runtime subtypes exist.

The AOT provider resolves explicitly registered types without reflected method
discovery or runtime generic construction. An unknown type produces a clear
MemoryPackSerializationException naming the missing type and registration
requirement. Existing generated static constructors and registration helpers
must be audited: they must not accidentally retain an unsafe global fallback.

Keep the JIT reflection fallback where required for compatibility, isolated so
native publishing can exclude it. A merely mutable runtime flag or successful
startup registration does not establish linker unreachability. Test the actual
published result; do not solve this with blanket warning suppression or rooting
the entire assembly. Any narrowly justified suppression must be reviewed.

## Implementation steps

1. Establish focused baseline test results and capture original wire payloads
   for representative models before modifying the runtime. Inspect existing
   custom-formatter and generator test conventions.
2. Add context attributes and generator support with direct closed-type
   registration. Include deterministic output and diagnostics tests.
3. Implement strict AOT formatter resolution and retain the JIT compatibility
   path. Ensure registration completes before lookup, including deserialize-first
   scenarios. Keep failures actionable, and verify concurrent/repeated startup.
4. Replace or bypass reflection-based array/fixed-size optimization probes in
   AOT. Prefer statically known/generated metadata. Preserve wire encoding and
   document any deliberate optimization tradeoff.
5. Remove broad metadata roots only once generated/direct references preserve
   required entry points. Propagate precise contracts for any retained reflection.
6. Generate custom formatter attribute/factory construction directly when its
   arguments are compile-time known, including constructor/named arguments,
   enum/type/array/null constants and member accessibility. Give diagnostics for
   unsupported forms. Preserve the behavior of existing supported attributes.
7. Keep name-based System.Type resolution out of the default strict AOT provider.
   Provide/document an explicit supported-type mapping or an explicit formatter
   hook if Type serialization is needed. Preserve JIT behavior and avoid silently
   changing existing serialized type names.
8. Update ZeroNativeAot to initialize its generated context, strengthen regression
   coverage, document migration and limitations, and add a reproducible native
   validation command/script or appropriate existing CI check.

## Required validation

- Native publish of ZeroNativeAot on Windows x64: zero IL trimming/AOT warnings,
  no broad warning suppression, no whole-assembly linker roots; execute the
  resulting binary with `--require-aot` and require every scenario to pass.
- Verify deserialize-first behavior for models and unions with no prior model
  construction/serialization; test null as the first value and separate-process
  isolation where static cache initialization would otherwise mask a defect.
- Verify nested/recursive graphs, collection roots, closed generics, all union
  cases, version tolerance, custom formatter attributes, and explicit external
  formatter registration supported by the design.
- Verify missing registration produces the intended failure in the actual strict
  path. Verify repeated/concurrent context registration and deterministic output.
- Compare representative payloads against baseline fixtures and test both
  directions of compatibility. Include arrays/fixed-size models affected by
  changed fast paths, UTF-8/UTF-16, unions, and version tolerance.
- Run the applicable generator and runtime test suite on net8.0, net10.0 and
  net11.0. Distinguish pre-existing failures using the baseline. Compile dependent
  library projects to catch public-contract regressions.
- Native testing may use the net11.0 sandbox, but strict-path implementation
  must compile for every supported runtime target.
- Retain publish logs and unique MSBuild binlogs under `artifacts/`.

## Ownership and review

GPT-6.1 Sol / High owns implementation in `src/MemoryPack.Core/`,
`src/MemoryPack.Generator/`, `tests/MemoryPack.Tests/`, `sandbox/ZeroNativeAot/`,
and the relevant README documentation. The coordinating agent owns this plan,
design review, independent verification, and review artifacts. Shared-interface
changes within those implementation directories have one implementation owner.
Coordinate before touching other paths; do not stage, commit, reset, stash,
change branches or clean the shared checkout.

Review checkpoints: proposed runtime dispatch/registration mechanism; first
zero-warning native publish; final diff and compatibility verification. Review
findings go back to the implementation owner for bounded corrections, followed
by a fresh review and appropriate checks.

The implementation is complete only after unresolved correctness findings are
fixed and independent validation confirms the combined result.

## Resumption update (2026-10-08)

The user changed the context contract to generic attributes (`[MemoryPackSerializable<T>]`). Preserve that choice and align generator discovery, root extraction, documentation, and tests with it. Existing solution-format migration and generated web asset deletions are unrelated user changes and remain outside this task.

## Completed implementation and independent review

GPT-6.1 Sol / High implemented the generic attribute contract, generated closed-type
registration graph, strict NativeAOT provider, direct custom attribute construction,
and explicit `System.Type` name mapping. The original `typeof(...)` attribute form
is also supported. The application calls `AppMemoryPackContext.Register()` before
serialization or deserialization.

Review corrections covered external `NoGenerate` unions, explicit formatter
precedence, precise boxed/null attribute arguments, partial declaration
deduplication, and recovery after failed generic/non-generic lookups. The reviewer
independently reproduced and verified the cache recovery correction.

Graph discovery now stops with a bounded diagnostic for expanding generic
recursion: active paths are limited to 128 levels and individual type expressions
to 512 structural nodes. Traversal stops after its first error, including for
branching expansions; ordinary cycles continue to use the visited-type set.

Expanded native coverage exposed a circular serialization access violation that
also reproduced against untouched baseline binaries on this .NET 11 SDK. Keeping
the generated circular body behind a non-inlined helper resolves the observed
crash while preserving the bytes. This is a verified workaround; the underlying
compiler/runtime cause has not been established.

Validation on Windows x64:

- Native sandbox: zero IL trimming/AOT warnings, 21/21 scenarios, and two separate
  deserialize-first processes passed. The reviewer independently reran all three
  processes and inspected the publish binlog (zero diagnostics).
- Runtime/generator suite: 176/176 passed on each of net8.0, net10.0, and net11.0.
- MemoryPack facade and ASP.NET Core formatter dependencies build on all three
  target frameworks with zero warnings/errors; Streaming builds with the tests.
- Independent copied-binary corpus: all 26 UTF-8/UTF-16 payloads exactly match the
  baseline on both JIT and NativeAOT. Additional closed-generic circular models
  preserve identity and bytes through buffer-writer and asynchronous stream APIs.
- The independent native corpus publish also has zero binlog diagnostics.

Evidence is retained under `artifacts/native-aot-review/`,
`artifacts/native-context-tests/`, and the native sandbox log set
`artifacts/native-aot-20261009-000828-acbdb0b354a24603999dc21566cb28cf.*`.
The script `sandbox/ZeroNativeAot/Validate-NativeAot.ps1` reproduces the native
publish and process checks with fresh logs and a unique binlog.

Native execution was verified on net11.0/win-x64. Other target frameworks passed
managed tests; other native platforms were not run. Optional reflected allocation
fast paths are bypassed in NativeAOT. Contexts use the global provider, and custom
formatter dependencies must be declared explicitly. Trimmed JIT reflection
fallback remains a separate concern. These boundaries are documented in README.

Final resumption on 2026-10-09 preserved the user's intervening package, result
type, Core syntax, and linker-path changes. The complete current checkout passed
the three-framework suite and dependent library builds again. The default native
validation command passed without an explicit VC environment argument. Refreshed
copied-binary JIT and native probes again matched all 26 baseline payloads and
passed the extra generic circular buffer/stream checks. Both final native publish
binlogs contain zero diagnostics. Current-checkout evidence uses the `resume-*`
files and `resume-tests/` under `artifacts/native-aot-review/`.
