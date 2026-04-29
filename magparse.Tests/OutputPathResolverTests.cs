using magparse.Services;
using Xunit;

namespace magparse.Tests;

public sealed class OutputPathResolverTests
{
    private const string CurrentDirectory = "/workspace/current";

    [Fact]
    public void Resolve_UsesSubdirectoryNamedAfterPdfByDefault()
    {
        var path = OutputPathResolver.Resolve(null, "/input/Magazine.pdf", CurrentDirectory);

        Assert.Equal(Path.Combine(CurrentDirectory, "Magazine"), path);
    }

    [Fact]
    public void Resolve_UsesProvidedDirectoryPath()
    {
        var path = OutputPathResolver.Resolve("./out/mydir", "/input/Magazine.pdf", CurrentDirectory);

        Assert.Equal(Path.GetFullPath("./out/mydir"), path);
    }
}