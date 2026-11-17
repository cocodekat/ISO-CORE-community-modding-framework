using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ISOCORE.ModSdk
{
    public sealed class InventoryItem
    {
        internal InventoryItem(int id, string name, int maxQuantity, int maxDurability,
            bool isAvailable, Sprite icon)
        {
            Id = id;
            Name = name;
            MaxQuantity = maxQuantity;
            MaxDurability = maxDurability;
            IsAvailable = isAvailable;
            Icon = icon;
        }

        public int Id { get; }
        public string Name { get; }
        public int MaxQuantity { get; }
        public int MaxDurability { get; }
        public bool IsAvailable { get; }
        public Sprite Icon { get; }
    }

    public sealed class InventorySlot
    {
        private readonly InventoryApi _inventory;

        internal InventorySlot(InventoryApi inventory, Storage storage, int index, ItemStack stack)
        {
            _inventory = inventory;
            Storage = storage;
            Stack = stack;
            Index = index;
        }

        internal Storage Storage { get; }
        internal ItemStack Stack { get; }
        public int Index { get; }
        public int Number => Index + 1;
        public bool IsEmpty => Stack == null || Stack.item == Items.noItem || Stack.amount <= 0;
        public InventoryItem Item => _inventory.GetItem(Id);
        public string Name => IsEmpty ? "Empty" : Item?.Name ?? Stack.item.ToString();

        public int Id
        {
            get { return Stack == null ? (int)Items.noItem : (int)Stack.item; }
            set
            {
                if ((Items)value == Items.noItem) { Clear(); return; }
                Set(value, Quantity > 0 ? Quantity : 1, null);
            }
        }

        public int ItemId
        {
            get { return Id; }
            set { Id = value; }
        }

        public int Quantity
        {
            get { return Stack == null ? 0 : Stack.amount; }
            set
            {
                if (value <= 0) { Clear(); return; }
                if (IsEmpty) throw new InvalidOperationException(
                    "Set the slot Id before setting a positive quantity.");
                Set(Id, value, null);
            }
        }

        public int Durability
        {
            get { return Stack == null ? 0 : Stack.remainingDurability; }
            set
            {
                if (IsEmpty) throw new InvalidOperationException(
                    "An empty slot has no durability.");
                Set(Id, Quantity, value);
            }
        }

        public void Set(int itemId, int quantity = 1, int? durability = null)
        {
            string error;
            if (!_inventory.TrySetSlot(this, itemId, quantity, durability, out error))
                throw new InvalidOperationException(error);
        }

        public void Clear()
        {
            string error;
            if (!_inventory.TryClearSlot(this, out error))
                throw new InvalidOperationException(error);
        }
    }

    public sealed class InventorySlotClickEventArgs : EventArgs
    {
        internal InventorySlotClickEventArgs(InventorySlot slot) => Slot = slot;

        public InventorySlot Slot { get; }

        // Set this when a mod replaces the native slot click, for example while edit mode is active.
        public bool Handled { get; set; }
    }

    public sealed class InventoryApi
    {
        private static InventoryApi _current;
        private static readonly FieldInfo HighlightedField =
            AccessTools.Field(typeof(InventoryDisplay), "highlighted");
        private readonly ManualLogSource _log;
        private Harmony _harmony;
        private bool _openedByPatch;
        private bool _iconMemberSearched;
        private FieldInfo _iconField;
        private PropertyInfo _iconProperty;

        internal InventoryApi(ManualLogSource log)
        {
            _current = this;
            _log = log;
            InstallHooks();
        }

        public event Action Opened;
        public event Action Closed;
        public event EventHandler<InventorySlotClickEventArgs> SlotClicked;

        public bool IsOpen => (_openedByPatch || Main.inInventory) && PlayerInventory.inventory != null;

        public IReadOnlyList<InventoryItem> GetItems()
        {
            var items = PlayerInventory.itemLookup;
            IEnumerable<Items> registered = (Items[])Enum.GetValues(typeof(Items));
            if (items != null) registered = registered.Concat(items.Keys);

            return registered
                .Where(id => id != Items.noItem)
                .Distinct()
                .Select(id =>
                {
                    Item item = null;
                    bool available = items != null && items.TryGetValue(id, out item) && item != null;
                    string fallback = Enum.IsDefined(typeof(Items), id)
                        ? id.ToString() : "Item " + (int)id;
                    string name = PrettyName(available && !string.IsNullOrWhiteSpace(item.name)
                        ? item.name : fallback);
                    return new InventoryItem((int)id, name,
                        available && item.noStack ? 1 : Storage.maxAmountPerSlot,
                        available && item is ToolItem tool ? tool.durability : 0,
                        available, available ? GetIcon(item) : null);
                })
                .OrderBy(item => item.Id).ToArray();
        }

        public InventoryItem GetItem(int id)
        {
            return GetItems().FirstOrDefault(item => item.Id == id);
        }

        private static string PrettyName(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            string spaced = Regex.Replace(value, "(?<=[a-z0-9])(?=[A-Z])", " ")
                .Replace('_', ' ');
            return char.ToUpperInvariant(spaced[0]) + spaced.Substring(1);
        }

        private Sprite GetIcon(Item item)
        {
            if (item == null) return null;
            if (!_iconMemberSearched) FindIconMember();
            try
            {
                return _iconField != null ? _iconField.GetValue(item) as Sprite :
                    _iconProperty != null ? _iconProperty.GetValue(item, null) as Sprite : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void FindIconMember()
        {
            _iconMemberSearched = true;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            int bestScore = -1;
            for (Type type = typeof(Item); type != null && type != typeof(object); type = type.BaseType)
            {
                foreach (FieldInfo field in type.GetFields(flags))
                {
                    if (field.FieldType != typeof(Sprite)) continue;
                    int score = IconScore(field.Name);
                    if (score <= bestScore) continue;
                    bestScore = score;
                    _iconField = field;
                    _iconProperty = null;
                }
                foreach (PropertyInfo property in type.GetProperties(flags))
                {
                    if (property.PropertyType != typeof(Sprite) || !property.CanRead ||
                        property.GetIndexParameters().Length != 0) continue;
                    int score = IconScore(property.Name);
                    if (score <= bestScore) continue;
                    bestScore = score;
                    _iconField = null;
                    _iconProperty = property;
                }
            }
            _log?.LogInfo("Inventory item icons: " + (_iconField != null
                ? "field " + _iconField.Name : _iconProperty != null
                    ? "property " + _iconProperty.Name : "no Sprite member found"));
        }

        private static int IconScore(string name)
        {
            string lower = name.ToLowerInvariant();
            if (lower.Contains("icon")) return 3;
            if (lower.Contains("sprite")) return 2;
            if (lower.Contains("image")) return 1;
            return 0;
        }

        public int SlotCount => PlayerInventory.inventory?.storage?.Length ?? 0;

        /// <summary>One-based slot access: Inventory[1] is the first visible inventory slot.</summary>
        public InventorySlot this[int slotNumber] => Slot(slotNumber);

        /// <summary>Gets a slot by its one-based number, matching the number shown to players.</summary>
        public InventorySlot Slot(int number) => GetSlot(number - 1);

        public InventorySlot Slot1 => Slot(1);
        public InventorySlot Slot2 => Slot(2);
        public InventorySlot Slot3 => Slot(3);
        public InventorySlot Slot4 => Slot(4);
        public InventorySlot Slot5 => Slot(5);
        public InventorySlot Slot6 => Slot(6);
        public InventorySlot Slot7 => Slot(7);
        public InventorySlot Slot8 => Slot(8);
        public InventorySlot Slot9 => Slot(9);
        public InventorySlot Slot10 => Slot(10);
        public InventorySlot Slot11 => Slot(11);
        public InventorySlot Slot12 => Slot(12);
        public InventorySlot Slot13 => Slot(13);
        public InventorySlot Slot14 => Slot(14);
        public InventorySlot Slot15 => Slot(15);
        public InventorySlot Slot16 => Slot(16);
        public InventorySlot Slot17 => Slot(17);
        public InventorySlot Slot18 => Slot(18);
        public InventorySlot Slot19 => Slot(19);
        public InventorySlot Slot20 => Slot(20);
        public InventorySlot Slot21 => Slot(21);
        public InventorySlot Slot22 => Slot(22);
        public InventorySlot Slot23 => Slot(23);
        public InventorySlot Slot24 => Slot(24);

        /// <summary>Returns the currently highlighted hotbar slot, or null when none is equipped.</summary>
        public InventorySlot Equipped()
        {
            Storage inventory = PlayerInventory.inventory;
            if (inventory?.storage == null) return null;
            foreach (InventoryDisplay display in
                UnityEngine.Object.FindObjectsOfType<InventoryDisplay>())
            {
                bool highlighted = display != null && HighlightedField != null &&
                    (bool)HighlightedField.GetValue(display);
                if (!highlighted || display.slot == null) continue;
                int index = inventory.GetIndex(display.slot);
                if (index >= 0 && index < inventory.storage.Length)
                    return new InventorySlot(this, inventory, index, display.slot);
            }
            return null;
        }

        // Use the returned snapshot to guard against swapping saves or editing a changed slot.
        public InventorySlot GetSlot(int index)
        {
            Storage inventory = PlayerInventory.inventory;
            if (inventory?.storage == null || index < 0 || index >= inventory.storage.Length) return null;
            ItemStack stack = inventory.storage[index];
            return stack == null ? null : new InventorySlot(this, inventory, index, stack);
        }

        public bool TrySetSlot(InventorySlot expected, int itemId, int quantity, int? durability, out string error)
        {
            if (!TryGetCurrentSlot(expected, out InventorySlot current, out error)) return false;
            if (quantity < 1) { error = "Quantity must be positive. Use Clear slot to empty it."; return false; }
            var items = PlayerInventory.itemLookup;
            if (items == null || (Items)itemId == Items.noItem ||
                !items.TryGetValue((Items)itemId, out Item item) || item == null)
            {
                error = "This item is unavailable.";
                return false;
            }

            int maximum = item.noStack ? 1 : Storage.maxAmountPerSlot;
            if (quantity > maximum)
            {
                error = "This item holds up to " + maximum + " per slot.";
                return false;
            }
            int maxDurability = item is ToolItem tool ? tool.durability : 0;
            int remaining = durability ?? (current.ItemId == itemId && current.Quantity > 0
                ? Math.Min(current.Durability, maxDurability) : maxDurability);
            if (remaining < 0 || remaining > maxDurability)
            {
                error = "Durability must be between 0 and " + maxDurability + ".";
                return false;
            }

            Write(current.Stack, (Items)itemId, quantity, remaining);
            return true;
        }

        public bool TryClearSlot(InventorySlot expected, out string error)
        {
            if (!TryGetCurrentSlot(expected, out InventorySlot current, out error)) return false;
            Write(current.Stack, Items.noItem, 0, 0);
            return true;
        }

        private bool TryGetCurrentSlot(InventorySlot expected, out InventorySlot current, out string error)
        {
            current = expected == null ? null : GetSlot(expected.Index);
            if (current == null || !ReferenceEquals(expected.Storage, current.Storage) ||
                !ReferenceEquals(expected.Stack, current.Stack))
            {
                error = "The slot changed. Select it again.";
                return false;
            }
            error = null;
            return true;
        }

        private static void Write(ItemStack stack, Items item, int quantity, int durability)
        {
            stack.item = item;
            stack.amount = quantity;
            stack.remainingDurability = durability;
            stack.updated = true;
            foreach (InventoryDisplay display in UnityEngine.Object.FindObjectsOfType<InventoryDisplay>())
                if (ReferenceEquals(display.slot, stack)) display.SetUp();
        }

        private void InstallHooks()
        {
            try
            {
                string id = "isocore.modsdk.inventory." + GetType().Assembly.GetName().Name;
                _harmony = new Harmony(id);
                _harmony.Patch(AccessTools.Method(typeof(InventoryUI), "Open"),
                    postfix: new HarmonyMethod(typeof(InventoryApi), nameof(InventoryOpenPostfix)));
                _harmony.Patch(AccessTools.Method(typeof(InventoryUI), "Close"),
                    prefix: new HarmonyMethod(typeof(InventoryApi), nameof(InventoryClosePrefix)));
                _harmony.Patch(AccessTools.Method(typeof(InventoryDisplay), "Clicked"),
                    prefix: new HarmonyMethod(typeof(InventoryApi), nameof(InventoryClickedPrefix)));
                _log?.LogInfo("Inventory API hooks installed: open, close, and native slot clicks.");
            }
            catch (Exception error)
            {
                _log?.LogError("Inventory API hooks failed: " + error);
            }
        }

        private void OnOpened()
        {
            _openedByPatch = true;
            Opened?.Invoke();
        }

        private void OnClosed()
        {
            _openedByPatch = false;
            Closed?.Invoke();
        }

        private bool OnSlotClicked(InventoryDisplay display)
        {
            Storage inventory = PlayerInventory.inventory;
            if (display == null || display.slot == null || inventory?.storage == null || !IsOpen)
                return false;
            int index = inventory.GetIndex(display.slot);
            if (index < 0 || index >= inventory.storage.Length) return false;
            var args = new InventorySlotClickEventArgs(
                new InventorySlot(this, inventory, index, display.slot));
            SlotClicked?.Invoke(this, args);
            return args.Handled;
        }

        private static void InventoryOpenPostfix(bool __result)
        {
            if (__result) _current?.OnOpened();
        }

        private static void InventoryClosePrefix() => _current?.OnClosed();

        private static bool InventoryClickedPrefix(InventoryDisplay __instance)
        {
            return _current == null || !_current.OnSlotClicked(__instance);
        }
    }
}
