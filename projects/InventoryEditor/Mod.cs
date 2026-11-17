using ISOCORE.ModSdk;

namespace ISOCORE.SimpleInventoryEditor
{
    public sealed class Mod : IMod
    {
        public void Load(ModContext mod)
        {
            mod.Start<InventoryEditor>(objectName: "Simple Inventory Editor");
        }
    }
}
