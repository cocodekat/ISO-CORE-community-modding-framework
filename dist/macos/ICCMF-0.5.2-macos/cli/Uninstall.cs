using System.Diagnostics;
using System.Text;

namespace ICCMF.Cli;

/// <summary>Implements 'iccmf uninstall': removes installed mods, the mod list, and the tool itself.</summary>
internal static class SelfUninstaller
{
    private const string ToolPackage = "ICCMF.Cli";

    public static int Run(string[] args)
    {
        bool assumeYes = false;
        foreach (string argument in args.Skip(1))
        {
            if (argument is "--yes" or "-y") assumeYes = true;
            else throw new CliException("Usage: iccmf uninstall [--yes]", 2);
        }

        string? managedRoot = TryGetManagedRoot();
        bool hasMods = managedRoot != null && Directory.Exists(managedRoot);
        string registryPath = RegistryPath();

        Console.WriteLine("This will remove ICCMF from your computer:");
        Console.WriteLine("  - the iccmf command-line tool");
        if (File.Exists(registryPath)) Console.WriteLine("  - the mod list: " + registryPath);
        if (hasMods) Console.WriteLine("  - every mod installed by ICCMF: " + managedRoot);
        Console.WriteLine();
        Console.WriteLine("BepInEx, the game itself, and your mod source projects are NOT touched.");
        Console.WriteLine();

        if (!assumeYes)
        {
            if (Console.IsInputRedirected)
                throw new CliException("Refusing to uninstall without confirmation. Re-run with --yes.", 2);
            Console.Write("Type 'yes' to continue: ");
            string? answer = Console.ReadLine();
            if (!string.Equals(answer?.Trim(), "yes", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Cancelled. Nothing was changed.");
                return 0;
            }
        }

        // 1. Installed mods.
        if (hasMods)
        {
            try { Directory.Delete(managedRoot!, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new CliException("Could not remove the installed mods (" + error.Message +
                    "). Close the game and try again.", 3);
            }
            Console.WriteLine("Removed installed mods.");
        }

        // 2. Mod list.
        RemoveRegistry(registryPath);

        // 3. The tool itself, last: the running program deletes its own files.
        return RemoveTool();
    }

    private static string? TryGetManagedRoot()
    {
        try { return ModInstaller.ManagedRoot(); }
        catch (CliException) { return null; } // game or BepInEx not found: nothing to remove there
    }

    // Mirrors the location logic in ModRegistry.Load so a corrupt modlist.json can still be removed.
    private static string RegistryPath()
    {
        string directory = Environment.GetEnvironmentVariable("ICCMF_CONFIG_DIR")?.Trim() ?? string.Empty;
        if (directory.Length == 0)
            directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ICCMF");
        return Path.GetFullPath(Path.Combine(directory, "modlist.json"));
    }

    private static void RemoveRegistry(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
            string? directory = Path.GetDirectoryName(path);
            // Only remove the folder if it is now empty (ICCMF_CONFIG_DIR may be shared).
            if (directory != null && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
            Console.WriteLine("Removed the mod list.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("iccmf: could not remove the mod list: " + error.Message);
        }
    }

    private static int RemoveTool()
    {
        string dotnet = ModInstaller.FindDotnet();
        string manual = $"dotnet tool uninstall --global {ToolPackage}";

        if (OperatingSystem.IsWindows())
        {
            // Windows cannot delete files of a running program, so a helper waits for us to exit first.
            string script = Path.Combine(Path.GetTempPath(), "iccmf-uninstall-" + Guid.NewGuid().ToString("N") + ".cmd");
            File.WriteAllText(script, string.Join("\r\n",
                "@echo off",
                "ping -n 3 127.0.0.1 >nul",
                $"\"{dotnet}\" tool uninstall --global {ToolPackage}",
                "del \"%~f0\"",
                ""), new UTF8Encoding(false));

            var start = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add(script);
            Process.Start(start);

            Console.WriteLine("iccmf is removing itself and will be gone in a few seconds.");
            Console.WriteLine("If 'iccmf' is still there afterwards, run: " + manual);
            return 0;
        }

        var uninstall = new ProcessStartInfo { FileName = dotnet, UseShellExecute = false };
        uninstall.ArgumentList.Add("tool");
        uninstall.ArgumentList.Add("uninstall");
        uninstall.ArgumentList.Add("--global");
        uninstall.ArgumentList.Add(ToolPackage);
        using Process process = Process.Start(uninstall)
            ?? throw new CliException("Could not start dotnet. Remove the tool manually with: " + manual);
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new CliException("Could not remove the iccmf tool itself. Run: " + manual);

        Console.WriteLine("ICCMF has been removed.");
        return 0;
    }
}