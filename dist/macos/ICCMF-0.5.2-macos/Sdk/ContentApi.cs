using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ISOCORE.ModSdk
{
    /// <summary>Registers custom inventory items and placeables with stable namespaced IDs.</summary>
    public sealed class ContentApi
    {
        private static readonly List<ContentApi> Instances = new List<ContentApi>();
        private readonly Dictionary<Items, ContentItem> _items = new Dictionary<Items, ContentItem>();
        private readonly List<ContentRecipe> _recipes = new List<ContentRecipe>();
        private readonly ManualLogSource _log;
        private readonly string _modId;
        private readonly GameObject _templates;
        private SavingAndLoading _saving;

        internal ContentApi(string modId, ManualLogSource log)
        {
            _modId = modId;
            _log = log;
            _templates = new GameObject(modId + " Content Templates");
            _templates.hideFlags = HideFlags.HideAndDontSave;
            _templates.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(_templates);
            Instances.Add(this);
            var harmony = new Harmony(modId + ".iccmf.content");
            harmony.PatchAll(typeof(LookupPatch));
            harmony.PatchAll(typeof(HoverNamePatch));
            harmony.PatchAll(typeof(InfoNamePatch));
            harmony.PatchAll(typeof(CollectionNamePatch));
        }

        /// <summary>
        /// Returns the stable ID for a key of this mod. IDs start at 40000, are derived from
        /// "modId:key", and are coordinated across every mod that uses the SDK: if the preferred
        /// ID is already taken, the next free one is used.
        /// </summary>
        public int Id(string key)
        {
            return ItemIdRegistry.Claim(Key(key), _log);
        }

        public ItemBuilder Item(string key, string name)
        {
            return new ItemBuilder(this, key, name, false);
        }

        public PlaceableBuilder Placeable(string key, string name)
        {
            return new PlaceableBuilder(this, key, name);
        }

        public RecipeBuilder Recipe(ContentItem result, int amount = 1)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            return new RecipeBuilder(this, result, amount);
        }

        public ContentItem Find(int id)
        {
            _items.TryGetValue((Items)id, out ContentItem item);
            return item;
        }

        internal ContentItem Register(ItemBuilder builder)
        {
            string owner = Key(builder.KeyValue);
            int numericId = builder.ExplicitId.HasValue
                ? ItemIdRegistry.ClaimExplicit(owner, builder.ExplicitId.Value)
                : ItemIdRegistry.Claim(owner, _log);
            Items id = (Items)numericId;
            if (_items.ContainsKey(id))
                throw new InvalidOperationException("This mod already registered item ID " + numericId + ".");

            var item = new ContentItem(numericId, builder.KeyValue, builder.NameValue,
                builder.DescriptionValue, builder.IconValue, builder.StackableValue,
                builder is PlaceableBuilder ? (PlaceableBuilder)builder : null);
            _items.Add(id, item);
            if (_saving != null) Publish(item, _saving);
            return item;
        }

        internal ContentRecipe Register(RecipeBuilder builder)
        {
            var recipe = new ContentRecipe(builder.ResultValue, builder.AmountValue,
                builder.IngredientsValue.ToArray(), builder.StationValue);
            _recipes.Add(recipe);
            if (_saving != null) Publish(recipe);
            return recipe;
        }

        private string Key(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Item key is required.");
            return _modId + ":" + key.Trim().ToLowerInvariant();
        }

        private void PublishAll(SavingAndLoading saving)
        {
            _saving = saving;
            // One failing item or recipe must not stop the rest of the mod from registering.
            foreach (ContentItem item in _items.Values)
            {
                try { Publish(item, saving); }
                catch (Exception error)
                {
                    _log?.LogError("Could not register item '" + item.Name + "' (" + item.Id + "): " +
                        error.Message);
                }
            }
            foreach (ContentRecipe recipe in _recipes)
            {
                try { Publish(recipe); }
                catch (Exception error)
                {
                    _log?.LogError("Could not register recipe for '" + recipe.Result.Name + "': " +
                        error.Message);
                }
            }
        }

        private void Publish(ContentRecipe recipe)
        {
            if (recipe.Product == null)
            {
                var template = new GameObject(_modId + " recipe " + recipe.Result.Id);
                template.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.Object.DontDestroyOnLoad(template);
                recipe.Product = template.AddComponent<Product>();
                recipe.Product.item = recipe.Result.GameId;
                recipe.Product.ProductionFactor = recipe.Amount;
                recipe.Product.Ingredients = recipe.Ingredients;
            }

            GameObject station = null;
            ContentItem customStation = Find((int)recipe.Station);
            if (customStation != null) station = customStation.Prefab;
            if (station == null) station = FindPlaceablePrefab(recipe.Station);
            Processing processing = station == null ? null : station.GetComponent<Processing>();
            if (processing == null)
                throw new InvalidOperationException("Recipe station is not a processing placeable: " +
                    (ContentApi.DisplayName(recipe.Station) ?? recipe.Station.ToString()));
            GameObject[] existing = processing.producableItems ?? Array.Empty<GameObject>();
            if (!existing.Contains(recipe.Product.gameObject))
                processing.producableItems = existing.Concat(new[] { recipe.Product.gameObject }).ToArray();
        }

        private void Publish(ContentItem definition, SavingAndLoading saving)
        {
            Items id = definition.GameId;
            if (PlayerInventory.itemLookup.TryGetValue(id, out Item occupied) &&
                occupied != definition.Asset)
                throw new InvalidOperationException("Item ID " + definition.Id +
                    " is already used by " + occupied.name + ".");
            if (definition.Asset == null) definition.Asset = CreateAsset(definition, saving);
            PlayerInventory.itemLookup[id] = definition.Asset;
            if (definition.Prefab != null) PublishPrefabTree(definition.Prefab, saving);
            _log?.LogInfo("Registered " + definition.Name + " as item " + definition.Id + ".");
        }

        private Item CreateAsset(ContentItem definition, SavingAndLoading saving)
        {
            Item asset;
            if (definition.Placeable == null)
                asset = ScriptableObject.CreateInstance<SdkInventoryItem>();
            else
            {
                GameObject source = FindPlaceablePrefab(definition.Placeable.SourceId);
                if (source == null)
                    throw new InvalidOperationException("Placeable '" + definition.Name +
                        "' could not clone base item " + (int)definition.Placeable.SourceId + ".");
                definition.Prefab = ClonePlaceableTree(source, definition, new Dictionary<GameObject, GameObject>());
                var placeable = ScriptableObject.CreateInstance<PlaceableItem>();
                placeable.Prefab = definition.Prefab;
                asset = placeable;
            }
            asset.name = definition.Name;
            asset.hideFlags = HideFlags.HideAndDontSave;
            asset.item = definition.GameId;
            asset.description = definition.Description;
            asset.noStack = !definition.Stackable;
            asset.icon = definition.Icon;
            return asset;
        }

        private static GameObject FindPlaceablePrefab(Items sourceId)
        {
            if (!PlayerInventory.itemLookup.TryGetValue(sourceId, out Item source)) return null;
            return (source as PlaceableItem)?.Prefab;
        }

        private GameObject ClonePlaceableTree(GameObject source, ContentItem item,
            Dictionary<GameObject, GameObject> clones)
        {
            if (source == null) return null;
            if (clones.TryGetValue(source, out GameObject known)) return known;

            GameObject clone = UnityEngine.Object.Instantiate(source, _templates.transform, false);
            clones.Add(source, clone);
            clone.name = _modId.Replace('.', '_') + "_" + item.Key +
                (clones.Count == 1 ? "" : "_" + source.name);
            // The inactive parent keeps this object out of the live world. Leave the actual
            // prefab unflagged so instances made by the game remain visible and saveable.
            clone.hideFlags = HideFlags.None;
            clone.SetActive(false);

            if (item.Placeable.PlacedSpriteValue != null)
                ApplyPlaceableAppearance(clone, item.Placeable.PlacedSpriteValue);

            Drop drop = clone.GetComponent<Drop>();
            if (drop != null)
            {
                drop.dropItems = true;
                drop.itemDrops = new[]
                {
                    new Drop.ItemDrop { item = item.GameId, min = 1, max = 1, durability = 0 }
                };
            }

            item.Placeable.ConfigureValue?.Invoke(clone);
            ObjectStats sourceStats = source.GetComponent<ObjectStats>();
            ObjectStats cloneStats = clone.GetComponent<ObjectStats>();
            if (sourceStats != null && cloneStats != null && sourceStats.rotatable)
            {
                cloneStats.rotation_Clockwise = ClonePlaceableTree(sourceStats.rotation_Clockwise,
                    item, clones) ?? clone;
                cloneStats.rotation_CounterClockwise = ClonePlaceableTree(sourceStats.rotation_CounterClockwise,
                    item, clones) ?? clone;
            }
            clone.SetActive(true);
            return clone;
        }

        private static void ApplyPlaceableAppearance(GameObject clone, Sprite supplied)
        {
            SpriteRenderer[] renderers = clone.GetComponentsInChildren<SpriteRenderer>(true);
            StaticLayer layer = clone.GetComponent<StaticLayer>();
            SpriteRenderer main = layer == null || layer.img == null
                ? null : layer.img.GetComponent<SpriteRenderer>();
            if (main == null || main.sprite == null)
                main = Array.Find(renderers, renderer => renderer.enabled && renderer.sprite != null);
            if (main == null || main.sprite == null) return;

            Sprite original = main.sprite;
            float originalWidth = Mathf.Max(.01f, original.bounds.size.x);
            Vector2 pivot = new Vector2(original.pivot.x / original.rect.width,
                original.pivot.y / original.rect.height);
            Sprite appearance = Sprite.Create(supplied.texture, supplied.rect, pivot,
                supplied.rect.width / originalWidth, 0, SpriteMeshType.FullRect);
            appearance.name = supplied.name + " " + clone.name;
            appearance.hideFlags = HideFlags.HideAndDontSave;

            foreach (SpriteRenderer renderer in renderers)
            {
                if (renderer.sprite != original) continue;
                renderer.sprite = appearance;
                renderer.color = Color.white;
            }
            foreach (NoiseBasedSprite noise in clone.GetComponentsInChildren<NoiseBasedSprite>(true))
                noise.enabled = false;
            foreach (RandomSprite random in clone.GetComponentsInChildren<RandomSprite>(true))
                random.sprites = new[] { appearance };
        }

        private static void PublishPrefabTree(GameObject root, SavingAndLoading saving)
        {
            var pending = new Queue<GameObject>();
            var visited = new HashSet<GameObject>();
            pending.Enqueue(root);
            while (pending.Count > 0)
            {
                GameObject prefab = pending.Dequeue();
                if (prefab == null || !visited.Add(prefab)) continue;
                if (saving.prefabLooupTable.TryGetValue(prefab.name, out GameObject occupied) &&
                    occupied != prefab)
                    throw new InvalidOperationException("Prefab name is already used: " + prefab.name);
                saving.prefabLooupTable[prefab.name] = prefab;
                ObjectStats stats = prefab.GetComponent<ObjectStats>();
                if (stats != null && stats.rotatable)
                {
                    pending.Enqueue(stats.rotation_Clockwise);
                    pending.Enqueue(stats.rotation_CounterClockwise);
                }
            }
        }

        internal static string DisplayName(Items id)
        {
            foreach (ContentApi api in Instances)
                if (api._items.TryGetValue(id, out ContentItem item)) return item.Name;
            return null;
        }

        [HarmonyPatch(typeof(SavingAndLoading), "PopulateLookupTables")]
        private static class LookupPatch
        {
            private static void Postfix(SavingAndLoading __instance)
            {
                foreach (ContentApi api in Instances.ToArray())
                {
                    try { api.PublishAll(__instance); }
                    catch (Exception error) { api._log.LogError("Content registration failed: " + error); }
                }
            }
        }

        [HarmonyPatch(typeof(HoverDisplay), nameof(HoverDisplay.SetItem))]
        private static class HoverNamePatch
        {
            private static void Postfix(HoverDisplay __instance, Items __0)
            {
                string name = DisplayName(__0);
                if (name != null) __instance.text.text = name;
            }
        }

        [HarmonyPatch(typeof(InfoUIHandler), nameof(InfoUIHandler.ActivateItemInfoUI))]
        private static class InfoNamePatch
        {
            private static void Postfix(InfoUIHandler __instance, Items __0)
            {
                string name = DisplayName(__0);
                if (name != null) __instance.itemName.text = name;
            }
        }

        [HarmonyPatch(typeof(UI_Controler), nameof(UI_Controler.DisplayItemCollection))]
        private static class CollectionNamePatch
        {
            private static void Postfix(UI_Controler __instance, Items __0)
            {
                string name = DisplayName(__0);
                if (name == null || __instance.ItemCollectionDisplayContainer == null) return;
                foreach (Transform row in __instance.ItemCollectionDisplayContainer.transform)
                {
                    collectDisplayValues label = row.GetComponent<collectDisplayValues>();
                    if (label == null || label.item != __0) continue;
                    label.text.text = name + (label.amount > 0 ? " " : ": ") + label.amount;
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Item ID bookkeeping shared by EVERY mod that embeds the SDK. Each mod has its own copy of
    /// this class, so the maps live in AppDomain data, which all copies can see. That lets two
    /// mods detect each other's claimed IDs and avoid handing out the same number twice.
    /// </summary>
    internal static class ItemIdRegistry
    {
        /// <summary>First ID used for custom items. Vanilla items stay below this.</summary>
        internal const int FirstCustomId = 40000;

        // How many IDs the hash is spread over. Lower = shorter numbers but more collisions
        // (collisions are resolved safely, but resolved IDs depend on mod load order).
        private const uint Span = 2000000000u;

        private static readonly Dictionary<int, string> Owners =
            Shared<Dictionary<int, string>>("ISOCORE.ModSdk.ItemIds.Owners");
        private static readonly Dictionary<string, int> Ids =
            Shared<Dictionary<string, int>>("ISOCORE.ModSdk.ItemIds.Ids");

        private static T Shared<T>(string name) where T : class, new()
        {
            lock (AppDomain.CurrentDomain)
            {
                T value = AppDomain.CurrentDomain.GetData(name) as T;
                if (value == null)
                {
                    value = new T();
                    AppDomain.CurrentDomain.SetData(name, value);
                }
                return value;
            }
        }

        /// <summary>Gets (or assigns) the ID for a namespaced key such as "guns:pistol".</summary>
        internal static int Claim(string fullKey, ManualLogSource log)
        {
            lock (Owners)
            {
                if (Ids.TryGetValue(fullKey, out int known)) return known;

                uint hash = 2166136261;
                foreach (char character in fullKey)
                {
                    hash ^= character;
                    hash *= 16777619;
                }

                int preferred = FirstCustomId + (int)(hash % Span);
                int id = preferred;
                while (Taken(id))
                {
                    id++;
                    if (id >= FirstCustomId + (int)Span) id = FirstCustomId;
                }

                if (id != preferred)
                    log?.LogWarning("Item ID " + preferred + " for '" + fullKey +
                        "' was already taken (" + OwnerOf(preferred) + "); using " + id + " instead.");

                Owners[id] = fullKey;
                Ids[fullKey] = id;
                return id;
            }
        }

        /// <summary>Claims a specific ID chosen by the mod author. Throws if another item owns it.</summary>
        internal static int ClaimExplicit(string fullKey, int id)
        {
            lock (Owners)
            {
                if (Owners.TryGetValue(id, out string owner) && owner != fullKey)
                    throw new InvalidOperationException("Item ID " + id + " belongs to '" + owner + "'.");
                Owners[id] = fullKey;
                Ids[fullKey] = id;
                return id;
            }
        }

        private static bool Taken(int id)
        {
            return Owners.ContainsKey(id) || Enum.IsDefined(typeof(Items), (Items)id);
        }

        private static string OwnerOf(int id)
        {
            return Owners.TryGetValue(id, out string owner) ? owner : "vanilla item";
        }
    }

    public class ItemBuilder
    {
        private readonly ContentApi _content;
        internal string KeyValue { get; }
        internal string NameValue { get; }
        internal string DescriptionValue { get; private set; } = "";
        internal Sprite IconValue { get; private set; }
        internal bool StackableValue { get; private set; } = true;
        internal int? ExplicitId { get; private set; }

        internal ItemBuilder(ContentApi content, string key, string name, bool placeable)
        {
            _content = content;
            KeyValue = key;
            NameValue = string.IsNullOrWhiteSpace(name) ? key : name;
        }

        public ItemBuilder Description(string text) { DescriptionValue = text ?? ""; return this; }
        public ItemBuilder Icon(Sprite sprite) { IconValue = sprite; return this; }
        public ItemBuilder Stackable(bool stackable = true) { StackableValue = stackable; return this; }
        public ItemBuilder NonStackable() { StackableValue = false; return this; }
        public ItemBuilder WithId(int id)
        {
            if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id));
            ExplicitId = id;
            return this;
        }
        public virtual ContentItem Register() { return _content.Register(this); }
    }

    public sealed class PlaceableBuilder : ItemBuilder
    {
        internal Items SourceId { get; private set; } = Items.CraftingTable;
        internal Sprite PlacedSpriteValue { get; private set; }
        internal Action<GameObject> ConfigureValue { get; private set; }

        internal PlaceableBuilder(ContentApi content, string key, string name)
            : base(content, key, name, true) { }

        public PlaceableBuilder Clone(Items baseItem) { SourceId = baseItem; return this; }
        public PlaceableBuilder PlacedSprite(Sprite sprite) { PlacedSpriteValue = sprite; return this; }
        public PlaceableBuilder Configure(Action<GameObject> configure) { ConfigureValue = configure; return this; }
        public new PlaceableBuilder Description(string text) { base.Description(text); return this; }
        public new PlaceableBuilder Icon(Sprite sprite) { base.Icon(sprite); return this; }
        public new PlaceableBuilder Stackable(bool stackable = true) { base.Stackable(stackable); return this; }
        public new PlaceableBuilder NonStackable() { base.NonStackable(); return this; }
        public new PlaceableBuilder WithId(int id) { base.WithId(id); return this; }
    }

    public sealed class ContentItem
    {
        internal ContentItem(int id, string key, string name, string description, Sprite icon,
            bool stackable, PlaceableBuilder placeable)
        {
            Id = id; Key = key; Name = name; Description = description; Icon = icon;
            Stackable = stackable; Placeable = placeable;
        }

        public int Id { get; }
        public Items GameId => (Items)Id;
        public string Key { get; }
        public string Name { get; }
        public string Description { get; }
        public Sprite Icon { get; }
        public bool Stackable { get; }
        public Item Asset { get; internal set; }
        public GameObject Prefab { get; internal set; }
        internal PlaceableBuilder Placeable { get; }
        public static implicit operator Items(ContentItem item) => item.GameId;
        public override string ToString() => Name + " (" + Id + ")";
    }

    internal sealed class SdkInventoryItem : Item { }

    public sealed class RecipeBuilder
    {
        private readonly ContentApi _content;
        internal ContentItem ResultValue { get; }
        internal int AmountValue { get; }
        internal List<ItemStack> IngredientsValue { get; } = new List<ItemStack>();
        internal Items StationValue { get; private set; } = Items.CraftingTable;

        internal RecipeBuilder(ContentApi content, ContentItem result, int amount)
        {
            _content = content;
            ResultValue = result;
            AmountValue = Math.Max(1, amount);
        }

        public RecipeBuilder Ingredient(Items item, int amount = 1)
        { IngredientsValue.Add(new ItemStack(item, Math.Max(1, amount))); return this; }
        public RecipeBuilder Ingredient(ContentItem item, int amount = 1)
        { return Ingredient(item.GameId, amount); }
        public RecipeBuilder At(Items station) { StationValue = station; return this; }
        public RecipeBuilder At(ContentItem station) { StationValue = station.GameId; return this; }
        public ContentRecipe Register() { return _content.Register(this); }
    }

    public sealed class ContentRecipe
    {
        internal ContentRecipe(ContentItem result, int amount, ItemStack[] ingredients, Items station)
        { Result = result; Amount = amount; Ingredients = ingredients; Station = station; }
        public ContentItem Result { get; }
        public int Amount { get; }
        public ItemStack[] Ingredients { get; }
        public Items Station { get; }
        public Product Product { get; internal set; }
    }
}