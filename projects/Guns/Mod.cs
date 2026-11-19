using ISOCORE.ModSdk;
using UnityEngine;

namespace Guns
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
        private ContentItem _workbench;
        private Sprite _tracer;
        private Sprite _rocket;

        protected override void OnModStart()
        {
            LoadTextures();
            CreateWorkbench();
            CreateWeapons();

            Log.LogInfo("Weapons, ammunition, and Weapons Workbench registered.");
        }

        private void LoadTextures()
        {
            _tracer = Assets.Sprite("tracer", 48f);
            _rocket = Assets.Sprite("rocket", 48f);
        }

        private void CreateWorkbench()
        {
            _workbench = Content
                .Placeable("weapons-workbench", "Weapons Workbench")
                .Description("Used to craft weapons and ammunition.")
                .Icon(Assets.Sprite("workbench", 48f))
                .Clone(Items.CraftingTable)
                .PlacedSprite(Assets.Sprite("workbench-placed", 16f))
                .Register();

            Content.Recipe(_workbench)
                .Ingredient(Items.wood, 12)
                .Ingredient(Items.Iron, 4)
                .Ingredient(Items.stone, 6)
                .At(Items.CraftingTable)
                .Register();
        }

        private void CreateWeapons()
        {
            CreateWeapon(
                key: "pistol",
                name: "Pistol",
                ammoKey: "pistol-rounds",
                ammoName: "Pistol Rounds",
                damage: 13,
                range: 8f,
                cooldown: .32f,
                automatic: false,
                ammoYield: 16,
                first: Items.Iron,
                firstAmount: 2,
                second: Items.Copper,
                secondAmount: 1,
                third: Items.wood,
                thirdAmount: 1,
                shot: DefaultSound.Pistol);

            CreateWeapon(
                key: "smg",
                name: "MP5 SMG",
                ammoKey: "smg-rounds",
                ammoName: "MP5 Rounds",
                damage: 9,
                range: 10f,
                cooldown: .10f,
                automatic: true,
                ammoYield: 30,
                first: Items.Iron,
                firstAmount: 4,
                second: Items.Copper,
                secondAmount: 2,
                third: Items.Leather,
                thirdAmount: 1,
                shot: DefaultSound.Smg);

            CreateWeapon(
                key: "rifle",
                name: "AR-15 Assault Rifle",
                ammoKey: "rifle-rounds",
                ammoName: "AR-15 Rounds",
                damage: 17,
                range: 14f,
                cooldown: .17f,
                automatic: true,
                ammoYield: 24,
                first: Items.Steel,
                firstAmount: 4,
                second: Items.Iron,
                secondAmount: 2,
                third: Items.wood,
                thirdAmount: 2,
                shot: DefaultSound.Rifle);

            CreateWeapon(
                key: "sniper",
                name: "Barrett M82 Sniper Rifle",
                ammoKey: "sniper-rounds",
                ammoName: "M82 Rounds",
                damage: 62,
                range: 24f,
                cooldown: 1.15f,
                automatic: false,
                ammoYield: 8,
                first: Items.Steel,
                firstAmount: 8,
                second: Items.Glass,
                secondAmount: 2,
                third: Items.Leather,
                thirdAmount: 2,
                shot: DefaultSound.Sniper,
                steelAmmo: true);

            CreateWeapon(
                key: "bazooka",
                name: "M1A1 Bazooka",
                ammoKey: "rockets",
                ammoName: "M1A1 Rockets",
                damage: 52,
                range: 16f,
                cooldown: 1.35f,
                automatic: false,
                ammoYield: 3,
                first: Items.Steel,
                firstAmount: 6,
                second: Items.Copper,
                secondAmount: 2,
                third: Items.wood,
                thirdAmount: 2,
                shot: DefaultSound.Rocket,
                bazooka: true);
        }

        private void CreateWeapon(
            string key,
            string name,
            string ammoKey,
            string ammoName,
            int damage,
            float range,
            float cooldown,
            bool automatic,
            int ammoYield,
            Items first,
            int firstAmount,
            Items second,
            int secondAmount,
            Items third,
            int thirdAmount,
            DefaultSound shot,
            bool steelAmmo = false,
            bool bazooka = false)
        {
            ContentItem ammo = Content.Item(ammoKey, ammoName)
                .Description("Ammunition for the " + name + ".")
                .Icon(Assets.Sprite(ammoKey, 48f))
                .Register();

            ContentItem weapon = Content.Item(key, name)
                .Description("Uses " + ammoName + ". Craft at the Weapons Workbench.")
                .Icon(Assets.Sprite(key, 48f))
                .NonStackable()
                .Register();

            Content.Recipe(weapon)
                .Ingredient(first, firstAmount)
                .Ingredient(second, secondAmount)
                .Ingredient(third, thirdAmount)
                .At(_workbench)
                .Register();

            CreateAmmoRecipe(ammo, ammoYield, steelAmmo, bazooka);

            WeaponBuilder attack = Combat.Weapon(weapon)
                .Ammo(ammo)
                .Damage(damage)
                .Range(range)
                .Cooldown(cooldown)
                .Automatic(automatic)
                .FireSound(Audio.Default(shot))
                .EmptySound(Audio.Default(DefaultSound.EmptyClick));

            if (bazooka)
            {
                attack
                    .Projectile(_rocket, speed: 12f, scale: 1f)
                    .Blast(radius: 2f, falloff: .75f)
                    .ImpactSound(Audio.Default(DefaultSound.Explosion));
            }
            else
            {
                attack.Projectile(_tracer, speed: 28f, scale: .65f);
            }

            attack.Register();
        }

        private void CreateAmmoRecipe(
            ContentItem ammo,
            int amount,
            bool steelAmmo,
            bool bazooka)
        {
            RecipeBuilder recipe = Content.Recipe(ammo, amount);

            if (bazooka)
            {
                recipe
                    .Ingredient(Items.Steel, 1)
                    .Ingredient(Items.coal, 3)
                    .Ingredient(Items.Copper, 1);
            }
            else
            {
                recipe
                    .Ingredient(Items.Copper, 1)
                    .Ingredient(Items.coal, 1)
                    .Ingredient(steelAmmo ? Items.Steel : Items.Iron, 1);
            }

            recipe.At(_workbench).Register();
        }
    }
}