using System.Diagnostics;
using System.Text.RegularExpressions;

namespace FactoryConnect.Dashboard.Tests;

[Collection(DashboardHostTestGroup.Name)]
public sealed partial class DashboardPublishTests
{
    [Fact]
    public async Task CleanPublishIncludesGeneratedFrontendEntryAndReferencedAsset()
    {
        var repositoryRoot = FindRepositoryRoot();
        var dashboardDirectory = Path.Combine(repositoryRoot, "src", "FactoryConnect.Dashboard");
        var generatedWebRoot = Path.Combine(dashboardDirectory, "wwwroot");
        var publishDirectory = Path.Combine(Path.GetTempPath(), $"factoryconnect-dashboard-publish-{Guid.NewGuid():N}");

        try
        {
            if (Directory.Exists(generatedWebRoot))
            {
                Directory.Delete(generatedWebRoot, recursive: true);
            }

            Directory.CreateDirectory(publishDirectory);

            var startInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = repositoryRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("publish");
            startInfo.ArgumentList.Add(Path.Combine("src", "FactoryConnect.Dashboard", "FactoryConnect.Dashboard.csproj"));
            startInfo.ArgumentList.Add("--no-restore");
            startInfo.ArgumentList.Add("--output");
            startInfo.ArgumentList.Add(publishDirectory);

            using var process = Process.Start(startInfo);
            Assert.NotNull(process);

            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await standardOutput;
            var error = await standardError;

            Assert.True(process.ExitCode == 0, $"dotnet publish failed.{Environment.NewLine}{output}{Environment.NewLine}{error}");

            var publishedWebRoot = Path.Combine(publishDirectory, "wwwroot");
            var indexPath = Path.Combine(publishedWebRoot, "index.html");
            Assert.True(File.Exists(indexPath), $"Published dashboard entry was not found at {indexPath}.");

            var index = await File.ReadAllTextAsync(indexPath);
            var match = HashedScriptRegex().Match(index);
            Assert.True(match.Success, "Published dashboard entry does not reference a hashed Vite asset.");

            var relativeAssetPath = match.Groups["path"].Value.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            Assert.True(
                File.Exists(Path.Combine(publishedWebRoot, relativeAssetPath)),
                $"Published dashboard asset referenced by index.html was not found: {relativeAssetPath}.");
        }
        finally
        {
            if (Directory.Exists(publishDirectory))
            {
                Directory.Delete(publishDirectory, recursive: true);
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FactoryConnect.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("FactoryConnect repository root could not be located.");
    }

    [GeneratedRegex("<script[^>]+src=\"(?<path>/assets/index-[^\"]+\\.js)\"")]
    private static partial Regex HashedScriptRegex();
}
