using System.Diagnostics;
using System.Reflection;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ICCMF.Cli;

internal static class Program
{
    private const string ProductName = "ISO CORE Community Modding Framework";
    private const string Version = "0.5.2";
    public static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (CliException error)
        {
            Error(error.Message);
            return error.ExitCode;
        }
        catch (Exception error)
        {
            Error("Unexpected failure: " + error.Message);
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintHelp();
            return 0;
        }

        return args[0].ToLowerInvariant() switch
        {
            "--version" or "-v" or "version" => PrintVersion(),
            "update" => Update(args),
            "modlist" => ModList(args),
            "create" => Create(args),
            _ => throw new CliException($"Unknown command '{args[0]}'. Run 'iccmf --help' for usage.", 2)
        };
    }

    private static int PrintVersion()
    {
        Console.WriteLine($"iccmf {Version}");
        return 0;
    }

    private static int Update(string[] args)
    {
        RequireArgumentCount(args, 1, "iccmf update");
        Console.WriteLine("ICCMF and BepInEx updating is not implemented yet.");
        Console.WriteLine("Nothing was changed.");
        return 0;
    }

    private static int ModList(string[] args)
    {
        ModRegistry registry = ModRegistry.Load();
        if (args.Length == 1 || args[1].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            RequireArgumentCount(args, args.Length == 1 ? 1 : 2, "iccmf modlist [list]");
            PrintModList(registry);
            return 0;
        }

        string action = args[1].ToLowerInvariant();
        switch (action)
        {
            case "add":
                RequireArgumentCount(args, 4, "iccmf modlist add <mod_name> <path/to/mod>");
                string addSource = ResolveExistingPath(args[3]);
                registry.EnsureMissing(args[2]);
                string addInstalled = ModInstaller.Install(args[2], addSource, replace: false);
                registry.Add(args[2], addSource, addInstalled);
                registry.Save();
                Console.WriteLine($"Added and installed '{args[2]}'.");
                Console.WriteLine(addInstalled);
                return 0;

            case "update":
                RequireArgumentCount(args, 4, "iccmf modlist update <mod_name> <path/to/mod>");
                registry.EnsurePresent(args[2]);
                string updateSource = ResolveExistingPath(args[3]);
                string updateInstalled = ModInstaller.Install(args[2], updateSource, replace: true);
                registry.Update(args[2], updateSource, updateInstalled);
                registry.Save();
                Console.WriteLine($"Updated and reinstalled '{args[2]}'.");
                Console.WriteLine(updateInstalled);
                return 0;

            case "remove":
                RequireArgumentCount(args, 3, "iccmf modlist remove <mod_name>");
                registry.EnsurePresent(args[2]);
                ModInstaller.Uninstall(args[2]);
                registry.Remove(args[2]);
                registry.Save();
                Console.WriteLine($"Removed '{args[2]}' from the game and mod list.");
                return 0;

            default:
                throw new CliException($"Unknown modlist action '{args[1]}'. Expected add, update, remove, or list.", 2);
        }
    }

    private static int Create(string[] args)
    {
        if (args.Length < 2 || !args[1].Equals("mod", StringComparison.OrdinalIgnoreCase))
            throw new CliException("Usage: iccmf create mod <mod_name>", 2);
        RequireArgumentCount(args, 3, "iccmf create mod <mod_name>");

        string requestedName = args[2].Trim();
        ValidateModName(requestedName);
        string directory = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, requestedName));
        if (Directory.Exists(directory) || File.Exists(directory))
            throw new CliException($"Cannot create mod: '{directory}' already exists.", 3);

        string identifier = ToIdentifier(requestedName);
        string slug = Regex.Replace(requestedName.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        string displayName = Regex.Replace(requestedName.Replace('_', ' ').Replace('-', ' '), @"\s+", " ").Trim();
        string projectName = requestedName + ".csproj";
        string buildCommand;

        try
        {
            Directory.CreateDirectory(directory);
            Directory.CreateDirectory(Path.Combine(directory, ".iccmf", "Sdk"));
            Directory.CreateDirectory(Path.Combine(directory, "Assets"));
            ExtractSdk(Path.Combine(directory, ".iccmf", "Sdk"));
            File.WriteAllText(Path.Combine(directory, projectName), ProjectFile(requestedName,
                displayName, identifier, slug), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(directory, "Mod.cs"), ModFile(identifier),
                new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(directory, ".gitignore"),
                "bin/\nobj/\n.DS_Store\n", new UTF8Encoding(false));
            buildCommand = WriteBuildScript(directory, projectName);
        }
        catch
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            throw;
        }

        Console.WriteLine($"Created mod '{displayName}' at:");
        Console.WriteLine(directory);
        Console.WriteLine();
        Console.WriteLine("Build it with:");
        Console.WriteLine("  " + buildCommand);
        return 0;
    }

    private static string WriteBuildScript(string directory, string projectName)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(Path.Combine(directory, "build.bat"),
                WindowsBuildScript(projectName) + Environment.NewLine, new UTF8Encoding(false));
            return "build.bat";
        }

        string path = Path.Combine(directory, "build.sh");
        File.WriteAllText(path, UnixBuildScript(projectName) + Environment.NewLine,
            new UTF8Encoding(false));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite |
            UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return "./build.sh";
    }

    private static string UnixBuildScript(string projectName) => $$"""
        #!/bin/sh
        set -eu

        SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)

        if [ -n "${DOTNET_BIN:-}" ]; then
          :
        elif command -v dotnet >/dev/null 2>&1; then
          DOTNET_BIN=$(command -v dotnet)
        elif [ -x "$HOME/.dotnet/dotnet" ]; then
          DOTNET_BIN="$HOME/.dotnet/dotnet"
        else
          printf '%s\n' "Could not find dotnet." >&2
          exit 127
        fi

        "$DOTNET_BIN" build "$SCRIPT_DIR/{{projectName}}" -c Release "$@"
        """;

    private static string WindowsBuildScript(string projectName) => $$"""
        @echo off
        setlocal
        set "SCRIPT_DIR=%~dp0"

        if defined DOTNET_BIN goto build
        where dotnet >nul 2>nul
        if not errorlevel 1 (
          set "DOTNET_BIN=dotnet"
          goto build
        )
        if exist "%USERPROFILE%\.dotnet\dotnet.exe" (
          set "DOTNET_BIN=%USERPROFILE%\.dotnet\dotnet.exe"
          goto build
        )

        echo Could not find dotnet. 1>&2
        exit /b 127

        :build
        "%DOTNET_BIN%" build "%SCRIPT_DIR%{{projectName}}" -c Release %*
        """;

    private static void ExtractSdk(string destination)
    {
        Assembly assembly = typeof(Program).Assembly;
        string[] files = { "AssetsApi.cs", "CombatApi.cs", "ContentApi.cs", "EntityApi.cs",
            "InventoryApi.cs", "IsoCore.Mod.targets", "MenuApi.cs", "ModContext.cs",
            "PathfindingApi.cs", "WorldApi.cs" };
        foreach (string file in files)
        {
            string resourceName = "ICCMF.Sdk." + file;
            using Stream input = assembly.GetManifestResourceStream(resourceName)
                ?? throw new CliException($"The installed ICCMF package is missing {file}.");
            using FileStream output = File.Create(Path.Combine(destination, file));
            input.CopyTo(output);
        }
    }

    private static string ProjectFile(string projectName, string displayName, string identifier,
        string slug)
    {
        string escapedDisplay = Xml(displayName);
        return $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <IsoCoreModId>community.isocore.{Xml(slug)}</IsoCoreModId>
                <IsoCoreModName>{escapedDisplay}</IsoCoreModName>
                <IsoCoreModVersion>1.0.0</IsoCoreModVersion>
                <IsoCoreModEntry>{Xml(identifier)}.Mod</IsoCoreModEntry>
              </PropertyGroup>
              <Import Project=".iccmf/Sdk/IsoCore.Mod.targets" />
            </Project>
            """;
    }

    private static string ModFile(string identifier) => $$"""
        using ISOCORE.ModSdk;

        namespace {{identifier}}
        {
            public sealed class Mod : IMod
            {
                public void Load(ModContext mod)
                {
                    mod.Start<Main>();
                }
            }

            public sealed class Main : ModComponent
            {
                protected override void OnModStart()
                {
                    // Your mod starts here. Log, Config, and enabled SDK APIs are ready to use.
                }
            }
        }
        """;

    private static void PrintModList(ModRegistry registry)
    {
        if (registry.Mods.Count == 0)
        {
            Console.WriteLine("The mod list is empty.");
            Console.WriteLine($"Stored at: {registry.FilePath}");
            return;
        }

        int width = Math.Max(4, registry.Mods.Max(mod => mod.Name.Length));
        Console.WriteLine($"{"NAME".PadRight(width)}  SOURCE");
        foreach (ModEntry mod in registry.Mods.OrderBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase))
            Console.WriteLine($"{mod.Name.PadRight(width)}  {mod.Path}");
    }

    private static string ResolveExistingPath(string value)
    {
        string path = Path.GetFullPath(value);
        if (!Directory.Exists(path) && !File.Exists(path))
            throw new CliException($"Mod path does not exist: {path}", 3);
        return path;
    }

    private static void ValidateModName(string name)
    {
        if (!Regex.IsMatch(name, "^[A-Za-z][A-Za-z0-9._-]{0,63}$"))
            throw new CliException("Mod names must start with a letter and contain only letters, digits, '.', '_' or '-'.", 2);
    }

    private static string ToIdentifier(string name)
    {
        string[] parts = Regex.Split(name, "[^A-Za-z0-9]+").Where(part => part.Length > 0).ToArray();
        string identifier = string.Concat(parts.Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
        return char.IsDigit(identifier[0]) ? "Mod" + identifier : identifier;
    }

    private static string Xml(string value) => SecurityElement.Escape(value) ?? value;

    private static bool IsHelp(string value) => value is "--help" or "-h" or "help";

    private static void RequireArgumentCount(string[] args, int expected, string usage)
    {
        if (args.Length != expected) throw new CliException("Usage: " + usage, 2);
    }

    private static void Error(string message)
    {
        Console.Error.WriteLine("iccmf: " + message);
    }

    private static void PrintHelp()
    {
        Console.WriteLine($"{ProductName} ({Version})");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  iccmf update");
        Console.WriteLine("  iccmf modlist [list]");
        Console.WriteLine("  iccmf modlist add <mod_name> <path/to/mod>");
        Console.WriteLine("  iccmf modlist update <mod_name> <path/to/mod>");
        Console.WriteLine("  iccmf modlist remove <mod_name>");
        Console.WriteLine("  iccmf create mod <mod_name>");
        Console.WriteLine();
        Console.WriteLine("Environment:");
        Console.WriteLine("  ICCMF_CONFIG_DIR  Override the directory containing modlist.json.");
        Console.WriteLine("  ISOCORE_GAME_DIR  Override the ISO-CORE installation directory.");
    }
}

internal static class ModInstaller
{
    public static string Install(string name, string source, bool replace)
    {
        string root = ManagedRoot();
        Directory.CreateDirectory(root);
        string target = TargetPath(root, name);
        if (!replace && Directory.Exists(target))
            throw new CliException($"A managed mod folder already exists: {target}", 3);

        string staging = Path.Combine(root, ".staging-" + Guid.NewGuid().ToString("N"));
        string? backup = null;
        Directory.CreateDirectory(staging);
        try
        {
            PreparePayload(source, staging);
            if (!Directory.EnumerateFiles(staging, "*.dll", SearchOption.AllDirectories).Any())
                throw new CliException("The mod produced no DLL files to install.", 3);

            if (Directory.Exists(target))
            {
                backup = Path.Combine(root, ".backup-" + Guid.NewGuid().ToString("N"));
                Directory.Move(target, backup);
            }
            Directory.Move(staging, target);
            if (backup != null && Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
            return target;
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            if (backup != null && Directory.Exists(backup) && !Directory.Exists(target))
                Directory.Move(backup, target);
            throw;
        }
    }

    public static void Uninstall(string name)
    {
        string root = ManagedRoot();
        string target = TargetPath(root, name);
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
    }

    private static void PreparePayload(string source, string staging)
    {
        if (File.Exists(source))
        {
            if (!source.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                throw new CliException("A mod file must be a .dll file.", 3);
            File.Copy(source, Path.Combine(staging, Path.GetFileName(source)), overwrite: true);
            return;
        }

        string[] projects = Directory.GetFiles(source, "*.csproj", SearchOption.TopDirectoryOnly);
        if (projects.Length > 1)
            throw new CliException("The mod directory contains multiple project files. Pass a built DLL instead.", 3);
        if (projects.Length == 1)
        {
            string artifact = BuildProject(projects[0]);
            File.Copy(artifact, Path.Combine(staging, Path.GetFileName(artifact)), overwrite: true);
            return;
        }

        string[] topLevelDlls = Directory.GetFiles(source, "*.dll", SearchOption.TopDirectoryOnly);
        if (topLevelDlls.Length == 0)
            throw new CliException("The mod directory contains neither a project nor top-level DLL files.", 3);
        CopyDirectory(source, staging);
    }

    private static string BuildProject(string project)
    {
        string dotnet = FindDotnet();
        Console.WriteLine("Building mod project...");
        var start = new ProcessStartInfo
        {
            FileName = dotnet,
            UseShellExecute = false
        };
        start.ArgumentList.Add("build");
        start.ArgumentList.Add(project);
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("Release");
        start.ArgumentList.Add("--nologo");
        using Process process = Process.Start(start)
            ?? throw new CliException("Could not start dotnet to build the mod.");
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new CliException($"Mod build failed with exit code {process.ExitCode}.", 3);

        string projectDirectory = Path.GetDirectoryName(project)
            ?? throw new CliException("Could not determine the mod project directory.");
        string expectedName = Path.GetFileNameWithoutExtension(project) + ".dll";
        string release = Path.Combine(projectDirectory, "bin", "Release");
        if (!Directory.Exists(release))
            throw new CliException("The mod build succeeded but no Release output directory was found.", 3);
        string? artifact = Directory.GetFiles(release, expectedName, SearchOption.AllDirectories)
            .Where(path => !HasPathSegment(path, "ref"))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (artifact == null)
        {
            string[] candidates = Directory.GetFiles(release, "*.dll", SearchOption.AllDirectories)
                .Where(path => !HasPathSegment(path, "ref"))
                .ToArray();
            if (candidates.Length == 1) artifact = candidates[0];
        }
        return artifact ?? throw new CliException(
            $"Could not find the built mod DLL '{expectedName}' under {release}.", 3);
    }

    private static bool HasPathSegment(string path, string segment)
    {
        string marker = Path.DirectorySeparatorChar + segment + Path.DirectorySeparatorChar;
        return path.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            string output = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.Copy(file, output, overwrite: true);
        }
    }

    private static string FindDotnet()
    {
        string configured = Environment.GetEnvironmentVariable("DOTNET_BIN")?.Trim() ?? string.Empty;
        if (configured.Length > 0 && File.Exists(configured)) return configured;
        string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dotnet", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        if (File.Exists(local)) return local;
        return "dotnet";
    }

    private static string ManagedRoot()
    {
        string game = ResolveGameDirectory();
        string plugins = Path.Combine(game, "BepInEx", "plugins");
        if (!Directory.Exists(plugins))
            throw new CliException($"BepInEx plugins directory was not found: {plugins}", 3);
        return Path.GetFullPath(Path.Combine(plugins, "ICCMF"));
    }

    private static string ResolveGameDirectory()
    {
        string configured = Environment.GetEnvironmentVariable("ISOCORE_GAME_DIR")?.Trim() ?? string.Empty;
        if (configured.Length > 0)
        {
            string resolved = Path.GetFullPath(configured);
            if (!Directory.Exists(resolved))
                throw new CliException($"ISOCORE_GAME_DIR does not exist: {resolved}", 3);
            return resolved;
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string>();
        if (OperatingSystem.IsMacOS())
            candidates.Add(Path.Combine(home, "Library", "Application Support", "Steam", "steamapps",
                "common", "ISO-CORE Playtest"));
        else if (OperatingSystem.IsWindows())
        {
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (programFilesX86.Length > 0) candidates.Add(Path.Combine(programFilesX86, "Steam",
                "steamapps", "common", "ISO-CORE Playtest"));
            if (programFiles.Length > 0) candidates.Add(Path.Combine(programFiles, "Steam",
                "steamapps", "common", "ISO-CORE Playtest"));
        }
        else
        {
            candidates.Add(Path.Combine(home, ".local", "share", "Steam", "steamapps", "common",
                "ISO-CORE Playtest"));
            candidates.Add(Path.Combine(home, ".steam", "steam", "steamapps", "common",
                "ISO-CORE Playtest"));
        }
        return candidates.FirstOrDefault(Directory.Exists)
            ?? throw new CliException("ISO-CORE was not found. Set ISOCORE_GAME_DIR to the game folder.", 3);
    }

    private static string TargetPath(string root, string name)
    {
        string folder = Regex.Replace(name.Trim(), "[^A-Za-z0-9._-]+", "-").Trim('-', '.');
        if (folder.Length == 0) throw new CliException("Mod name cannot form an install folder.", 2);
        string target = Path.GetFullPath(Path.Combine(root, folder));
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new CliException("Refusing to install outside the ICCMF plugins directory.");
        return target;
    }
}

internal sealed class ModRegistry
{
    public List<ModEntry> Mods { get; set; } = new();

    [JsonIgnore]
    public string FilePath { get; private set; } = string.Empty;

    public static ModRegistry Load()
    {
        string configDirectory = Environment.GetEnvironmentVariable("ICCMF_CONFIG_DIR")?.Trim() ?? string.Empty;
        if (configDirectory.Length == 0)
            configDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ICCMF");
        string filePath = Path.GetFullPath(Path.Combine(configDirectory, "modlist.json"));
        if (!File.Exists(filePath)) return new ModRegistry { FilePath = filePath };

        try
        {
            ModRegistry registry = JsonSerializer.Deserialize<ModRegistry>(File.ReadAllText(filePath),
                ProgramJson.Options) ?? new ModRegistry();
            registry.FilePath = filePath;
            registry.Mods ??= new List<ModEntry>();
            return registry;
        }
        catch (JsonException error)
        {
            throw new CliException($"Could not read mod list '{filePath}': {error.Message}");
        }
    }

    public void EnsureMissing(string name)
    {
        ValidateEntryName(name);
        if (Find(name) != null) throw new CliException($"A mod named '{name}' is already in the mod list.", 3);
    }

    public void EnsurePresent(string name)
    {
        ValidateEntryName(name);
        if (Find(name) == null) throw new CliException($"No mod named '{name}' exists in the mod list.", 3);
    }

    public void Add(string name, string path, string installedPath)
    {
        EnsureMissing(name);
        Mods.Add(new ModEntry { Name = name, Path = path, InstalledPath = installedPath });
    }

    public void Update(string name, string path, string installedPath)
    {
        ModEntry entry = Find(name) ?? throw new CliException($"No mod named '{name}' exists in the mod list.", 3);
        entry.Path = path;
        entry.InstalledPath = installedPath;
    }

    public void Remove(string name)
    {
        ModEntry entry = Find(name) ?? throw new CliException($"No mod named '{name}' exists in the mod list.", 3);
        Mods.Remove(entry);
    }

    public ModEntry Get(string name) => Find(name)
        ?? throw new CliException($"No mod named '{name}' exists in the mod list.", 3);

    public void Save()
    {
        string? directory = Path.GetDirectoryName(FilePath);
        if (string.IsNullOrEmpty(directory)) throw new CliException("Could not determine the mod list directory.");
        Directory.CreateDirectory(directory);
        string temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, ProgramJson.Options) + Environment.NewLine,
            new UTF8Encoding(false));
        File.Move(temporary, FilePath, overwrite: true);
    }

    private ModEntry? Find(string name) => Mods.FirstOrDefault(entry =>
        entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static void ValidateEntryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new CliException("Mod name cannot be empty.", 2);
        if (name.IndexOfAny(new[] { '\r', '\n', '\t' }) >= 0)
            throw new CliException("Mod name cannot contain control characters.", 2);
    }
}

internal sealed class ModEntry
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string? InstalledPath { get; set; }
}

internal sealed class CliException : Exception
{
    public CliException(string message, int exitCode = 1) : base(message) => ExitCode = exitCode;
    public int ExitCode { get; }
}

internal static class ProgramJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
