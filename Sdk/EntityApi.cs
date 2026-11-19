using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ISOCORE.ModSdk
{
    /// <summary>Defines, spawns and schedules custom animated world entities.</summary>
    public sealed class EntityApi
    {
        private readonly string _modId;
        private readonly GameObject _host;
        private readonly CombatApi _combat;
        private readonly PathfindingApi _paths;
        private readonly ManualLogSource _log;
        private readonly List<EntityType> _types = new List<EntityType>();
        private readonly List<EntityInstance> _alive = new List<EntityInstance>();

        internal EntityApi(string modId, GameObject host, CombatApi combat, PathfindingApi paths,
            ManualLogSource log)
        { _modId = modId; _host = host; _combat = combat; _paths = paths; _log = log; }

        public IReadOnlyList<EntityInstance> Alive => _alive;
        internal CombatApi Combat => _combat;

        public EntityBuilder Define(string key, string name, EntityAnimation animation)
        { return new EntityBuilder(this, key, name, animation); }

        public NightRaidBuilder NightRaid(string name)
        { return new NightRaidBuilder(this, name); }

        public EntityInstance Spawn(EntityType type, Vector3Int cell, int group = 0)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (!_types.Contains(type)) throw new InvalidOperationException("Entity type belongs to another API.");
            var obj = new GameObject(_modId + " entity " + type.Key);
            obj.hideFlags = HideFlags.HideAndDontSave;
            var renderer = obj.AddComponent<SpriteRenderer>();
            renderer.sprite = type.Animation.Idle[0];
            renderer.color = type.Tint;
            renderer.sortingLayerName = "World";
            obj.transform.localScale = Vector3.one * type.Scale;

            Damageable health = _combat.MakeDamageable(obj, type.Health);
            var instance = new EntityInstance(type, obj, health, cell, group);
            var controller = obj.AddComponent<EntityController>();
            controller.Initialize(this, instance, renderer);
            instance.Controller = controller;
            _alive.Add(instance);
            CombatApi.RegisterEntity(obj);
            type.ConfigureValue?.Invoke(instance);
            health.Died += dead => Die(instance);
            return instance;
        }

        internal EntityType Register(EntityBuilder builder)
        {
            foreach (EntityType type in _types)
                if (string.Equals(type.Key, builder.KeyValue, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Entity key is already registered: " + builder.KeyValue);
            var result = new EntityType(builder);
            _types.Add(result);
            _log?.LogInfo("Registered entity " + result.Name + ".");
            return result;
        }

        internal void Forget(EntityInstance instance) { _alive.Remove(instance); }

        private void Die(EntityInstance instance)
        {
            if (instance == null || instance.GameObject == null) return;
            instance.Controller?.PlayDeath();
            DropLoot(instance);
            instance.Type.OnDeathValue?.Invoke(instance);
            _alive.Remove(instance);
            CombatApi.UnregisterEntity(instance.GameObject);
            UnityEngine.Object.Destroy(instance.GameObject, .7f);
        }

        private void DropLoot(EntityInstance entity)
        {
            var drops = new List<Drop.ItemDrop>();
            foreach (EntityLoot loot in entity.Type.Loot)
            {
                if (UnityEngine.Random.value > loot.Chance) continue;
                drops.Add(new Drop.ItemDrop { item = loot.Item, min = loot.Minimum,
                    max = loot.Maximum, durability = 0 });
            }
            if (drops.Count == 0) return;
            try
            {
                Drop drop = entity.GameObject.GetComponent<Drop>() ?? entity.GameObject.AddComponent<Drop>();
                drop.dropItems = true;
                drop.noRandomDrops = true;
                drop.itemDrops = drops.ToArray();
                MethodInfo method = AccessTools.Method(typeof(Drop), "DropItems");
                if (method != null && method.GetParameters().Length == 0) method.Invoke(drop, null);
                else _log?.LogWarning("Could not find Drop.DropItems(); loot remains attached to corpse.");
            }
            catch (Exception error) { _log?.LogWarning("Could not drop entity loot: " + error.Message); }
        }

        internal NightRaid Start(NightRaidBuilder builder)
        {
            var raidObject = new GameObject(_modId + " " + builder.NameValue);
            raidObject.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(raidObject);
            NightRaid raid = raidObject.AddComponent<NightRaid>();
            raid.Initialize(this, builder, _combat, _log);
            return raid;
        }
    }

    public sealed class EntityAnimation
    {
        public EntityAnimation(Sprite[] idle, Sprite[] walk, Sprite[] attack,
            Sprite hurt = null, Sprite death = null)
        {
            Idle = Require(idle, "idle"); Walk = Require(walk, "walk");
            Attack = Require(attack, "attack"); Hurt = hurt ?? Idle[0]; Death = death ?? Hurt;
        }
        public Sprite[] Idle { get; }
        public Sprite[] Walk { get; }
        public Sprite[] Attack { get; }
        public Sprite Hurt { get; }
        public Sprite Death { get; }
        private static Sprite[] Require(Sprite[] frames, string name)
        {
            if (frames == null || frames.Length == 0) throw new ArgumentException(name + " frames are required.");
            return frames;
        }

        /// <summary>Creates idle, walk, attack, hurt and death animation from one eight-frame row.</summary>
        public static EntityAnimation EightFrames(Sprite[] sheet, int row)
        {
            if (sheet == null || row < 0 || sheet.Length < (row + 1) * 8)
                throw new ArgumentException("The sheet does not contain that eight-frame row.");
            int i = row * 8;
            return new EntityAnimation(new[] { sheet[i], sheet[i + 1] },
                new[] { sheet[i + 2], sheet[i + 3] }, new[] { sheet[i + 4], sheet[i + 5] },
                sheet[i + 6], sheet[i + 7]);
        }
    }

    public sealed class EntityBuilder
    {
        private readonly EntityApi _api;
        internal readonly string KeyValue;
        internal readonly string NameValue;
        internal readonly EntityAnimation AnimationValue;
        internal int HealthValue = 10;
        internal float SpeedValue = 2f;
        internal float ScaleValue = 1f;
        internal Color TintValue = Color.white;
        internal int MeleeDamageValue = 1;
        internal float MeleeEveryValue = 1.2f;
        internal float MeleeRangeValue = 1.4f;
        internal int BreachDamageValue;
        internal float BreachEveryValue = 1.1f;
        internal readonly HashSet<Items> VaultItemsValue = new HashSet<Items>();
        internal readonly HashSet<string> VaultNamesValue = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal int GroupMinimumValue = 1;
        internal int GroupMaximumValue = 1;
        internal float GroupSpeedValue;
        internal int SunDamageValue;
        internal float SunEveryValue = 1.5f;
        internal bool SunSafeValue;
        internal Sprite ProjectileValue;
        internal float ProjectileSpeedValue = 8f;
        internal int RangedDamageValue;
        internal float RangedRangeValue = 8f;
        internal float RangedEveryValue = 4f;
        internal EntityHazardDefinition HazardValue;
        internal readonly List<EntityLoot> LootValue = new List<EntityLoot>();
        internal Action<EntityInstance> ConfigureValue;
        internal Action<EntityInstance> OnDeathValue;

        internal EntityBuilder(EntityApi api, string key, string name, EntityAnimation animation)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Entity key is required.");
            _api = api; KeyValue = key.Trim().ToLowerInvariant();
            NameValue = string.IsNullOrWhiteSpace(name) ? key : name; AnimationValue = animation;
        }
        public EntityBuilder Health(int value) { HealthValue = Math.Max(1, value); return this; }
        public EntityBuilder Speed(float value) { SpeedValue = Mathf.Max(.1f, value); return this; }
        public EntityBuilder Scale(float value) { ScaleValue = Mathf.Max(.05f, value); return this; }
        public EntityBuilder Tint(Color value) { TintValue = value; return this; }
        public EntityBuilder Melee(int damage, float every = 1.2f, float range = 1.4f)
        { MeleeDamageValue = Math.Max(0, damage); MeleeEveryValue = Mathf.Max(.1f, every);
            MeleeRangeValue = Mathf.Max(.1f, range); return this; }
        public EntityBuilder BreakDefenses(int damage, float every = 1.1f)
        { BreachDamageValue = Math.Max(0, damage); BreachEveryValue = Mathf.Max(.1f, every); return this; }
        public EntityBuilder VaultOver(params Items[] items)
        { if (items != null) foreach (Items item in items) VaultItemsValue.Add(item); return this; }
        public EntityBuilder VaultOverObjects(params string[] names)
        { if (names != null) foreach (string name in names) if (!string.IsNullOrWhiteSpace(name))
            VaultNamesValue.Add(name.Trim()); return this; }
        public EntityBuilder Groups(int minimum = 2, int maximum = 3, float approachSpeed = 1.55f)
        { GroupMinimumValue = Math.Max(1, minimum); GroupMaximumValue = Math.Max(GroupMinimumValue, maximum);
            GroupSpeedValue = Mathf.Max(.1f, approachSpeed); return this; }
        public EntityBuilder BurnsInDaylight(int damage = 4, float every = 1.5f)
        { SunDamageValue = Math.Max(1, damage); SunEveryValue = Mathf.Max(.1f, every); return this; }
        public EntityBuilder DaylightSafe(bool safe = true) { SunSafeValue = safe; return this; }
        public EntityBuilder Ranged(Sprite projectile, int damage, float range = 8f,
            float every = 4f, float speed = 8f)
        { ProjectileValue = projectile; RangedDamageValue = Math.Max(0, damage);
            RangedRangeValue = Mathf.Max(1f, range); RangedEveryValue = Mathf.Max(.1f, every);
            ProjectileSpeedValue = Mathf.Max(.1f, speed); return this; }
        public EntityBuilder LeavesHazard(Sprite sprite, int damage = 1, float duration = 8f,
            float every = 1.25f, float radius = .7f)
        { HazardValue = new EntityHazardDefinition(sprite, damage, duration, every, radius); return this; }
        public EntityBuilder Drops(Items item, float chance = 1f, int minimum = 1, int maximum = 1)
        { LootValue.Add(new EntityLoot(item, chance, minimum, maximum)); return this; }
        public EntityBuilder Drops(ContentItem item, float chance = 1f, int minimum = 1, int maximum = 1)
        { if (item == null) throw new ArgumentNullException(nameof(item)); return Drops(item.GameId, chance, minimum, maximum); }
        public EntityBuilder Configure(Action<EntityInstance> configure) { ConfigureValue = configure; return this; }
        public EntityBuilder OnDeath(Action<EntityInstance> action) { OnDeathValue = action; return this; }
        public EntityType Register() { return _api.Register(this); }
    }

    public sealed class EntityType
    {
        internal EntityType(EntityBuilder b)
        {
            Key = b.KeyValue; Name = b.NameValue; Animation = b.AnimationValue; Health = b.HealthValue;
            Speed = b.SpeedValue; Scale = b.ScaleValue; Tint = b.TintValue;
            MeleeDamage = b.MeleeDamageValue; MeleeEvery = b.MeleeEveryValue; MeleeRange = b.MeleeRangeValue;
            BreachDamage = b.BreachDamageValue; BreachEvery = b.BreachEveryValue;
            VaultItems = b.VaultItemsValue; VaultObjectNames = b.VaultNamesValue;
            GroupMinimum = b.GroupMinimumValue;
            GroupMaximum = b.GroupMaximumValue; GroupSpeed = b.GroupSpeedValue;
            SunDamage = b.SunDamageValue; SunEvery = b.SunEveryValue; SunSafe = b.SunSafeValue;
            Projectile = b.ProjectileValue; ProjectileSpeed = b.ProjectileSpeedValue;
            RangedDamage = b.RangedDamageValue; RangedRange = b.RangedRangeValue;
            RangedEvery = b.RangedEveryValue; Hazard = b.HazardValue; Loot = b.LootValue;
            ConfigureValue = b.ConfigureValue; OnDeathValue = b.OnDeathValue;
        }
        public string Key { get; } public string Name { get; } public EntityAnimation Animation { get; }
        public int Health { get; } public float Speed { get; } public float Scale { get; }
        public Color Tint { get; } public int MeleeDamage { get; } public float MeleeEvery { get; }
        public float MeleeRange { get; } public int BreachDamage { get; } public float BreachEvery { get; }
        public IReadOnlyCollection<Items> VaultItems { get; } public int GroupMinimum { get; }
        public IReadOnlyCollection<string> VaultObjectNames { get; }
        public int GroupMaximum { get; } public float GroupSpeed { get; } public int SunDamage { get; }
        public float SunEvery { get; } public bool SunSafe { get; } public Sprite Projectile { get; }
        public float ProjectileSpeed { get; } public int RangedDamage { get; } public float RangedRange { get; }
        public float RangedEvery { get; } public EntityHazardDefinition Hazard { get; }
        public IReadOnlyList<EntityLoot> Loot { get; }
        internal Action<EntityInstance> ConfigureValue { get; } internal Action<EntityInstance> OnDeathValue { get; }
    }

    public sealed class EntityInstance
    {
        internal EntityInstance(EntityType type, GameObject gameObject, Damageable health,
            Vector3Int cell, int group)
        { Type = type; GameObject = gameObject; Health = health; Cell = cell; Group = group; }
        public EntityType Type { get; } public GameObject GameObject { get; }
        public Damageable Health { get; } public Vector3Int Cell { get; internal set; }
        public int Group { get; } public bool Burning { get; internal set; }
        public EntityController Controller { get; internal set; }
    }

    public struct EntityLoot
    {
        public EntityLoot(Items item, float chance, int minimum, int maximum)
        { Item = item; Chance = Mathf.Clamp01(chance); Minimum = Math.Max(1, minimum);
            Maximum = Math.Max(Minimum, maximum); }
        public Items Item { get; } public float Chance { get; }
        public int Minimum { get; } public int Maximum { get; }
    }

    public sealed class EntityHazardDefinition
    {
        internal EntityHazardDefinition(Sprite sprite, int damage, float duration, float every, float radius)
        { Sprite = sprite; Damage = Math.Max(0, damage); Duration = Mathf.Max(.1f, duration);
            Every = Mathf.Max(.1f, every); Radius = Mathf.Max(.1f, radius); }
        public Sprite Sprite { get; } public int Damage { get; } public float Duration { get; }
        public float Every { get; } public float Radius { get; }
    }

    public sealed class EntityController : MonoBehaviour
    {
        private static readonly Vector3Int Vertical = new Vector3Int(0, 0, 1);
        private static readonly Vector3Int[] Directions = { Vector3Int.right, Vector3Int.left,
            Vector3Int.up, Vector3Int.down };
        private EntityApi _api; private EntityInstance _entity; private SpriteRenderer _renderer;
        private Queue<Vector3Int> _route = new Queue<Vector3Int>();
        private Vector3Int _next; private float _move; private bool _moving;
        private float _repathAt; private float _attackAt; private float _sunAt;
        private float _hurtUntil; private float _attackUntil; private int _frame;
        private float _frameAt; private bool _dead;

        internal void Initialize(EntityApi api, EntityInstance entity, SpriteRenderer renderer)
        {
            _api = api; _entity = entity; _renderer = renderer;
            entity.Health.Damaged += (health, result) => _hurtUntil = Time.time + .18f;
            Place(entity.Cell);
        }

        public void Repath() { _route.Clear(); _repathAt = 0f; }
        internal void PlayDeath() { _dead = true; _renderer.sprite = _entity.Type.Animation.Death; }

        private void Update()
        {
            if (_dead || Terrain.singleton == null || Main.Player == null || !Main.inWorld ||
                Terrain.singleton.worldSpaceTransitionActive || Time.timeScale <= 0f) return;
            EntityType type = _entity.Type;
            TerrainPhysicsBody body = Main.Player.GetComponent<TerrainPhysicsBody>();
            if (body == null) return;
            Vector3Int target = FindGround(body.GetIntPosition() + Vertical);

            UpdateDaylight(type);
            UpdateAnimation(type);
            if (_entity.Health.IsDead) return;

            float distance = Vector3.Distance(_entity.Cell, target);
            if (type.Projectile != null && distance > type.MeleeRange && distance <= type.RangedRange &&
                Time.time >= _attackAt)
            { RangedAttack(type, target); return; }
            if (distance <= type.MeleeRange)
            { _route.Clear(); if (Time.time >= _attackAt) MeleeAttack(type); return; }

            if (_moving) { Move(type); return; }
            if (Time.time >= _repathAt || _route.Count == 0) Plan(target);
            if (_route.Count == 0) return;
            Vector3Int next = _route.Peek();
            EntityStep step;
            if (!TryStep(_entity.Cell, next, true, out step)) { Repath(); return; }
            if (step.Obstacle != null || step.Block.HasValue)
            {
                if (Time.time >= _attackAt)
                {
                    _attackAt = Time.time + type.BreachEvery; _attackUntil = Time.time + .28f;
                    if (step.Obstacle != null) _apiDamage(step.Obstacle, type.BreachDamage);
                    if (step.Block.HasValue) BreakBlock(step.Block.Value, type.BreachDamage);
                }
                return;
            }
            _route.Dequeue(); _next = next; _moving = true; _move = 0f;
        }

        private void _apiDamage(GameObject target, int damage)
        {
            if (damage <= 0) return;
            _api.Combat.Damage(target, damage, gameObject);
        }

        private void MeleeAttack(EntityType type)
        {
            _attackAt = Time.time + type.MeleeEvery; _attackUntil = Time.time + .28f;
            _api.Combat.Damage(Main.Player, type.MeleeDamage, gameObject);
        }

        private void RangedAttack(EntityType type, Vector3Int target)
        {
            _attackAt = Time.time + type.RangedEvery; _attackUntil = Time.time + .45f;
            Vector3 start = transform.position + Vector3.up * .45f;
            Vector3 end = Terrain.singleton.TerrainToWorldPosition(target - Vertical);
            _api.Combat.Projectile(type.Projectile, start, end).Speed(type.ProjectileSpeed).OnImpact(() =>
            {
                if (type.RangedDamage > 0) _api.Combat.Damage(Main.Player, type.RangedDamage, gameObject);
                if (type.Hazard != null) EntityHazard.Spawn(type.Hazard, target, _api.Combat, gameObject);
            }).Launch();
        }

        private void UpdateDaylight(EntityType type)
        {
            WeatherAndDayTime weather = WeatherAndDayTime.singleton;
            bool daylight = weather != null && (weather.dayTime == WeatherAndDayTime.DayTime.day ||
                weather.dayTime == WeatherAndDayTime.DayTime.sunnRise);
            _entity.Burning = daylight && !type.SunSafe && type.SunDamage > 0 && !UnderCover(_entity.Cell);
            if (_entity.Burning && Time.time >= _sunAt)
            { _sunAt = Time.time + type.SunEvery; _entity.Health.TakeDamage(type.SunDamage); }
        }

        private void Move(EntityType type)
        {
            float speed = _entity.Group > 0 && type.GroupSpeed > 0f ? type.GroupSpeed : type.Speed;
            if (speed <= 0f) speed = type.Speed;
            _move += Time.deltaTime * speed;
            Vector3 from = World(_entity.Cell), to = World(_next);
            float t = Mathf.Clamp01(_move);
            float jump = _next.z > _entity.Cell.z ? Mathf.Sin(t * Mathf.PI) * .26f : 0f;
            transform.position = Vector3.Lerp(from, to, t) + Vector3.up * jump;
            if (_move < 1f) return;
            _entity.Cell = _next; _moving = false; _move = 0f; Place(_entity.Cell);
        }

        private void Plan(Vector3Int target)
        {
            _repathAt = Time.time + UnityEngine.Random.Range(.7f, 1.15f);
            Vector3Int origin = _entity.Cell;
            _route = new PathfindingApi().Find(origin,
                cell => Distance(cell, target) <= 1,
                cell => 10 * Math.Max(0, Distance(cell, target) - 1),
                cell => Edges(cell, origin), 420, true);
        }

        private IEnumerable<PathEdge> Edges(Vector3Int from, Vector3Int origin)
        {
            foreach (Vector3Int direction in Directions)
            for (int rise = -2; rise <= 1; rise++)
            {
                Vector3Int cell = from + direction + Vertical * rise;
                EntityStep step;
                if (Distance(cell, origin) <= 32 && TryStep(from, cell, true, out step))
                    yield return new PathEdge(cell, step.Cost);
            }
        }

        private bool TryStep(Vector3Int from, Vector3Int to, bool breach, out EntityStep step)
        {
            step = new EntityStep(); EntityType type = _entity.Type;
            int rise = to.z - from.z;
            if (rise > 1 || rise < -2 || !Solid(to - Vertical) || Terrain.singleton.CellContainsWater(to)) return false;
            if (rise > 0 && !type.VaultItems.Contains(BlockItem(to - Vertical))) return false;
            bool feet = Solid(to), head = Solid(to + Vertical);
            if (feet || head)
            {
                Vector3Int block = head ? to + Vertical : to;
                if (!breach || type.BreachDamage <= 0 || !CanBreakBlock(block)) return false;
                step.Block = block;
            }
            GameObject obstacle = Obstacle(to);
            if (obstacle != null)
            {
                if (CanVaultObject(obstacle, type.VaultItems, type.VaultObjectNames)) step.Jump = true;
                else if (breach && type.BreachDamage > 0 && CanBreakObject(obstacle)) step.Obstacle = obstacle;
                else return false;
            }
            else if (Terrain.singleton.CheckForCollider(to) ||
                Terrain.singleton.CheckForCollider(to + Vertical)) return false;
            step.Cost = step.Block.HasValue || step.Obstacle != null ? 70 : rise > 0 || step.Jump ? 15 : 10;
            return true;
        }

        private static bool CanVaultObject(GameObject obj, IReadOnlyCollection<Items> items,
            IReadOnlyCollection<string> names)
        {
            string name = obj.name.Replace("(Clone)", "");
            foreach (Items item in items)
                if (name.IndexOf(item.ToString(), StringComparison.OrdinalIgnoreCase) >= 0) return true;
            foreach (string candidate in names)
                if (name.IndexOf(candidate, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }
        private static bool CanBreakObject(GameObject obj)
        { Hurt hurt = obj.GetComponent<Hurt>(); return hurt != null && !hurt.unbreakable && hurt.hp > 0; }
        private static bool CanBreakBlock(Vector3Int cell)
        { return GroundEddeting.singelton != null && WorldGeneration.blockData.ContainsKey(
            new TerrainKey(Terrain.activeWorldSpaceId, cell)); }
        private static readonly Dictionary<TerrainKey, int> BlockDamage = new Dictionary<TerrainKey, int>();
        private static void BreakBlock(Vector3Int cell, int damage)
        {
            if (damage <= 0 || !CanBreakBlock(cell)) return;
            var key = new TerrainKey(Terrain.activeWorldSpaceId, cell);
            int total; BlockDamage.TryGetValue(key, out total); total += damage;
            if (total < 35) { BlockDamage[key] = total; return; }
            BlockDamage.Remove(key); GroundEddeting.singelton.DestroyBlock(cell);
        }
        private static GameObject Obstacle(Vector3Int foot)
        {
            if (WorldGeneration.generator == null) return null;
            for (int offset = 0; offset <= 2; offset++)
            {
                GameObject value;
                if (WorldGeneration.generator.loadedObjects.TryGetValue(foot - Vertical * offset,
                    out value) && value != null) return value;
            }
            return null;
        }
        private static Items BlockItem(Vector3Int cell)
        { Block block; return Terrain.singleton.loadedBlocks.TryGetValue(cell, out block) && block != null
                ? block.item : Items.noItem; }
        private static bool Solid(Vector3Int cell)
        { Block block; return Terrain.singleton.loadedBlocks.TryGetValue(cell, out block) && block != null &&
                block != Terrain.singleton.waterBlock; }
        private static bool UnderCover(Vector3Int foot)
        { for (int i = 2; i <= 5; i++) if (Solid(foot + Vertical * i)) return true; return false; }
        private static Vector3Int FindGround(Vector3Int near)
        { for (int i = 0; i <= 3; i++) if (Solid(near - Vertical * i - Vertical) &&
            !Solid(near - Vertical * i)) return near - Vertical * i; return near; }
        private static int Distance(Vector3Int a, Vector3Int b)
        { return Math.Abs(a.x - b.x) + Math.Abs(a.y - b.y); }
        private static Vector3 World(Vector3Int foot)
        { return Terrain.singleton.TerrainToWorldPosition(foot - Vertical); }
        private void Place(Vector3Int foot)
        { transform.position = World(foot); _renderer.sortingOrder =
            Terrain.singleton.GetObjectSortingOrder(foot - Vertical) + 1; }

        private void UpdateAnimation(EntityType type)
        {
            if (_dead) return;
            if (Time.time < _hurtUntil) { _renderer.sprite = type.Animation.Hurt; return; }
            Sprite[] frames = Time.time < _attackUntil ? type.Animation.Attack :
                _moving ? type.Animation.Walk : type.Animation.Idle;
            if (Time.time >= _frameAt) { _frameAt = Time.time + (_moving ? .16f : .28f);
                _frame = (_frame + 1) % frames.Length; }
            _renderer.sprite = frames[_frame % frames.Length];
            _renderer.color = _entity.Burning ? new Color(1f, .62f, .3f) : type.Tint;
            if (_moving && _next.x != _entity.Cell.x) _renderer.flipX = _next.x < _entity.Cell.x;
        }

        private struct EntityStep { internal GameObject Obstacle; internal Vector3Int? Block;
            internal bool Jump; internal int Cost; }
    }

    public sealed class EntityHazard : MonoBehaviour
    {
        private EntityHazardDefinition _definition; private CombatApi _combat;
        private GameObject _source; private float _expires; private float _damageAt;
        public static EntityHazard Spawn(EntityHazardDefinition definition, Vector3Int cell,
            CombatApi combat, GameObject source)
        {
            var obj = new GameObject("Entity hazard"); obj.hideFlags = HideFlags.HideAndDontSave;
            var renderer = obj.AddComponent<SpriteRenderer>(); renderer.sprite = definition.Sprite;
            renderer.sortingLayerName = "World";
            obj.transform.position = Terrain.singleton.TerrainToWorldPosition(cell - new Vector3Int(0, 0, 1));
            var hazard = obj.AddComponent<EntityHazard>(); hazard._definition = definition;
            hazard._combat = combat; hazard._source = source; hazard._expires = Time.time + definition.Duration;
            return hazard;
        }
        private void Update()
        {
            if (Time.time >= _expires) { Destroy(gameObject); return; }
            if (Main.Player == null || Time.time < _damageAt ||
                Vector3.Distance(transform.position, Main.Player.transform.position) > _definition.Radius) return;
            _damageAt = Time.time + _definition.Every;
            _combat.Damage(Main.Player, _definition.Damage, _source);
        }
    }

    public sealed class NightRaidBuilder
    {
        private readonly EntityApi _api;
        internal string NameValue; internal readonly List<RaidEntry> Entries = new List<RaidEntry>();
        internal int PerNightValue = 12; internal int MaxAliveValue = 12;
        internal int MinimumDistanceValue = 12; internal int MaximumDistanceValue = 22;
        internal float EveryValue = 4.5f; internal Items? SkipItemValue;
        internal KeyCode SkipKeyValue = KeyCode.K; internal float SkipRangeValue = 3f;
        internal Action StartedValue; internal Action EndedValue;
        internal NightRaidBuilder(EntityApi api, string name) { _api = api; NameValue = name ?? "Night raid"; }
        public NightRaidBuilder Add(EntityType type, int weight = 1)
        { if (type == null) throw new ArgumentNullException(nameof(type)); Entries.Add(new RaidEntry(type, weight)); return this; }
        public NightRaidBuilder PerNight(int count) { PerNightValue = Math.Max(1, count); return this; }
        public NightRaidBuilder MaxAlive(int count) { MaxAliveValue = Math.Max(1, count); return this; }
        public NightRaidBuilder SpawnDistance(int minimum, int maximum)
        { MinimumDistanceValue = Math.Max(2, minimum); MaximumDistanceValue = Math.Max(MinimumDistanceValue, maximum); return this; }
        public NightRaidBuilder SpawnEvery(float seconds) { EveryValue = Mathf.Max(.2f, seconds); return this; }
        public NightRaidBuilder CanSkipAt(Items item, KeyCode key = KeyCode.K, float range = 3f)
        { SkipItemValue = item; SkipKeyValue = key; SkipRangeValue = Mathf.Max(1f, range); return this; }
        public NightRaidBuilder OnStarted(Action action) { StartedValue = action; return this; }
        public NightRaidBuilder OnEnded(Action action) { EndedValue = action; return this; }
        public NightRaid Start()
        { if (Entries.Count == 0) throw new InvalidOperationException("A raid needs at least one entity type.");
            return _api.Start(this); }
    }

    internal struct RaidEntry
    {
        internal RaidEntry(EntityType type, int weight) { Type = type; Weight = Math.Max(1, weight); }
        internal EntityType Type; internal int Weight;
    }

    public sealed class NightRaid : MonoBehaviour
    {
        private static readonly Vector3Int Vertical = new Vector3Int(0, 0, 1);
        private EntityApi _api; private NightRaidBuilder _settings; private ManualLogSource _log;
        private bool _night; private int _spawned; private int _group; private float _spawnAt;
        internal void Initialize(EntityApi api, NightRaidBuilder settings, CombatApi combat, ManualLogSource log)
        { _api = api; _settings = settings; _log = log; }
        private void Update()
        {
            if (!Main.inWorld || Main.Player == null || Terrain.singleton == null ||
                Terrain.singleton.worldSpaceTransitionActive || WeatherAndDayTime.singleton == null) return;
            bool night = WeatherAndDayTime.singleton.dayTime == WeatherAndDayTime.DayTime.night;
            if (night && !_night) { _spawned = 0; _spawnAt = Time.time + 2f; _settings.StartedValue?.Invoke(); }
            if (!night && _night) _settings.EndedValue?.Invoke();
            _night = night;
            if (night && _settings.SkipItemValue.HasValue && Input.GetKeyDown(_settings.SkipKeyValue)) TrySkip();
            if (!night || _spawned >= _settings.PerNightValue || Time.time < _spawnAt) return;
            int alive = 0; foreach (EntityInstance entity in _api.Alive) if (!entity.Health.IsDead) alive++;
            if (alive >= _settings.MaxAliveValue) return;
            EntityType type = Pick(); int amount = UnityEngine.Random.Range(type.GroupMinimum, type.GroupMaximum + 1);
            amount = Math.Min(amount, Math.Min(_settings.PerNightValue - _spawned, _settings.MaxAliveValue - alive));
            Vector3Int center;
            if (!TrySpawnCell(out center)) { _spawnAt = Time.time + 1f; return; }
            int group = amount > 1 ? ++_group : 0;
            for (int i = 0; i < amount; i++)
            {
                Vector3Int cell; if (i == 0) cell = center;
                else if (!TryNearby(center, out cell)) continue;
                _api.Spawn(type, cell, group); _spawned++;
            }
            _spawnAt = Time.time + _settings.EveryValue;
        }
        private EntityType Pick()
        {
            int total = 0; foreach (RaidEntry entry in _settings.Entries) total += entry.Weight;
            int roll = UnityEngine.Random.Range(0, total);
            foreach (RaidEntry entry in _settings.Entries) { roll -= entry.Weight; if (roll < 0) return entry.Type; }
            return _settings.Entries[0].Type;
        }
        private bool TrySpawnCell(out Vector3Int cell)
        {
            TerrainPhysicsBody body = Main.Player.GetComponent<TerrainPhysicsBody>();
            Vector3Int player = body == null ? Vector3Int.zero : body.GetIntPosition() + Vertical;
            for (int attempt = 0; attempt < 28; attempt++)
            {
                int x = UnityEngine.Random.Range(-_settings.MaximumDistanceValue, _settings.MaximumDistanceValue + 1);
                int y = UnityEngine.Random.Range(-_settings.MaximumDistanceValue, _settings.MaximumDistanceValue + 1);
                int distance = Math.Abs(x) + Math.Abs(y);
                if (distance < _settings.MinimumDistanceValue || distance > _settings.MaximumDistanceValue) continue;
                if (FindOpen(player + new Vector3Int(x, y, 0), out cell)) return true;
            }
            cell = default(Vector3Int); return false;
        }
        private static bool TryNearby(Vector3Int center, out Vector3Int cell)
        {
            for (int i = 0; i < 12; i++)
                if (FindOpen(center + new Vector3Int(UnityEngine.Random.Range(-2, 3),
                    UnityEngine.Random.Range(-2, 3), 0), out cell)) return true;
            cell = default(Vector3Int); return false;
        }
        private static bool FindOpen(Vector3Int near, out Vector3Int cell)
        {
            for (int z = 3; z >= -4; z--)
            {
                cell = near + Vertical * z; Block ground, feet, head;
                bool hasGround = Terrain.singleton.loadedBlocks.TryGetValue(cell - Vertical, out ground) &&
                    ground != null && ground != Terrain.singleton.waterBlock;
                bool blockedFeet = Terrain.singleton.loadedBlocks.TryGetValue(cell, out feet) && feet != null &&
                    feet != Terrain.singleton.waterBlock;
                bool blockedHead = Terrain.singleton.loadedBlocks.TryGetValue(cell + Vertical, out head) && head != null &&
                    head != Terrain.singleton.waterBlock;
                if (hasGround && !blockedFeet && !blockedHead && !Terrain.singleton.CellContainsWater(cell) &&
                    !Terrain.singleton.CheckForCollider(cell) && !Terrain.singleton.CheckForCollider(cell + Vertical)) return true;
            }
            cell = default(Vector3Int); return false;
        }
        private void TrySkip()
        {
            if (!NearSkipObject()) { InGameComunication.ErrorToPlayer("Move closer to the campfire to skip the night."); return; }
            TimeHandler clock = TimeHandler.singleton;
            if (clock == null || clock.realTimeMinutesPerInGameHour <= 0f) return;
            const float sunrise = 6f;
            TimeHandler.time = sunrise * 60f * clock.realTimeMinutesPerInGameHour;
            TimeHandler.gameTime = sunrise; clock.gameTimeDisplay = sunrise;
            WeatherAndDayTime.singleton.SetDayTime(true);
            InGameComunication.ErrorToPlayer("You rest beside the fire until sunrise.");
        }
        private bool NearSkipObject()
        {
            if (WorldGeneration.generator == null) return false;
            Vector3 player = Main.Player.transform.position;
            string name = _settings.SkipItemValue.Value.ToString();
            foreach (GameObject obj in WorldGeneration.generator.loadedObjects.Values)
                if (obj != null && obj.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0 &&
                    Vector3.Distance(player, obj.transform.position) <= _settings.SkipRangeValue) return true;
            return false;
        }
        private void OnDestroy()
        {
            if (_api == null) return;
            foreach (EntityInstance entity in new List<EntityInstance>(_api.Alive))
                if (entity.GameObject != null) Destroy(entity.GameObject);
        }
    }
}
