using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoRetainer.Modules.Undercut;

/// <summary>
/// Works out which item every row of the retainer's sell list holds without opening the rows. The addon keeps one
/// string per row in its AtkValues but the layout isn't documented, so rather than hardcoding offsets every string that
/// resolves to one of the retainer's listed items is collected in order; the caller only uses the result once it lines
/// up with the market container.
/// </summary>
public static unsafe class UndercutSellList
{
    private const string HqGlyph = "\ue03c";
    private const string CollectableGlyph = "\ue03d";

    /// <summary>
    /// Resolves an item name as the UI shows it to one of the retainer's listed items, or null when it isn't one.
    /// </summary>
    internal static (uint ItemId, bool Hq)? Resolve(string rawName)
    {
        if(rawName.IsNullOrEmpty()) return null;
        var clean = rawName.Replace(HqGlyph, "").Replace(CollectableGlyph, "").Trim().ToLowerInvariant();
        if(TaskUndercutItems.NameToItemId.TryGetValue(clean, out var itemId))
        {
            return (itemId, rawName.Contains(HqGlyph));
        }
        return null;
    }

    /// <summary>
    /// The item of each sell list row in row order, or an empty list when the rows can't be read.
    /// </summary>
    internal static List<(uint ItemId, bool Hq)> ReadRows()
    {
        var rows = new List<(uint ItemId, bool Hq)>();
        if(!TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addon) || !IsAddonReady(addon) || addon->AtkValues == null) return rows;
        for(var i = 0; i < addon->AtkValuesCount; i++)
        {
            var value = addon->AtkValues[i];
            if(!value.Type.EqualsAny(AtkValueType.String, AtkValueType.String8, AtkValueType.WideString, AtkValueType.ManagedString)) continue;
            if(value.String.Value == null) continue;
            //the HQ marker survives as a literal glyph or as an icon payload depending on how the row was built, so the
            //caller falls back to the container when the flag is lost here
            var resolved = Resolve(MemoryHelper.ReadSeStringNullTerminated((nint)value.String.Value).TextValue);
            if(resolved != null) rows.Add(resolved.Value);
        }
        return rows;
    }
}
