# ISO-CORE Mod SDK

Write a C# class with a `Load` method and run `dotnet build`. The SDK locates the game's assemblies, generates the BepInEx entry class, and compiles your mod and any enabled SDK helpers into one DLL.

## ICCMF terminal commands

Install the command-line tool once:

```sh
./install-cli.sh
```

On Windows, extract the release archive and run `install-cli.bat`. The complete setup and project
workflow is in [WINDOWS-GUIDE.md](WINDOWS-GUIDE.md).

The installer checks `PATH` first and then `$HOME/.dotnet/dotnet`. You can override detection with `DOTNET_BIN=/path/to/dotnet`.

The **ISO CORE Community Modding Framework** command is then available as `iccmf`:

```text
iccmf update
iccmf modlist
iccmf modlist add <mod_name> <path/to/mod>
iccmf modlist update <mod_name> <path/to/mod>
iccmf modlist remove <mod_name>
iccmf create mod <mod_name>
```

`modlist add` builds source projects when necessary and installs each mod into its own managed folder under `BepInEx/plugins/ICCMF/`. `modlist update` safely stages and replaces that installed folder, while `modlist remove` removes it from both the game and the registry. A direct DLL or packaged mod directory can also be supplied. Set `ISOCORE_GAME_DIR` when the game is outside the normal Steam location.

`create mod` creates a new folder in the current directory with a starter project, a private copy of the SDK, and an OS-specific build script: `build.sh` on macOS/Linux or `build.bat` on Windows. Updating ICCMF and BepInEx is intentionally a placeholder for now.

## Try the inventory editor

1. Install BepInEx 5 for the ISO-CORE Playtest and quit the game. On Steam, use **Browse local files** to open the game folder.
2. Copy `dist/ISOCORE.SimpleInventoryEditor.dll` into the game's `BepInEx/plugins/` directory. If upgrading from the old runtime SDK, remove its `ISOCORE.ModApi.dll` from `BepInEx/plugins/` and move any earlier inventory editor packages out of the game's `mods/` directory. For an isolated test, also move any other inventory editor DLL out of the plugins directory.
3. Launch through BepInEx and open the game's normal inventory. Click **Edit inventory [F8]** (or press F8), then click the native inventory slot you want to change. Search the complete item catalog and click **Apply**. **Clear** empties the selected slot.

A successful startup should show `Loading [Simple Inventory Editor 1.0.0]` in `BepInEx/LogOutput.log`.

## Start a mod

With the .NET 8 SDK or newer and a local BepInEx-enabled game installation, create a project with ICCMF:

```sh
iccmf create mod MyMod
cd MyMod
./build.sh
```

On Windows, run `build.bat` instead. If Steam uses a custom library, set `ISOCORE_GAME_DIR`
to the directory that contains the ISO-CORE app or executable and the `BepInEx` folder.

In `MyMod.csproj`, set your own `IsoCoreModId`, `IsoCoreModName`, and `IsoCoreModVersion`.
If you rename `MyMod.Mod` in C#, update `IsoCoreModEntry` too. Install it with
`iccmf modlist add MyMod /path/to/MyMod`; later builds use `modlist update`.

### What mod code can use

- `mod.Log` and `mod.Config` expose BepInEx logging and persistent settings.
- `mod.AddComponent<T>()` attaches a Unity component for behavior that needs the game loop.
- `mod.Start<T>()` starts a persistent `ModComponent` after the first scene load. The component
  automatically receives `Assets`, `Content`, `Combat`, `Paths`, `World`, `Log`, and `Config`
  (plus enabled optional APIs); guards lifecycle callbacks;
  records diagnostics; and releases subscriptions registered through its helper methods.
- `mod.Assets` loads embedded PNGs from `Assets/` with pixel-perfect filtering. A matching file
  under `BepInEx/config/<mod-id>.Assets/` can override art without recompiling.
- `mod.Content` registers items, placeables, and recipes. IDs are stable and automatically derived
  from the mod ID and item key, with collision checks instead of manually chosen enum values.
- `mod.Combat` provides damageable objects, reusable attacks, custom item weapons, blast damage,
  and visible projectiles.
- `mod.Paths` provides bounded A* and attachable terrain-aware agents. `mod.World` combines sprites,
  health, pathfinding, attacks, damage, and projectiles into a short actor builder.
- `mod.Entities` defines animated custom entities, melee and ranged attacks, hazards, weighted
  loot, selective vaulting, defense breaking, groups, daylight damage and scheduled night raids.
  See `projects/ZombieSurvival/` for five concise examples.
- Add `<IsoCoreInventory>true</IsoCoreInventory>` to the project's property group to get `mod.Inventory`. It exposes the complete item catalog (including availability and icons), native inventory lifecycle and slot-click events, slot snapshots, and validated slot writes. The helper is compiled into the mod DLL.
- Add `<IsoCoreUi>true</IsoCoreUi>` to get `mod.Ui`, a runtime uGUI menu builder with resolution scaling, ISO-CORE font detection, themed labels, buttons and inputs, icon-and-metadata list rows, responsive rows and columns, isolated overlays, and correctly wired scroll lists. Create menus after the first scene has loaded so the game's startup transition does not remove them.
- The project can also call Unity, Harmony, and ISO-CORE's game classes directly using the references supplied by the SDK.

### Custom content and combat

Place PNG files in the generated project's `Assets/` folder. ICCMF embeds them automatically.
The SDK assigns deterministic IDs, publishes the game assets and prefabs, and supplies custom
names everywhere the base game displays them:

```csharp
ContentItem bullets = Content.Item("pistol-rounds", "Pistol Rounds")
    .Description("Ammo for the Pistol.")
    .Icon(Assets.Sprite("pistol-rounds", 48f))
    .Register();

ContentItem pistol = Content.Item("pistol", "Pistol")
    .Description("A compact ranged weapon.")
    .Icon(Assets.Sprite("pistol", 48f))
    .NonStackable()
    .Register();

ContentItem bench = Content.Placeable("weapons-workbench", "Weapons Workbench")
    .Icon(Assets.Sprite("workbench", 48f))
    .Clone(Items.CraftingTable)
    .PlacedSprite(Assets.Sprite("workbench-placed", 16f))
    .Register();

Content.Recipe(pistol)
    .Ingredient(Items.Iron, 2)
    .Ingredient(Items.Copper, 1)
    .At(bench)
    .Register();

Combat.Weapon(pistol)
    .Ammo(bullets)
    .Damage(13)
    .Range(8f)
    .Cooldown(.32f)
    .Projectile(Assets.Sprite("tracer", 48f), speed: 24f)
    .Register();
```

The automatic ID comes from `<mod-id>:<key>`, so it is stable between builds and different mods
can safely use the same short key. `.WithId(number)` remains available when compatibility with an
existing save or old mod requires a specific ID.

### Actors, pathfinding, damage, and projectiles

For common enemies and NPCs, `World.Actor` composes the lower-level systems:

```csharp
GameObject spitter = World.Actor("Spitter", Assets.Sprite("spitter"), spawnCell)
    .Health(32)
    .PathfindToPlayer(speed: 1.75f, stopWithin: 4)
    .AttackPlayer(damage: 2, every: 1.5f, range: 6f)
    .Projectile(Assets.Sprite("goo"), speed: 8f)
    .OnHit(hit => Log.LogInfo("Damage: " + hit.Damage.Amount))
    .Spawn();
```

For unusual behavior, use the same pieces independently: `Combat.MakeDamageable`,
`Combat.Damage`, `Combat.Attack`, `Combat.Projectile`, `Combat.DamageRadius`,
`Paths.TerrainAgent`, or the callback-driven `Paths.Find`. Every builder exposes callbacks or a
`Configure` escape hatch, so the simple route does not prevent custom mechanics.

The complete compiling example is in `projects/SdkShowcase/`.

### Simple UI controls

Standalone controls are definitions until `Show` is called. Most styling is optional, named
arguments keep call sites readable, and the underlying uGUI objects remain available through
`Rect`, `Component`, and `Label` when a mod needs lower-level customization.

```csharp
ModButton edit = mod.Ui.Button("EDIT SLOT", fontSize: 17f,
        size: new Vector2(210f, 38f), roundedEdges: 7f)
    .OnClick(OpenEditor);

if (mod.Inventory.IsOpen)
    edit.Show(UiAnchor.TopRight, new Vector2(-20f, -50f));
else
    edit.Hide();

ModText message = mod.Ui.Text("hello", fontSize: 24f);
message.Show(UiAnchor.Center);
```

`Show` also accepts a custom normalized anchor as a `Vector2`. Buttons additionally accept a
font, border color, background color, border thickness, corner radius, and sprite texture.
Controls can be updated with `SetText`, `SetSize`, and `SetBackground`, or removed with `Destroy`.

Menus use the same controls through composable containers:

```csharp
ModMenu editor = null;
editor = mod.Ui.Menu("Edit slot", new Vector2(560, 700), content =>
{
    var column = content.Column(spacing: 8).Fill();
    column.Text("Choose an item", fontSize: 20, height: 46);
    column.Input("Search name or item ID", height: 40, changed: Search);
    column.ScrollList("Items", height: 280);

    var actions = column.Row(spacing: 10, expandHeight: true).Layout(40);
    actions.Button("APPLY", Apply, ButtonTone.Primary, height: 40);
    actions.Button("CANCEL", editor.Close, height: 40);
});
```

`ModContainer.Rect` exposes the generated container for custom uGUI work. The original
`AddLabel`, `AddButton`, and related builder methods remain available as the lower-level escape
hatch; both APIs produce the same themed controls.

### Mod components and bindings

```csharp
public sealed class InventoryControls : ModComponent
{
    protected override void OnModStart()
    {
        var label = Ui.State("EDIT SLOT");

        Ui.Button("EDIT SLOT")
            .BindText(label)
            .OnClick(GuardUi(OpenEditor))
            .VisibleWhen(() => Inventory.IsOpen, UiAnchor.TopRight)
            .Hotkey(Key.F8);

        WhenInventoryClosed(() => label.Value = "EDIT SLOT");
        WhenSlotClicked(OnSlotClicked);
    }
}

public void Load(ModContext mod)
{
    mod.Start<InventoryControls>();
}
```

`UiState<T>` can bind text and typed inputs, so changing `state.Value` updates the UI and editing
the input updates the state. `ModComponent` automatically unsubscribes callbacks registered with
`WhenInventoryOpened`, `WhenInventoryClosed`, and `WhenSlotClicked`. Set
`mod.Diagnostics.Verbose = false` to hide lifecycle trace messages; errors remain enabled.

### Direct inventory access

Slots are live handles. Numbered access is one-based, matching the slot numbers shown to players:

```csharp
InventorySlot equipped = Inventory.Equipped();
InventorySlot first = Inventory.Slot1; // same as Inventory.Slot(1) or Inventory[1]

string name = first.Name;
int id = first.Id;
int quantity = first.Quantity;

first.Quantity = 5;
first.Id = 78;
first.Set(itemId: 78, quantity: 5, durability: 20); // atomic update
first.Clear();
```

Invalid item IDs, stack sizes, durability, or stale inventory handles throw an
`InvalidOperationException` with a player-readable explanation. Use `Set` when changing an item
and its quantity together, because assigning `Id` preserves the slot's current quantity.

## Build the inventory example

```sh
./projects/InventoryEditor/build.sh
```

Its DLL is under `projects/InventoryEditor/bin/Release/net472/`. In `Mod.cs`, the mod uses
`AddPersistentComponentAfterSceneLoad` so it does not need to manage Unity scene events or a
persistent host itself. The example uses the SDK's simple controls and menu builder while the
inventory helper handles reading, validating, writing, and refreshing native slots.
