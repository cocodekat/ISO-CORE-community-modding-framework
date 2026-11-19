# ICCMF on Windows

This guide covers installing ICCMF, creating a mod, building the example projects, and installing
mods into ISO-CORE. ICCMF 0.5.2 targets the **ISO-CORE Playtest** and BepInEx 5.

## 1. Install the prerequisites

You need:

1. ISO-CORE Playtest installed through Steam.
2. The x64 **.NET 8 SDK** or newer. Install the SDK, not only the desktop runtime. Open a new
   Command Prompt and verify it with `dotnet --info`.
3. BepInEx 5 installed in the ISO-CORE game directory. Start the game once after installing
   BepInEx, close it, and confirm that `BepInEx\plugins` and `BepInEx\LogOutput.log` exist.
4. The extracted `ICCMF-0.5.2-windows` folder. Do not run tools from inside the ZIP file.

The normal Steam game directory is:

```text
C:\Program Files (x86)\Steam\steamapps\common\ISO-CORE Playtest
```

If the game is in another Steam library, set its location once, then open a new terminal:

```bat
setx ISOCORE_GAME_DIR "D:\SteamLibrary\steamapps\common\ISO-CORE Playtest"
```

`ISOCORE_GAME_DIR` must point at the folder containing both the game executable and `BepInEx`.

## 2. Install the ICCMF command

Double-click `install-cli.bat`, or run it from Command Prompt:

```bat
cd /d C:\path\to\ICCMF-0.5.2-windows
install-cli.bat
```

Open a new Command Prompt and check the installation:

```bat
iccmf --version
```

It should print `iccmf 0.5.2`. If Windows says the command is unknown, add this directory to your
user `Path`, then open another terminal:

```text
%USERPROFILE%\.dotnet\tools
```

## 3. Create your first project

Choose a folder where you keep source code, then create a mod:

```bat
mkdir C:\ISOCORE-Mods
cd /d C:\ISOCORE-Mods
iccmf create mod MyFirstMod
cd MyFirstMod
build.bat
```

The generated project contains:

- `Mod.cs`: your mod's C# code.
- `MyFirstMod.csproj`: its ID, display name, version, and enabled optional APIs.
- `Assets\`: PNG files embedded into the mod.
- `.iccmf\Sdk\`: the SDK source used by this project.
- `build.bat`: the Windows build command.

The compiled DLL is written to:

```text
bin\Release\net472\MyFirstMod.dll
```

The generated starter waits for the first loaded scene and starts a persistent `ModComponent`.
Put initialization in `OnModStart`; use `Update` only for behavior that genuinely runs every frame:

```csharp
using ISOCORE.ModSdk;
using UnityEngine;

namespace MyFirstMod
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
            Log.LogInfo("My first mod started!");
        }
    }
}
```

Build again after editing:

```bat
build.bat
```

## 4. Install, update, and remove a mod

Fully close ISO-CORE before replacing DLLs on Windows. From any directory, install the project
folder—not its `bin` folder:

```bat
iccmf modlist add MyFirstMod "C:\ISOCORE-Mods\MyFirstMod"
```

`modlist add` builds the project and installs its DLL under
`BepInEx\plugins\ICCMF\MyFirstMod`. After changing the source, use `update`:

```bat
iccmf modlist update MyFirstMod "C:\ISOCORE-Mods\MyFirstMod"
```

Other useful commands are:

```bat
iccmf modlist
iccmf modlist remove MyFirstMod
```

You can also install a compiled DLL directly:

```bat
iccmf modlist add MyCompiledMod "C:\path\to\MyCompiledMod.dll"
```

The name after `add` or `update` is ICCMF's install-list name. It does not need to match the DLL
filename, but use one consistent name so later updates find the same entry.

## 5. Build and install the included projects

Open Command Prompt in the extracted ICCMF folder. Each example has a Windows build script:

```bat
projects\InventoryEditor\build.bat
projects\Guns\build.bat
projects\ZombieSurvival\build.bat
```

Install their source folders so ICCMF can rebuild them when needed:

```bat
iccmf modlist add InventoryEditor "%CD%\projects\InventoryEditor"
iccmf modlist add Guns "%CD%\projects\Guns"
iccmf modlist add ZombieSurvival "%CD%\projects\ZombieSurvival"
```

If an entry already exists, replace `add` with `update`. The release archive also contains
`example-dlls\`; those are convenient for testing, but the source folders are better while
developing.

## 6. Add optional APIs and assets

Add either property inside the main `<PropertyGroup>` in your `.csproj` when needed:

```xml
<IsoCoreInventory>true</IsoCoreInventory>
<IsoCoreUi>true</IsoCoreUi>
```

Then rebuild. The core APIs—assets, content, combat, pathfinding, world actors, and custom
entities—are available without extra switches.

Put PNG files in `Assets\`. Load `Assets\my-icon.png` as:

```csharp
Sprite icon = Assets.Sprite("my-icon", pixelsPerUnit: 48f);
```

The main `README.md` contains examples for custom items, placeables, recipes, weapons,
projectiles, entities, inventory access, and UI.

## 7. Diagnose a failed build or startup

- **`dotnet` is not recognized:** install the .NET SDK and reopen Command Prompt.
- **Game assemblies were not found:** set `ISOCORE_GAME_DIR` to the correct game directory.
- **BepInEx plugins directory was not found:** install BepInEx 5 and launch the game once.
- **A DLL cannot be replaced:** close ISO-CORE completely, including any remaining process.
- **The mod builds but does not load:** inspect `BepInEx\LogOutput.log`. It should contain a line
  such as `Loading [Your Mod 1.0.0]` followed by the mod's lifecycle diagnostics.
- **A project uses an old SDK copy:** newly generated projects embed `.iccmf\Sdk`. Create a fresh
  project with the current ICCMF version and compare that folder before migrating custom code.

The `iccmf update` command is still a placeholder; it does not update ICCMF or BepInEx yet.

## 8. Build a Windows release of ICCMF itself

Maintainers can build the CLI, all examples, and a distributable ZIP from PowerShell:

```powershell
powershell -ExecutionPolicy Bypass -File .\build-windows-release.ps1
```

The result is `dist\windows\ICCMF-0.5.2-windows.zip`. The script expects a working ISO-CORE and
BepInEx installation because the example projects compile against the game's managed assemblies.
Use `-SkipExampleBuilds` only when packaging source and the CLI without prebuilt example DLLs.

