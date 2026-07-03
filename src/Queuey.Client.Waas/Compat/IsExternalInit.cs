#if !NET5_0_OR_GREATER
using System.ComponentModel;

namespace System.Runtime.CompilerServices
{
    /// <summary>Polyfill enabling <c>init</c>-only setters on netstandard2.0.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class IsExternalInit { }
}
#endif
