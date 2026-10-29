using OnionHopV3.Core.Platform;
using Xunit;

namespace OnionHopV3.Tests.Platform;

/// <summary>
/// On a fresh Linux account ~/.config does not exist yet, and Environment.GetFolderPath returned an
/// empty string for it, so the data paths were relative and the CLI crashed where the working
/// directory was not writable. The data folders are always real, existing directories now.
/// </summary>
public sealed class AppDirectoriesTests
{
    [Fact]
    public void The_data_folders_are_rooted_existing_directories()
    {
        foreach (var folder in new[] { AppDirectories.RoamingData, AppDirectories.LocalData })
        {
            Assert.True(Path.IsPathRooted(folder), $"'{folder}' is not an absolute path");
            Assert.True(Directory.Exists(folder), $"'{folder}' does not exist");
        }
    }
}
