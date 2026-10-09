#if NET11_0_OR_GREATER
using MemoryPack.Errors;
using Polyester;
using Polyester.CompilerServices;
#endif

namespace MemoryPack;

#if NET11_0_OR_GREATER
[ResultImpl]
[NeverInstantiate]
public readonly partial union MemoryPackResult<T>(Success<T>, Failure<MemoryPackError>);
#endif
