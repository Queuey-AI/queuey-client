#if !NET5_0_OR_GREATER
using System.ComponentModel;

namespace System.Runtime.CompilerServices
{
    /// <summary>Enables <c>init</c>-only setters and records when targeting netstandard2.0.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class IsExternalInit
    {
    }
}
#endif
