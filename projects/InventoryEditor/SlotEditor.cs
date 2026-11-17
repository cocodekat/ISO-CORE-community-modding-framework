using System;
using System.Collections.Generic;
using System.Linq;
using ISOCORE.ModSdk;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ISOCORE.SimpleInventoryEditor
{
    /// <summary>A self-contained view for choosing the new contents of one inventory slot.</summary>
    internal sealed class SlotEditor
    {
        private readonly MenuApi _ui;
        private readonly InventoryApi _inventory;
        private readonly ModMenu _menu;
        private readonly UiState<string> _search;
        private readonly UiState<int> _quantity;
        private readonly UiState<int> _durability;
        private readonly UiState<string> _selection;
        private readonly UiState<string> _message;

        private IReadOnlyList<InventoryItem> _items = Array.Empty<InventoryItem>();
        private ModScrollList _results;
        private InventorySlot _slot;
        private InventoryItem _item;

        internal SlotEditor(MenuApi ui, InventoryApi inventory)
        {
            _ui = ui;
            _inventory = inventory;
            _search = ui.State("");
            _quantity = ui.State(1);
            _durability = ui.State(0);
            _selection = ui.State("");
            _message = ui.State("");

            _search.Changed += delegate { ShowItems(fromTop: true); };
            _menu = ui.Menu("Edit slot", new Vector2(440f, 540f), Build)
                .CloseOn(Key.Escape);
        }

        internal void Open(InventorySlot slot)
        {
            Close();
            _slot = slot;
            _item = slot.IsEmpty ? null : slot.Item;
            _items = _inventory.GetItems();
            _menu.SetOpen(true);
        }

        internal void Close() => _menu.Close();
        internal void Destroy() => _menu.Destroy();

        private void Build(ModContainer content)
        {
            ModContainer page = content.Column(spacing: 6f).Fill();

            page.Text("", 16f, 36f).Bind(_selection);
            page.Input("Search name or item ID", 32f).Bind(_search);
            _results = page.ScrollList(height: 210f).WheelSpeed(5f);

            ModContainer values = page.Row(spacing: 8f, expandHeight: true).Layout(54f);
            NumberField(values, "QUANTITY", _quantity);
            NumberField(values, "DURABILITY", _durability);

            page.Text("", 13f, 18f, color: _ui.Theme.Muted,
                alignment: TextAlignmentOptions.Center).Bind(_message);

            ModContainer actions = page.Row(spacing: 8f, expandHeight: true).Layout(32f);
            actions.Button("APPLY", Apply, ButtonTone.Primary, 32f, 14f);
            actions.Button("CLEAR", Clear, ButtonTone.Danger, 32f, 14f);
            actions.Button("CANCEL", Close, height: 32f, fontSize: 14f);

            _quantity.Value = Math.Max(1, _slot.Quantity);
            _durability.Value = _item != null && _item.MaxDurability > 0
                ? _slot.Durability : 0;
            _search.Value = "";
            ShowSelection();
            ShowItems(fromTop: true);
        }

        private static void NumberField(ModContainer row, string label, UiState<int> value)
        {
            ModContainer field = row.Column(spacing: 2f).Layout(54f);
            field.Text(label, 13f, 20f);
            field.Input("0", 32f).Bind(value);
        }

        private void ShowItems(bool fromTop)
        {
            if (_results == null) return;

            float previousPosition = fromTop ? 0f : _results.Offset;
            _results.Clear(fromTop);
            string query = (_search.Value ?? "").Trim();
            InventoryItem[] matches = _items.Where(item =>
                item.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                item.Id.ToString().Contains(query))
                .OrderBy(item => item.Id)
                .ToArray();

            foreach (InventoryItem item in matches)
            {
                InventoryItem choice = item;
                string details = "ID " + item.Id + (item.IsAvailable ? "" : "  · N/A");
                _results.AddItem(item.Name, details, item.Icon,
                    _item != null && item.Id == _item.Id,
                    delegate { Select(choice); }, 32f);
            }

            if (!fromTop) _results.Offset = previousPosition;
            _message.Value = matches.Length == 0 ? "No items found." :
                matches.Length + (matches.Length == 1 ? " item" : " items");
        }

        private void Select(InventoryItem item)
        {
            _item = item;
            _quantity.Value = Math.Max(1, _quantity.Value);
            _durability.Value = item.MaxDurability > 0 ? item.MaxDurability : 0;
            ShowSelection();
            ShowItems(fromTop: false);
        }

        private void ShowSelection()
        {
            _selection.Value = "SLOT " + _slot.Number + "    " +
                (_item == null ? "Empty" : _item.Name + "\nID " + _item.Id);
        }

        private void Apply()
        {
            if (_item == null || _quantity.Value < 1)
            {
                _message.Value = "Choose an item and enter a whole quantity.";
                return;
            }

            TryChangeSlot(delegate
            {
                int? durability = _item.MaxDurability > 0 ? _durability.Value : (int?)null;
                _slot.Set(_item.Id, _quantity.Value, durability);
            });
        }

        private void Clear() => TryChangeSlot(_slot.Clear);

        private void TryChangeSlot(Action change)
        {
            try
            {
                change();
                Close();
            }
            catch (InvalidOperationException error)
            {
                _message.Value = error.Message;
            }
        }
    }
}
