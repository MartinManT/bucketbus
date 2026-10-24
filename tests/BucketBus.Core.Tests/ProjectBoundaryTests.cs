using System.Xml.Linq;

namespace BucketBus.Core.Tests;

public sealed class ProjectBoundaryTests
{
    [Theory]
    [InlineData("Core", new string[0])]
    [InlineData("S3", new[] { "Core" })]
    [InlineData("Platform", new[] { "Core" })]
    [InlineData("Desktop", new[] { "Core", "Platform", "S3" })]
    public void Production_projects_only_reference_their_approved_modules(string project, string[] dependencies)
    {
        var document = LoadProject(project);
        var actual = document.Descendants("ProjectReference")
            .Select(reference => Path.GetFileNameWithoutExtension(reference.Attribute("Include")!.Value))
            .Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(dependencies.Select(name => "BucketBus." + name).Order(StringComparer.Ordinal), actual);
    }

    [Fact]
    public void Core_has_no_external_framework_packages_or_provider_types()
    {
        Assert.Empty(LoadProject("Core").Descendants("PackageReference"));
        Assert.All(typeof(InitializationStatus).Assembly.GetReferencedAssemblies(), assembly =>
            Assert.StartsWith("System.", assembly.Name!));
    }

    [Fact]
    public void Aws_packages_stay_in_the_s3_adapter()
    {
        foreach (var project in new[] { "Core", "Platform", "Desktop" })
        {
            Assert.DoesNotContain(LoadProject(project).Descendants("PackageReference"), reference =>
                reference.Attribute("Include")!.Value.StartsWith("AWSSDK.", StringComparison.Ordinal));
        }
    }

    private static XDocument LoadProject(string project)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BucketBus.slnx")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return XDocument.Load(Path.Combine(directory.FullName, "src", "BucketBus." + project,
            "BucketBus." + project + ".csproj"));
    }
}
