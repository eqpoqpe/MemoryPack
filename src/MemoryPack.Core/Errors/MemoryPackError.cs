#if NET11_0_OR_GREATER
using Failure.CompilerServices;
#endif

namespace MemoryPack.Errors;

#if NET11_0_OR_GREATER
[FailureImpl]
public readonly partial struct MemoryPackError { }
#endif
