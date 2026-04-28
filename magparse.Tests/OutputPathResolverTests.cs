using magparse.Services;
using Xunit;

namespace magparse.Tests;

public sealed class OutputPathResolverTests
{
    private const string CurrentDirectory = "/workspace/current";

    [Fact]
    public void Resolve_UsesCurrentDirectoryByDefault()
    {
        var path = OutputPathResolver.Resolve(null, "/input/Magazine.pdf", CurrentDirectory);

        Assert.Equal(Path.Combine(CurrentDirectory, "Magazine.json"), path);
    }

    [Fact]
    public void Resolve_UsesProvidedFilePath()
    {
        var path = OutputPathResolver.Resolve("./out/custom.json", "/input/Magazine.pdf", CurrentDirectory);

        Assert.Equal(Path.GetFullPath("./out/custom.json"), path);
    }

    [Fact]
    public void Resolve_AppendsDefaultFileNameWhenPathLooksLikeDirectory()
    {
        var path = OutputPathResolver.Resolve("./out/", "/input/Magazine.pdf", CurrentDirectory);

        Assert.Equal(Path.Combine(Path.GetFullPath("./out/"), "Magazine.json"), path);
    }
}