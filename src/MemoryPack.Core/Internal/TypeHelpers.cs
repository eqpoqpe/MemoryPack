using System.Reflection;
using System.Runtime.CompilerServices;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace MemoryPack.Internal;

internal static class TypeHelpers
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsReferenceOrNullable<T>()
    {
        return Cache<T>.IsReferenceOrNullable;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TypeKind TryGetUnmanagedSZArrayElementSizeOrMemoryPackableFixedSize<T>(out int size)
    {
        if (Cache<T>.IsUnmanagedSZArray)
        {
            size = Cache<T>.UnmanagedSZArrayElementSize;
            return TypeKind.UnmanagedSZArray;
        }
        else
        {
            if (Cache<T>.IsFixedSizeMemoryPackable)
            {
                size = Cache<T>.MemoryPackableFixedSize;
                return TypeKind.FixedSizeMemoryPackable;
            }
        }

        size = 0;
        return TypeKind.None;
    }

    public static bool IsAnonymous(Type type)
    {
        return type.Namespace == null
               && type.IsSealed
               && (type.Name.StartsWith("<>f__AnonymousType", StringComparison.Ordinal)
                   || type.Name.StartsWith("<>__AnonType", StringComparison.Ordinal)
                   || type.Name.StartsWith("VB$AnonymousType_", StringComparison.Ordinal))
               && type.IsDefined(typeof(CompilerGeneratedAttribute), false);
    }

    static class Cache<T>
    {
        public static bool IsReferenceOrNullable;
        public static bool IsUnmanagedSZArray;
        public static int UnmanagedSZArrayElementSize;
        public static bool IsFixedSizeMemoryPackable = false;
        public static int MemoryPackableFixedSize = 0;

        static Cache()
        {
            var type = typeof(T);
            IsReferenceOrNullable = !type.IsValueType || Nullable.GetUnderlyingType(type) != null;
            if (!RuntimeFeature.IsDynamicCodeSupported) return;
            InitializeWithReflection();
        }

        [RequiresDynamicCode("Optional fast paths inspect generic array element types at runtime.")]
        [RequiresUnreferencedCode("Optional fast paths inspect generated fixed-size properties at runtime.")]
        static void InitializeWithReflection()
        {
            try
            {
                var type = typeof(T);

                if (type.IsSZArray)
                {
                    var elementType = type.GetElementType();
                    bool containsReference = (bool)(typeof(RuntimeHelpers).GetMethod("IsReferenceOrContainsReferences")!.MakeGenericMethod(elementType!).Invoke(null, null)!);
                    if (!containsReference)
                    {
                        IsUnmanagedSZArray = true;
                        UnmanagedSZArrayElementSize = (int)typeof(Unsafe).GetMethod("SizeOf")!.MakeGenericMethod(elementType!).Invoke(null, null)!;
                    }
                }
#if NET7_0_OR_GREATER
                else
                {
                    if (typeof(IFixedSizeMemoryPackable).IsAssignableFrom(type))
                    {
                        var prop = type.GetProperty("global::MemoryPack.IFixedSizeMemoryPackable.Size", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                        if (prop != null)
                        {
                            IsFixedSizeMemoryPackable = true;
                            MemoryPackableFixedSize = (int)prop.GetValue(null)!;
                        }
                    }
                }
#endif
            }
            catch
            {
                IsUnmanagedSZArray = false;
                IsFixedSizeMemoryPackable = false;
            }
        }
    }

    internal enum TypeKind : byte
    {
        None,
        UnmanagedSZArray,
        FixedSizeMemoryPackable
    }
}
