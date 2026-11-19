using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ISOCORE.ModSdk
{
    /// <summary>Damage, reusable attacks, item weapons, and visible projectiles.</summary>
    public sealed class CombatApi
    {
        private static readonly List<CombatApi> Instances = new List<CombatApi>();
        private static readonly FieldInfo HealthDisplayTarget =
            AccessTools.Field(typeof(DisplayPlayerHealth), "playerHurt");
        private const float MobPickRadius = 1.0f;
        private const string DamageableTypeName = "ISOCORE.ModSdk.Damageable";
        private const string EntityRegistryKey = "ISOCORE.ModSdk.LiveEntityObjects.v1";
        private readonly Dictionary<Items, WeaponDefinition> _weapons =
            new Dictionary<Items, WeaponDefinition>();
        private readonly ManualLogSource _log;
        private readonly SdkCoroutineHost _host;
        private bool _triggerWasHeld;

        internal CombatApi(string modId, GameObject host, ManualLogSource log)
        {
            _log = log;
            _host = host.GetComponent<SdkCoroutineHost>() ?? host.AddComponent<SdkCoroutineHost>();
            Instances.Add(this);
            var harmony = new Harmony(modId + ".iccmf.combat");
            harmony.PatchAll(typeof(PlayerAttackPatch));
            try { harmony.PatchAll(typeof(MobDeathProbe)); }
            catch (Exception e) { _log?.LogWarning("MobDeathProbe failed to patch: " + e.Message); }
            try { harmony.PatchAll(typeof(KillObjectProbe)); }
            catch (Exception e) { _log?.LogWarning("KillObjectProbe failed to patch: " + e.Message); }
        }

        public Damageable MakeDamageable(GameObject target, int health)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            Damageable damageable = target.GetComponent<Damageable>() ?? target.AddComponent<Damageable>();
            damageable.SetHealth(health);
            return damageable;
        }

        // SDK source is compiled into every mod, so normal static fields are private to that
        // mod's assembly. AppDomain data gives all ICCMF mods one small, shared list containing
        // only Unity GameObjects (a type they all agree on).
        internal static void RegisterEntity(GameObject entity)
        {
            if (entity == null) return;
            List<GameObject> entities = SharedEntities();
            if (!entities.Contains(entity)) entities.Add(entity);
        }

        internal static void UnregisterEntity(GameObject entity)
        {
            if (entity == null) return;
            SharedEntities().Remove(entity);
        }

        public DamageResult Damage(GameObject target, int amount, GameObject source = null)
        {
            if (target == null || amount <= 0) return DamageResult.Missed;
            Damageable custom = target.GetComponentInParent<Damageable>() ??
                target.GetComponentInChildren<Damageable>();
            if (custom != null) return custom.TakeDamage(amount, source);
            DamageResult foreignResult;
            if (TryDamageForeign(target, amount, source, out foreignResult)) return foreignResult;

            GameObject player = Main.Player;
            if (player != null && (target == player || target.transform.IsChildOf(player.transform)))
                return DamagePlayer(player, amount, source);

            Mob mob = target.GetComponentInParent<Mob>() ?? target.GetComponentInChildren<Mob>();
            if (mob != null) return DamageMob(mob, amount);
            Hurt hurt = target.GetComponentInParent<Hurt>() ?? target.GetComponentInChildren<Hurt>();
            return DamageObject(hurt, amount, source);
        }

        public AttackBuilder Attack(GameObject attacker)
        {
            if (attacker == null) throw new ArgumentNullException(nameof(attacker));
            return new AttackBuilder(this, attacker);
        }

        public WeaponBuilder Weapon(ContentItem item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            return new WeaponBuilder(this, item);
        }

        public ProjectileBuilder Projectile(Sprite sprite, Vector3 start, Vector3 end)
        {
            return new ProjectileBuilder(this, sprite, start, end);
        }

        /// <param name="exclude">Optional object (and its children) that is skipped, e.g. a target that was already hit directly.</param>
        public void DamageRadius(Vector3 center, float radius, int damage, GameObject source = null,
            Func<float, float> falloff = null, GameObject exclude = null)
        {
            var targets = new HashSet<GameObject>();
            foreach (Damageable value in UnityEngine.Object.FindObjectsOfType<Damageable>())
                targets.Add(value.gameObject);
            foreach (MonoBehaviour value in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
                if (IsForeignDamageable(value)) targets.Add(value.gameObject);
            foreach (Hurt value in UnityEngine.Object.FindObjectsOfType<Hurt>())
                targets.Add(value.gameObject);
            foreach (GameObject target in targets)
            {
                if (target == null) continue;
                // Never hit the shooter with their own blast.
                if (source != null && (target == source || target.transform.IsChildOf(source.transform)))
                    continue;
                // Skip the direct target that already took the full hit.
                if (exclude != null && (target == exclude || target.transform.IsChildOf(exclude.transform) ||
                    exclude.transform.IsChildOf(target.transform)))
                    continue;
                float distance = Vector3.Distance(center, target.transform.position);
                if (distance > radius) continue;
                float scale = falloff == null ? 1f : Mathf.Max(0f, falloff(distance / radius));
                Damage(target, Mathf.RoundToInt(damage * scale), source);
            }
        }

        internal void Register(WeaponDefinition weapon)
        {
            if (_weapons.ContainsKey(weapon.Item.GameId))
                throw new InvalidOperationException("A weapon attack already exists for " + weapon.Item.Name + ".");
            _weapons.Add(weapon.Item.GameId, weapon);
            _log?.LogInfo("Registered weapon attack for " + weapon.Item.Name + ".");
        }

        internal Projectile Launch(ProjectileBuilder builder)
        {
            var obj = new GameObject(builder.NameValue ?? "Mod projectile");
            obj.hideFlags = HideFlags.HideAndDontSave;
            obj.transform.position = builder.StartValue;
            Vector3 direction = builder.EndValue - builder.StartValue;
            obj.transform.rotation = Quaternion.Euler(0f, 0f,
                Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg);
            obj.transform.localScale = Vector3.one * builder.ScaleValue;
            var renderer = obj.AddComponent<SpriteRenderer>();
            renderer.sprite = builder.SpriteValue;
            renderer.sortingOrder = builder.SortingOrderValue;
            var projectile = obj.AddComponent<Projectile>();
            projectile.Initialize(builder);
            return projectile;
        }

        private bool TryHandleWeapon(PlayerAimAndAtack attack)
        {
            ToolBar bar = ToolBar.singleton;
            ItemStack[] slots = bar == null ? null : bar.inventory?.storage;
            if (slots == null || bar.index < 0 || bar.index >= slots.Length) return false;
            ItemStack equipped = slots[bar.index];
            if (equipped == null || equipped.amount <= 0 ||
                !_weapons.TryGetValue(equipped.item, out WeaponDefinition weapon)) return false;

            bool held = (GameSettings.controlMode == Controls.Keyboard && Input.GetMouseButton(0)) ||
                (GameSettings.controlMode == Controls.Controller && ControllerInput.device != null &&
                    ControllerInput.IsButtonPressed(ControllerInput.device.buttonWest));
            bool pressed = held && !_triggerWasHeld;
            _triggerWasHeld = held;
            if (!held || (!weapon.Automatic && !pressed)) return true;

            if (attack.hitRateTimer > 0f || Main.Player == null || Terrain.singleton == null ||
                Main.inInventory || Main.inProduction || Main.gameMode != Main.GameMode.Normal)
                return true;

            TerrainPhysicsBody player = Main.Player.GetComponent<TerrainPhysicsBody>();
            if (player == null) return true;
            GameObject target = ResolveTarget(out Vector3 end);
            Vector3 start = player.transform.position + Vector3.up * .24f;
            float range = Vector3.Distance(start, end);
            _log?.LogInfo("[Combat] " + weapon.Item.Name + " target=" +
                (target != null ? target.name : "null") + " range=" + range.ToString("F2") +
                "/" + weapon.Range.ToString("F2"));
            if (target == null || range > weapon.Range) return true;

            if (weapon.Ammo != null)
            {
                Storage inventory = PlayerInventory.inventory;
                if (inventory == null || inventory.GetTotalItemAmount(weapon.Ammo.GameId) <= 0)
                {
                    // Dry-fire click (only once per trigger pull, not every frame).
                    if (pressed) weapon.EmptySound?.Fire();
                    return true;
                }
                if (inventory.Get(new ItemStack(weapon.Ammo.GameId, 1)).amount != 1) return true;
            }

            attack.hitRateTimer = weapon.Cooldown;
            weapon.FireSound?.Fire();
            Action impact = delegate
            {
                weapon.ImpactSound?.Fire();
                DamageResult result = Damage(target, weapon.Damage, Main.Player);
                weapon.OnHit?.Invoke(new AttackHit(weapon.Item, target, start, end, result));
                if (weapon.BlastRadius > 0f)
                    DamageRadius(end, weapon.BlastRadius, weapon.Damage, Main.Player,
                        normalized => 1f - normalized * weapon.BlastFalloff, target);
            };
            if (weapon.ProjectileSprite == null) impact();
            else Projectile(weapon.ProjectileSprite, start, end)
                .Speed(weapon.ProjectileSpeed)
                .Scale(weapon.ProjectileScale)
                .OnImpact(impact)
                .Launch();
            return true;
        }

        private static GameObject ResolveTarget(out Vector3 end)
        {
            // Custom entities do not need to participate in the game's native selection system.
            // Prefer their visible sprite under the mouse before the terrain raycast selects a
            // tree, floor tile or wall behind them.
            GameObject cursorCustom = FindCustomUnderCursor();
            if (cursorCustom != null)
            {
                end = cursorCustom.transform.position + Vector3.up * .18f;
                return cursorCustom;
            }

            // 1. Whatever the game already has selected under the cursor.
            if (Main.selected != null)
            {
                GameObject selectedCustom = FindCustomObject(Main.selected);
                if (selectedCustom != null)
                {
                    end = selectedCustom.transform.position + Vector3.up * .18f;
                    return selectedCustom;
                }
                Mob selectedMob = Main.selected.GetComponentInParent<Mob>();
                if (selectedMob != null)
                {
                    // Dead mobs (corpses) are ignored so shots and ammo aren't wasted on them.
                    if (IsAlive(selectedMob))
                    {
                        end = selectedMob.transform.position + Vector3.up * .18f;
                        return selectedMob.gameObject;
                    }
                }
                else
                {
                    TerrainPhysicsBody selected = Main.selected.GetComponentInParent<TerrainPhysicsBody>();
                    if (selected != null && selected.gameObject != Main.Player)
                    {
                        end = selected.transform.position + Vector3.up * .18f;
                        return selected.gameObject;
                    }
                }
            }

            // 2. Terrain raycast, but prefer a mob standing near the aimed point.
            if (Terrain.singleton != null && WorldGeneration.generator != null)
            {
                Vector3Int cell = Terrain.singleton.GetBlockFromRaycast();
                end = Terrain.singleton.TerrainToWorldPosition(cell);

                Mob nearest = FindMobNear(end, MobPickRadius);
                if (nearest != null)
                {
                    end = nearest.transform.position + Vector3.up * .18f;
                    return nearest.gameObject;
                }

                GameObject custom = FindCustomNear(end, MobPickRadius);
                if (custom != null)
                {
                    end = custom.transform.position + Vector3.up * .18f;
                    return custom;
                }

                if (WorldGeneration.generator.loadedObjects.TryGetValue(cell, out GameObject value)) return value;
                if (WorldGeneration.generator.loadedObjects.TryGetValue(cell + new Vector3Int(0, 0, 1), out value))
                    return value;
            }
            end = Vector3.zero;
            return null;
        }

        private static GameObject FindCustomUnderCursor()
        {
            // Input.mousePosition is valid even when ISO-CORE has not yet switched its own
            // control-mode flag back to Keyboard. Gating on that flag made registered entities
            // impossible to aim at after some controller/menu interactions.
            if (Camera.main == null) return null;
            Vector2 mouse = Input.mousePosition;
            GameObject best = null;
            float bestScore = float.PositiveInfinity;
            var seen = new HashSet<GameObject>();
            foreach (GameObject value in LiveSharedEntities())
                ConsiderCursorTarget(value, mouse, seen, ref best, ref bestScore);
            foreach (Damageable value in UnityEngine.Object.FindObjectsOfType<Damageable>())
                ConsiderCursorTarget(value == null || value.IsDead ? null : value.gameObject,
                    mouse, seen, ref best, ref bestScore);
            foreach (MonoBehaviour value in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
                if (IsForeignDamageable(value) && IsAlive(value))
                    ConsiderCursorTarget(value.gameObject, mouse, seen, ref best, ref bestScore);
            return best;
        }

        private static void ConsiderCursorTarget(GameObject target, Vector2 mouse,
            HashSet<GameObject> seen, ref GameObject best, ref float bestScore)
        {
            if (target == null || !seen.Add(target) || target == Main.Player ||
                (Main.Player != null && target.transform.IsChildOf(Main.Player.transform))) return;
            SpriteRenderer renderer = target.GetComponentInChildren<SpriteRenderer>();
            if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) return;
            Sprite sprite = renderer.sprite;
            if (sprite == null) return;
            Bounds bounds = sprite.bounds;
            Vector3 center = Camera.main.WorldToScreenPoint(renderer.transform.TransformPoint(bounds.center));
            if (center.z <= 0f) return;
            Vector2 min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            Vector2 max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            for (int i = 0; i < 4; i++)
            {
                Vector3 corner = new Vector3((i & 1) == 0 ? bounds.min.x : bounds.max.x,
                    (i & 2) == 0 ? bounds.min.y : bounds.max.y, bounds.center.z);
                Vector2 screen = Camera.main.WorldToScreenPoint(renderer.transform.TransformPoint(corner));
                min = Vector2.Min(min, screen);
                max = Vector2.Max(max, screen);
            }
            float padding = Mathf.Max(10f, Camera.main.pixelHeight / 90f);
            Vector2 half = (max - min) * .5f;
            Vector2 offset = mouse - (min + max) * .5f;
            float score = Mathf.Max(Mathf.Abs(offset.x) / (half.x + padding),
                Mathf.Abs(offset.y) / (half.y + padding));
            if (score > 1f) return;
            if (score >= bestScore) return;
            best = target; bestScore = score;
        }

        private static GameObject FindCustomNear(Vector3 point, float radius)
        {
            GameObject best = null;
            float bestDistance = radius;
            foreach (GameObject value in LiveSharedEntities())
            {
                if (value == null || value == Main.Player ||
                    (Main.Player != null && value.transform.IsChildOf(Main.Player.transform))) continue;
                float distance = Vector2.Distance(point, value.transform.position);
                if (distance >= bestDistance) continue;
                best = value; bestDistance = distance;
            }
            foreach (Damageable value in UnityEngine.Object.FindObjectsOfType<Damageable>())
            {
                if (value == null || value.IsDead || Main.Player == value.gameObject ||
                    (Main.Player != null && value.transform.IsChildOf(Main.Player.transform))) continue;
                float distance = Vector2.Distance(point, value.transform.position);
                if (distance >= bestDistance) continue;
                best = value.gameObject; bestDistance = distance;
            }
            foreach (MonoBehaviour value in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
            {
                if (!IsForeignDamageable(value) || !IsAlive(value)) continue;
                float distance = Vector2.Distance(point, value.transform.position);
                if (distance >= bestDistance) continue;
                best = value.gameObject; bestDistance = distance;
            }
            return best;
        }

        private static List<GameObject> SharedEntities()
        {
            var entities = AppDomain.CurrentDomain.GetData(EntityRegistryKey) as List<GameObject>;
            if (entities != null) return entities;
            entities = new List<GameObject>();
            AppDomain.CurrentDomain.SetData(EntityRegistryKey, entities);
            return entities;
        }

        private static GameObject[] LiveSharedEntities()
        {
            List<GameObject> entities = SharedEntities();
            for (int i = entities.Count - 1; i >= 0; i--)
                if (entities[i] == null) entities.RemoveAt(i);
            return entities.ToArray();
        }

        private static GameObject FindCustomObject(GameObject target)
        {
            if (target == null) return null;
            Damageable local = target.GetComponentInParent<Damageable>() ??
                target.GetComponentInChildren<Damageable>();
            if (local != null && !local.IsDead) return local.gameObject;
            foreach (MonoBehaviour value in target.GetComponentsInParent<MonoBehaviour>(true))
                if (IsForeignDamageable(value) && IsAlive(value)) return value.gameObject;
            foreach (MonoBehaviour value in target.GetComponentsInChildren<MonoBehaviour>(true))
                if (IsForeignDamageable(value) && IsAlive(value)) return value.gameObject;
            return null;
        }

        private static bool TryDamageForeign(GameObject target, int amount, GameObject source,
            out DamageResult result)
        {
            result = DamageResult.Missed;
            MonoBehaviour damageable = null;
            foreach (MonoBehaviour value in target.GetComponentsInParent<MonoBehaviour>(true))
                if (IsForeignDamageable(value)) { damageable = value; break; }
            if (damageable == null)
                foreach (MonoBehaviour value in target.GetComponentsInChildren<MonoBehaviour>(true))
                    if (IsForeignDamageable(value)) { damageable = value; break; }
            if (damageable == null || !IsAlive(damageable)) return false;
            try
            {
                MethodInfo method = damageable.GetType().GetMethod("TakeDamage",
                    BindingFlags.Instance | BindingFlags.Public, null,
                    new[] { typeof(int), typeof(GameObject) }, null);
                object foreign = method == null ? null : method.Invoke(damageable, new object[] { amount, source });
                if (foreign == null) return false;
                Type type = foreign.GetType();
                bool hit = (bool)type.GetProperty("Hit").GetValue(foreign, null);
                int applied = (int)type.GetProperty("Amount").GetValue(foreign, null);
                bool killed = (bool)type.GetProperty("Killed").GetValue(foreign, null);
                result = new DamageResult(hit, applied, killed);
                return true;
            }
            catch { return false; }
        }

        private static bool IsForeignDamageable(MonoBehaviour value)
        {
            return value != null && !(value is Damageable) &&
                string.Equals(value.GetType().FullName, DamageableTypeName, StringComparison.Ordinal);
        }

        private static bool IsAlive(MonoBehaviour value)
        {
            try
            {
                PropertyInfo property = value.GetType().GetProperty("IsDead",
                    BindingFlags.Instance | BindingFlags.Public);
                return property != null && !(bool)property.GetValue(value, null);
            }
            catch { return false; }
        }

        private static Mob FindMobNear(Vector3 point, float radius)
        {
            Mob best = null;
            float bestDistance = radius;
            foreach (Mob mob in UnityEngine.Object.FindObjectsOfType<Mob>())
            {
                if (mob == null || !IsAlive(mob)) continue;
                float distance = Vector2.Distance(point, mob.transform.position);
                if (distance > bestDistance) continue;
                best = mob;
                bestDistance = distance;
            }
            return best;
        }

        private static bool IsAlive(Mob mob)
        {
            if (mob == null) return false;
            Hurt hurt = mob.GetComponent<Hurt>() ?? mob.GetComponentInParent<Hurt>() ??
                mob.GetComponentInChildren<Hurt>();
            return hurt == null || hurt.hp > 0;
        }

        private static DamageResult DamagePlayer(GameObject player, int amount, GameObject source)
        {
            Hurt health = player.GetComponent<Hurt>();
            if (health == null || health.hp <= 0) return DamageResult.Missed;
            int applied = Mathf.Min(amount, health.hp);
            health.hp -= applied;
            DisplayPlayerHealth display = DisplayPlayerHealth.singleton;
            if (display != null)
            {
                if (HealthDisplayTarget != null && HealthDisplayTarget.GetValue(display) as Hurt != health)
                    HealthDisplayTarget.SetValue(display, health);
                display.UpdateHealthBar();
            }
            if (health.hp <= 0) health.KillPlayer();
            else if (source != null)
            {
                TerrainPhysicsBody body = player.GetComponent<TerrainPhysicsBody>();
                if (body != null) body.KnockBack(source.transform.position, 1f);
            }
            return new DamageResult(true, applied, health.hp <= 0);
        }

        private DamageResult DamageMob(Mob mob, int amount)
        {
            if (mob == null) return DamageResult.Missed;

            // Hurt may live on the Mob object, a parent, or a child.
            Hurt hurt = mob.GetComponent<Hurt>() ?? mob.GetComponentInParent<Hurt>() ??
                mob.GetComponentInChildren<Hurt>();
            if (hurt == null)
            {
                _log?.LogWarning("[Combat] Mob " + mob.name + " has no Hurt component.");
                return DamageResult.Missed;
            }
            _log?.LogInfo("[Combat] DamageMob " + mob.name + " hp=" + hurt.hp +
                " unbreakable=" + hurt.unbreakable + " amount=" + amount);
            if (hurt.unbreakable || hurt.hp <= 0) return DamageResult.Missed;

            int before = hurt.hp;
            int applied = Mathf.Min(amount, before);
            // Let the game handle damage AND death itself, exactly like a vanilla hit.
            try { hurt.TakeDamage(applied); }
            catch (Exception e) { _log?.LogError("[Combat] Hurt.TakeDamage threw: " + e); }

            // Fallback only if the game's method didn't lower hp at all.
            if (hurt.hp > before - applied)
            {
                _log?.LogWarning("[Combat] TakeDamage did not lower hp; applying damage manually.");
                hurt.hp = before - applied;
                if (hurt.hp <= 0)
                {
                    try { mob.OnDeath(); }
                    catch (Exception e) { _log?.LogError("[Combat] Mob.OnDeath threw: " + e); }
                }
            }
            _log?.LogInfo("[Combat] Mob " + mob.name + " hp " + before + " -> " + hurt.hp);

            // Vanilla melee hits run Mob.OnDeath (inside TakeDamage) AND then Hurt.KillObject.
            // TakeDamage alone leaves the mob standing at 0 hp, so finish the job like vanilla does.
            if (hurt != null && hurt.hp <= 0)
            {
                try { hurt.KillObject(); }
                catch (Exception e) { _log?.LogError("[Combat] Hurt.KillObject threw: " + e); }
            }
            return new DamageResult(true, applied, hurt.hp <= 0);
        }

        private static DamageResult DamageObject(Hurt hurt, int amount, GameObject source)
        {
            if (hurt == null || hurt.unbreakable || hurt.hp <= 0) return DamageResult.Missed;
            int applied = Mathf.Min(amount, hurt.hp);
            hurt.hp -= applied;
            hurt.ObjectHurt(source);
            if (hurt.hp <= 0) hurt.KillObject();
            return new DamageResult(true, applied, hurt.hp <= 0);
        }

        [HarmonyPatch(typeof(PlayerAimAndAtack), "AktivateHit")]
        private static class PlayerAttackPatch
        {
            private static bool Prefix(PlayerAimAndAtack __instance)
            {
                foreach (CombatApi api in Instances.ToArray())
                    if (api.TryHandleWeapon(__instance)) return false;
                return true;
            }
        }
    }

    public struct DamageResult
    {
        public static readonly DamageResult Missed = new DamageResult(false, 0, false);
        public DamageResult(bool hit, int amount, bool killed)
        { Hit = hit; Amount = amount; Killed = killed; }
        public bool Hit { get; }
        public int Amount { get; }
        public bool Killed { get; }
    }

    public sealed class Damageable : MonoBehaviour
    {
        public int Health { get; private set; }
        public int MaxHealth { get; private set; }
        public bool IsDead => Health <= 0;
        public event Action<Damageable, DamageResult> Damaged;
        public event Action<Damageable> Died;

        public void SetHealth(int health)
        {
            MaxHealth = Health = Mathf.Max(1, health);
        }

        public DamageResult TakeDamage(int amount, GameObject source = null)
        {
            if (IsDead || amount <= 0) return DamageResult.Missed;
            int applied = Mathf.Min(amount, Health);
            Health -= applied;
            var result = new DamageResult(true, applied, IsDead);
            Damaged?.Invoke(this, result);
            if (IsDead) Died?.Invoke(this);
            return result;
        }
    }

    public sealed class AttackBuilder
    {
        private readonly CombatApi _combat;
        private readonly GameObject _attacker;
        internal Func<GameObject> TargetValue;
        internal int DamageValue = 1;
        internal float RangeValue = 1.5f;
        internal float IntervalValue = 1f;
        internal Action<AttackHit> OnHitValue;
        internal Action<GameObject> EffectValue;
        internal Sprite ProjectileValue;
        internal float ProjectileSpeedValue = 12f;
        internal float ProjectileScaleValue = 1f;

        internal AttackBuilder(CombatApi combat, GameObject attacker)
        { _combat = combat; _attacker = attacker; }
        public AttackBuilder Target(Func<GameObject> target) { TargetValue = target; return this; }
        public AttackBuilder PlayerTarget() { TargetValue = () => Main.Player; return this; }
        public AttackBuilder Damage(int amount) { DamageValue = amount; return this; }
        public AttackBuilder Range(float distance) { RangeValue = distance; return this; }
        public AttackBuilder Every(float seconds) { IntervalValue = seconds; return this; }
        public AttackBuilder Projectile(Sprite sprite, float speed = 12f, float scale = 1f)
        { ProjectileValue = sprite; ProjectileSpeedValue = speed; ProjectileScaleValue = scale; return this; }
        public AttackBuilder OnHit(Action<AttackHit> action) { OnHitValue = action; return this; }
        /// <summary>Runs a custom impact effect in addition to (or instead of) normal damage.</summary>
        public AttackBuilder Effect(Action<GameObject> action) { EffectValue = action; return this; }
        public TimedAttack Attach()
        {
            TimedAttack attack = _attacker.AddComponent<TimedAttack>();
            attack.Initialize(_combat, this);
            return attack;
        }
    }

    public sealed class TimedAttack : MonoBehaviour
    {
        private CombatApi _combat;
        private AttackBuilder _settings;
        private float _nextAttack;
        internal void Initialize(CombatApi combat, AttackBuilder settings)
        { _combat = combat; _settings = settings; }
        private void Update()
        {
            GameObject target = _settings?.TargetValue?.Invoke();
            if (target == null || Time.time < _nextAttack ||
                Vector3.Distance(transform.position, target.transform.position) > _settings.RangeValue) return;
            _nextAttack = Time.time + Mathf.Max(.01f, _settings.IntervalValue);
            Vector3 start = transform.position;
            Vector3 end = target.transform.position;
            Action impact = delegate
            {
                DamageResult result = _combat.Damage(target, _settings.DamageValue, gameObject);
                _settings.EffectValue?.Invoke(target);
                _settings.OnHitValue?.Invoke(new AttackHit(null, target, start, end, result));
            };
            if (_settings.ProjectileValue == null) impact();
            else _combat.Projectile(_settings.ProjectileValue, start, end)
                .Speed(_settings.ProjectileSpeedValue)
                .Scale(_settings.ProjectileScaleValue)
                .OnImpact(impact)
                .Launch();
        }
    }

    public sealed class WeaponBuilder
    {
        private readonly CombatApi _combat;
        private readonly WeaponDefinition _weapon;
        internal WeaponBuilder(CombatApi combat, ContentItem item)
        { _combat = combat; _weapon = new WeaponDefinition { Item = item }; }
        public WeaponBuilder Ammo(ContentItem item) { _weapon.Ammo = item; return this; }
        public WeaponBuilder Damage(int amount) { _weapon.Damage = amount; return this; }
        public WeaponBuilder Range(float distance) { _weapon.Range = distance; return this; }
        public WeaponBuilder Cooldown(float seconds) { _weapon.Cooldown = seconds; return this; }
        public WeaponBuilder Automatic(bool enabled = true) { _weapon.Automatic = enabled; return this; }
        public WeaponBuilder Projectile(Sprite sprite, float speed = 18f, float scale = 1f)
        { _weapon.ProjectileSprite = sprite; _weapon.ProjectileSpeed = speed; _weapon.ProjectileScale = scale; return this; }
        public WeaponBuilder Blast(float radius, float falloff = .75f)
        { _weapon.BlastRadius = radius; _weapon.BlastFalloff = falloff; return this; }
        public WeaponBuilder OnHit(Action<AttackHit> action) { _weapon.OnHit = action; return this; }
        /// <summary>Sound played each time the weapon fires.</summary>
        public WeaponBuilder FireSound(Sound sound) { _weapon.FireSound = sound; return this; }
        /// <summary>Sound played when the projectile lands (e.g. an explosion).</summary>
        public WeaponBuilder ImpactSound(Sound sound) { _weapon.ImpactSound = sound; return this; }
        /// <summary>Sound played when the trigger is pulled with no ammo.</summary>
        public WeaponBuilder EmptySound(Sound sound) { _weapon.EmptySound = sound; return this; }
        public void Register() { _combat.Register(_weapon); }
    }

    internal sealed class WeaponDefinition
    {
        internal ContentItem Item;
        internal ContentItem Ammo;
        internal int Damage = 1;
        internal float Range = 8f;
        internal float Cooldown = .5f;
        internal bool Automatic;
        internal Sprite ProjectileSprite;
        internal float ProjectileSpeed = 18f;
        internal float ProjectileScale = 1f;
        internal float BlastRadius;
        internal float BlastFalloff = .75f;
        internal Action<AttackHit> OnHit;
        internal Sound FireSound;
        internal Sound ImpactSound;
        internal Sound EmptySound;
    }

    public sealed class AttackHit
    {
        internal AttackHit(ContentItem item, GameObject target, Vector3 start, Vector3 end,
            DamageResult damage)
        { Item = item; Target = target; Start = start; End = end; Damage = damage; }
        public ContentItem Item { get; }
        public GameObject Target { get; }
        public Vector3 Start { get; }
        public Vector3 End { get; }
        public DamageResult Damage { get; }
    }

    public sealed class ProjectileBuilder
    {
        private readonly CombatApi _combat;
        internal Sprite SpriteValue;
        internal Vector3 StartValue;
        internal Vector3 EndValue;
        internal float SpeedValue = 18f;
        internal float ScaleValue = 1f;
        internal int SortingOrderValue = 32760;
        internal Color TintValue = Color.white;
        internal string NameValue;
        internal Action ImpactValue;
        internal Action<GameObject> ConfigureValue;
        internal ProjectileBuilder(CombatApi combat, Sprite sprite, Vector3 start, Vector3 end)
        { _combat = combat; SpriteValue = sprite; StartValue = start; EndValue = end; }
        public ProjectileBuilder Speed(float unitsPerSecond) { SpeedValue = unitsPerSecond; return this; }
        public ProjectileBuilder Scale(float scale) { ScaleValue = scale; return this; }
        public ProjectileBuilder SortingOrder(int order) { SortingOrderValue = order; return this; }
        public ProjectileBuilder Tint(Color color) { TintValue = color; return this; }
        public ProjectileBuilder Named(string name) { NameValue = name; return this; }
        public ProjectileBuilder OnImpact(Action action) { ImpactValue = action; return this; }
        public ProjectileBuilder Configure(Action<GameObject> action) { ConfigureValue = action; return this; }
        public Projectile Launch() { return _combat.Launch(this); }
    }

    public sealed class Projectile : MonoBehaviour
    {
        private Vector3 _start;
        private Vector3 _end;
        private float _duration;
        private float _launched;
        private Action _impact;
        internal void Initialize(ProjectileBuilder builder)
        {
            _start = builder.StartValue; _end = builder.EndValue;
            _duration = Mathf.Max(.01f, Vector3.Distance(_start, _end) / Mathf.Max(.01f, builder.SpeedValue));
            _launched = Time.time; _impact = builder.ImpactValue;
        }
        private void Update()
        {
            float progress = Mathf.Clamp01((Time.time - _launched) / _duration);
            transform.position = Vector3.Lerp(_start, _end, progress);
            if (progress < 1f) return;
            try { _impact?.Invoke(); }
            finally { Destroy(gameObject); }
        }
    }

    internal sealed class SdkCoroutineHost : MonoBehaviour { }

    /// <summary>Temporary: logs when the game kills a mob, with the call chain.</summary>
    [HarmonyPatch(typeof(Mob), "OnDeath")]
    internal static class MobDeathProbe
    {
        private static readonly ManualLogSource ProbeLog = BepInEx.Logging.Logger.CreateLogSource("Probe");
        private static void Prefix(Mob __instance)
        {
            ProbeLog.LogInfo("Mob.OnDeath on " + __instance.name + "\n" + Environment.StackTrace);
        }
    }

    /// <summary>Temporary: logs when the game kills an object, with the call chain.</summary>
    [HarmonyPatch(typeof(Hurt), "KillObject")]
    internal static class KillObjectProbe
    {
        private static readonly ManualLogSource ProbeLog = BepInEx.Logging.Logger.CreateLogSource("Probe");
        private static void Prefix(Hurt __instance)
        {
            ProbeLog.LogInfo("Hurt.KillObject on " + __instance.name + "\n" + Environment.StackTrace);
        }
    }
}