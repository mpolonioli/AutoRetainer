using AutoRetainer.Modules.Undercut;
using System.Collections.Frozen;

namespace AutoRetainer.UI.NeoUI;
public class UndercutTab : NeoUIEntry
{
    private static readonly FrozenDictionary<UndercutPriceSource, string> PriceSourceNames = new Dictionary<UndercutPriceSource, string>()
    {
        [UndercutPriceSource.In_Game] = "In-game price list (current world)",
        [UndercutPriceSource.Universalis] = "Universalis",
        [UndercutPriceSource.In_Game_And_Universalis] = "In-game + Universalis",
    }.ToFrozenDictionary();

    private static readonly FrozenDictionary<UndercutUniversalisScope, string> ScopeNames = new Dictionary<UndercutUniversalisScope, string>()
    {
        [UndercutUniversalisScope.Server] = "Server (current world)",
        [UndercutUniversalisScope.Data_Center] = "Data Center",
        [UndercutUniversalisScope.Region] = "Region",
    }.ToFrozenDictionary();

    private static bool UsesUniversalis => C.UndercutPriceSource != UndercutPriceSource.In_Game;

    public override string Path => "Undercutting";

    public override NuiBuilder Builder { get; init; } = new NuiBuilder()
        .Section("Market Board Undercutting")
        .Checkbox("Enable Undercutting", () => ref C.EnableUndercut, "While processing a retainer, adjust the prices of items it has listed on the market board. Individual retainers can be excluded in their configuration.")
        .EnumComboFullWidth(null, "Price Source", () => ref C.UndercutPriceSource, null, PriceSourceNames, "In-game price list is the most up to date source but requires opening the price list for every item, which is slow, and only covers your current world. Universalis is faster and can look across a whole data center or region, but its data may be outdated. The combined option uses the in-game price list for your current world and Universalis for the other worlds.")
        .If(() => UsesUniversalis)
            .EnumComboFullWidth(null, "Universalis Scope", () => ref C.UndercutUniversalisScope, null, ScopeNames, "How wide a market Universalis compares against: only your current world, your whole data center, or your entire region (e.g. all of Europe). A wider scope finds more competing listings but the cheapest may be on a world you would have to travel to.")
        .EndIf()
        .DragInt(120f, "Undercut By, gil", () => ref C.UndercutBy.ValidateRange(0, 999999), 0.2f, 0, 999999)
        .TextWrapped("Set \"Undercut By\" to 0 to match the lowest price instead of going below it.")
        .Checkbox("Compare HQ Items Only Against HQ Listings", () => ref C.UndercutCompareHQOnly, "When enabled, HQ items will only undercut other HQ listings. NQ items always compare against all listings.")
        .SliderInt(120f, "Undercut Protection, %", () => ref C.UndercutMaxDropPercent.ValidateRange(0, 99), 0, 99, "Skip undercutting an item if the lowest market price is more than this percent below your current price. Protects against following a crashed market down. 0 = disabled.")
        .DragInt(120f, "In-Game Price List Delay, ms", () => ref C.UndercutDelayMs.ValidateRange(1500, 10000), 5f, 1500, 10000, "Delay between opening item price lists when the in-game price source is used. The game ignores market data requests that are sent too quickly; values below 2000ms may result in items being skipped.");
}
