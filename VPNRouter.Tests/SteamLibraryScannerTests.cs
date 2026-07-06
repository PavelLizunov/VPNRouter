using VPNRouter.App.Services;

namespace VPNRouter.Tests;

public class SteamLibraryScannerTests
{
    [Fact]
    public void FindGames_ParsesSteamLibrariesAndSkipsHelperExecutables()
    {
        var root = Directory.CreateTempSubdirectory("vpnrouter-steam-");
        try
        {
            var library = Path.Combine(root.FullName, "library2");
            var steamApps = Path.Combine(library, "steamapps");
            var gameDir = Path.Combine(steamApps, "common", "Test Game");
            var binDir = Path.Combine(gameDir, "bin");
            Directory.CreateDirectory(binDir);

            Directory.CreateDirectory(Path.Combine(root.FullName, "steamapps"));
            File.WriteAllText(
                Path.Combine(root.FullName, "steamapps", "libraryfolders.vdf"),
                $$"""
                "libraryfolders"
                {
                    "1"
                    {
                        "path" "{{library.Replace(@"\", @"\\")}}"
                    }
                }
                """);

            File.WriteAllText(
                Path.Combine(steamApps, "appmanifest_123.acf"),
                """
                "AppState"
                {
                    "name" "Test Game"
                    "installdir" "Test Game"
                }
                """);

            File.WriteAllText(Path.Combine(gameDir, "TestGame.exe"), "");
            File.WriteAllText(Path.Combine(gameDir, "unins000.exe"), "");
            File.WriteAllText(Path.Combine(gameDir, "UnityCrashHandler64.exe"), "");
            File.WriteAllText(Path.Combine(binDir, "TestGame-Win64-Shipping.exe"), "");
            File.WriteAllText(Path.Combine(binDir, "vc_redist.x64.exe"), "");
            File.WriteAllText(Path.Combine(binDir, "setup.exe"), "");

            var games = SteamLibraryScanner.FindGames(new[] { root.FullName });
            var names = games.Select(g => g.ProcessName).ToList();

            Assert.Contains("TestGame.exe", names);
            Assert.Contains("TestGame-Win64-Shipping.exe", names);
            Assert.DoesNotContain("unins000.exe", names);
            Assert.DoesNotContain("UnityCrashHandler64.exe", names);
            Assert.DoesNotContain("vc_redist.x64.exe", names);
            Assert.DoesNotContain("setup.exe", names);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
