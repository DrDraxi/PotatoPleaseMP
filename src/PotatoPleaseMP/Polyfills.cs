// Unity 6's UnityEngine.CoreModule interop exposes its own public System.Runtime.CompilerServices.Nullable*Attribute
// types with different constructors, which the compiler would pick up and fail on. Defining them here makes the
// compiler use these (types in the compilation win over referenced ones).
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.All, Inherited = false)]
    internal sealed class NullableAttribute : Attribute
    {
        public readonly byte[] NullableFlags;
        public NullableAttribute(byte flag) => NullableFlags = new[] { flag };
        public NullableAttribute(byte[] flags) => NullableFlags = flags;
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Delegate | AttributeTargets.Interface | AttributeTargets.Method | AttributeTargets.Struct, Inherited = false)]
    internal sealed class NullableContextAttribute : Attribute
    {
        public readonly byte Flag;
        public NullableContextAttribute(byte flag) => Flag = flag;
    }
}

namespace System.Runtime.CompilerServices
{
    // Same story for the AudioModule interop and `where T : unmanaged`.
    [AttributeUsage(AttributeTargets.All)]
    internal sealed class IsUnmanagedAttribute : Attribute { }
}
