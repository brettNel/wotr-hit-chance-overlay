// Polyfill so C# 9+ records/init-only setters compile against .NET Framework 4.8.1.
namespace System.Runtime.CompilerServices;

internal static class IsExternalInit { }
