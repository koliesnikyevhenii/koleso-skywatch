using System.Xml.Linq;

namespace Skywatch.Core.Tests;

public class ProjectConstraintsTests
{
    [Fact]
    public void P0_AC5_core_project_has_no_package_references()
    {
        var csproj = Path.Combine(RepoRoot.Path, "src", "Skywatch.Core", "Skywatch.Core.csproj");

        var packageReferences = XDocument.Load(csproj).Descendants()
            .Where(e => e.Name.LocalName == "PackageReference")
            .Select(e => (string?)e.Attribute("Include") ?? e.ToString());

        packageReferences.ShouldBeEmpty();
    }
}
