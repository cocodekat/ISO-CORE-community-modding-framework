using ISOCORE.ModSdk;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ISOCORE.SimpleInventoryEditor
{
    /// <summary>
    /// Coordinates the game inventory and our editor. The visual editor lives in SlotEditor,
    /// so this file reads as the complete behaviour of the mod instead of a pile of UI details.
    /// </summary>
    public sealed class InventoryEditor : ModComponent
    {
        private ModButton _editButton;
        private SlotEditor _slotEditor;
        private bool _selectingSlot;

        protected override void OnModStart()
        {
            _slotEditor = new SlotEditor(Ui, Inventory);

            _editButton = Ui.Button("EDIT SLOT  [F8]", fontSize: 13f,
                    size: new Vector2(148f, 28f), roundedEdges: 2f)
                .OnClick(GuardUi(ToggleSlotSelection))
                .VisibleWhen(() => Inventory.IsOpen, UiAnchor.TopRight,
                    new Vector2(-14f, -28f))
                .Hotkey(Key.F8);

            WhenSlotClicked(EditSelectedSlot);
            WhenInventoryClosed(StopEditing);

            Cleanup(_slotEditor.Destroy);
            Cleanup(_editButton.Destroy);
        }

        private void ToggleSlotSelection()
        {
            if (!Inventory.IsOpen) return;

            _selectingSlot = !_selectingSlot;
            _slotEditor.Close();
            UpdateButton();
        }

        private void EditSelectedSlot(object sender, InventorySlotClickEventArgs click)
        {
            if (!_selectingSlot || click.Slot == null) return;

            click.Handled = true;
            _slotEditor.Open(click.Slot);
        }

        private void StopEditing()
        {
            _selectingSlot = false;
            _slotEditor.Close();
            UpdateButton();
        }

        private void UpdateButton()
        {
            _editButton
                .SetText(_selectingSlot ? "SELECT A SLOT  [F8]" : "EDIT SLOT  [F8]")
                .SetBackground(_selectingSlot ? Ui.Theme.Accent : Ui.Theme.Control);
        }
    }
}
