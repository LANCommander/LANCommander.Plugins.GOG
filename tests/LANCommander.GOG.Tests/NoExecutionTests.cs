using System.Reflection;

namespace LANCommander.GOG.Tests;

/// <summary>
/// Guards the library's central promise: it reads installers as data and never runs them.
/// </summary>
public class NoExecutionTests
{
    private static Assembly Library => typeof(GogInstaller).Assembly;

    [Fact]
    public void TheLibraryNeverStartsAProcess()
    {
        var offenders = Library.GetTypes()
            .SelectMany(type => type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .SelectMany(method => method.GetParameters().Select(p => p.ParameterType).Append(method.ReturnType))
            .Where(type => type.FullName is "System.Diagnostics.Process" or "System.Diagnostics.ProcessStartInfo")
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void TheLibraryDeclaresNoNativeEntryPoints()
    {
        var imported = Library.GetTypes()
            .SelectMany(type => type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(method => method.Attributes.HasFlag(MethodAttributes.PinvokeImpl))
            .Select(method => $"{method.DeclaringType?.Name}.{method.Name}")
            .ToList();

        Assert.Empty(imported);
    }

    [Fact]
    public void TheLibraryDoesNotReferenceProcessRelatedAssemblies()
    {
        var referenced = Library.GetReferencedAssemblies()
            .Select(name => name.Name)
            .Where(name => name is "System.Diagnostics.Process")
            .ToList();

        Assert.Empty(referenced);
    }
}
