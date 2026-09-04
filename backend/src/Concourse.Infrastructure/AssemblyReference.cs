using System.Reflection;

namespace Concourse.Infrastructure;

internal static class AssemblyReference
{
    internal static readonly Assembly Assembly = typeof(AssemblyReference).Assembly;
}
