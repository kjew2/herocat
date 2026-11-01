using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("HeroCat.Runtime.Tests")]

namespace System.Runtime.CompilerServices
{
    /// <summary>Polyfill so C# 9 records and init accessors compile on netstandard2.1 / Unity.</summary>
    internal static class IsExternalInit { }
}
