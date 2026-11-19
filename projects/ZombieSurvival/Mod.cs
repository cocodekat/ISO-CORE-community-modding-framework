using ISOCORE.ModSdk;
using UnityEngine;

namespace ZombieSurvival
{
    public sealed class Mod : IMod
    {
        public void Load(ModContext mod) { mod.Start<Zombies>(); }
    }

    public sealed class Zombies : ModComponent
    {
        protected override void OnModStart()
        {
            // Five rows, eight frames per row: idle, walk, attack, hurt and death/burning.
            Sprite[] sheet = Assets.SpriteGrid("zombies", columns: 8, rows: 5,
                pixelsPerUnit: 64f, pivot: new Vector2(.5f, .06f));
            Sprite goo = Assets.Sprite("goo-glob", 16f);
            Sprite puddle = Assets.Sprite("goo-pool", 16f, new Vector2(.5f, .5f));

            EntityType exploder = Zombie("exploder", "Exploder", sheet, row: 0)
                .Health(18).Speed(2.35f).Scale(.48f)
                .Melee(2, every: .9f).BreakDefenses(7)
                .Drops(Items.coal, chance: .65f)
                .OnDeath(zombie => Combat.DamageRadius(zombie.GameObject.transform.position,
                    radius: 2.2f, damage: 18, source: zombie.GameObject))
                .Register();

            EntityType tank = Zombie("tank", "Armored Tank", sheet, row: 1)
                .Health(110).Speed(.78f).Scale(.58f)
                .Melee(4, every: 1.8f).BreakDefenses(24, every: 1.25f)
                .Drops(Items.Iron, chance: .7f, minimum: 1, maximum: 2)
                .Register();

            EntityType walker = Zombie("walker", "Walker", sheet, row: 2)
                .Health(42).Speed(1.32f).Scale(.45f)
                .Melee(2, every: 1.3f).BreakDefenses(9)
                .Drops(Items.wood, chance: .5f)
                .Drops(Items.Berries, chance: .25f)
                .Register();

            EntityType pack = Zombie("pack", "Pack Zombie", sheet, row: 3)
                .Health(20).Speed(1.55f).Scale(.43f)
                .Melee(1, every: 1.05f).BreakDefenses(5)
                .Groups(minimum: 2, maximum: 4, approachSpeed: 1.15f)
                .Drops(Items.wood, chance: .35f)
                .Register();

            EntityType spitter = Zombie("spitter", "Goo Spitter", sheet, row: 4)
                .Health(34).Speed(1.05f).Scale(.46f)
                .Melee(1, every: 1.5f).BreakDefenses(4)
                .Ranged(goo, damage: 0, range: 8f, every: 4.4f, speed: 8f)
                .LeavesHazard(puddle, damage: 1, duration: 8f, every: 1.25f, radius: .7f)
                .Drops(Items.coal, chance: .35f)
                .Register();

            Entities.NightRaid("The undead")
                .Add(exploder, weight: 12)
                .Add(tank, weight: 9)
                .Add(walker, weight: 35)
                .Add(pack, weight: 30)
                .Add(spitter, weight: 14)
                .PerNight(16)
                .MaxAlive(12)
                .SpawnDistance(12, 23)
                .SpawnEvery(4.5f)
                .CanSkipAt(Items.FirePit, key: KeyCode.K, range: 3f)
                .OnStarted(() => InGameComunication.ErrorToPlayer(
                    "Nightfall. The undead are coming. Defend yourself or rest by a fire [K]."))
                .Start();

            Log.LogInfo("Zombie entity types and nighttime raid registered.");
        }

        private EntityBuilder Zombie(string key, string name, Sprite[] sheet, int row)
        {
            return Entities.Define(key, name, EntityAnimation.EightFrames(sheet, row))
                .HoverOutline(Color.white)
                .VanillaDeathSound()
                .VaultOver(Items.grassBlock, Items.dirtBlock)
                .VaultOverObjects("log", "fallen tree")
                .BurnsInDaylight(damage: 4, every: 1.5f);
        }
    }
}
