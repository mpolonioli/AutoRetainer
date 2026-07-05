using ECommons.ExcelServices;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoRetainer.Modules.Undercut;

public static unsafe class TaskUndercutItems
{
    internal class ListedItem
    {
        internal int Index;
        internal uint ItemId;
        internal bool Hq;
        internal int CurrentPrice;
        internal int? NewPrice;
        internal long SearchDeadline;
        internal bool Confirmed;
        internal int? CachedPrice;
        internal bool RetriedSearch;
        internal bool RetryIssuing;
        internal long RetryAt;
    }

    internal static readonly List<ListedItem> Items = [];
    internal static readonly Dictionary<string, uint> NameToItemId = [];
    internal static readonly Dictionary<(uint ItemId, bool Hq), int> DecidedPrices = [];
    internal static HashSet<ulong> OwnRetainerIds = [];
    internal static HashSet<string> OwnRetainerNames = [];
    internal static string CurrentWorldName = "";

    public static void Enqueue()
    {
        P.TaskManager.Enqueue(UndercutHandlers.SelectSellItems, "Undercut.SelectSellItems");
        P.TaskManager.Enqueue(UndercutHandlers.WaitForSellListReady, "Undercut.WaitForSellListReady");
        P.TaskManager.EnqueueDelay(500);
        P.TaskManager.Enqueue(EnqueueProcessing, "Undercut.EnqueueProcessing");
    }

    internal static void EnqueueProcessing()
    {
        Items.Clear();
        DecidedPrices.Clear();
        OwnRetainerIds = C.OfflineData.SelectMany(x => x.RetainerData.Select(r => r.RetainerID)).Where(x => x != 0).ToHashSet();
        OwnRetainerNames = C.OfflineData.SelectMany(x => x.RetainerData.Select(r => r.Name)).Where(x => !x.IsNullOrEmpty()).ToHashSet();
        CurrentWorldName = Player.CurrentWorld ?? "";
        var inv = InventoryManager.Instance()->GetInventoryContainer(InventoryType.RetainerMarket);
        if(inv != null)
        {
            for(var i = 0; i < inv->Size; i++)
            {
                var slot = inv->GetInventorySlot(i);
                if(slot != null && slot->ItemId != 0)
                {
                    Items.Add(new()
                    {
                        Index = Items.Count,
                        ItemId = slot->ItemId,
                        Hq = slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality),
                    });
                }
            }
        }
        NameToItemId.Clear();
        foreach(var item in Items)
        {
            var name = ExcelItemHelper.GetName(item.ItemId);
            if(!name.IsNullOrEmpty())
            {
                NameToItemId[name.ToLowerInvariant()] = item.ItemId;
            }
        }
        PluginLog.Debug($"Undercut: {Items.Count} listed items found");
        var useUniversalis = C.UndercutPriceSource != UndercutPriceSource.In_Game;
        var useInGame = C.UndercutPriceSource is UndercutPriceSource.In_Game or UndercutPriceSource.In_Game_And_Universalis;
        P.TaskManager.BeginStack();
        try
        {
            if(Items.Count > 0 && useUniversalis)
            {
                var scope = UniversalisClient.GetQueryScope();
                P.TaskManager.Enqueue(() => UniversalisClient.BeginFetch(Items.Select(x => x.ItemId), scope), "Undercut.BeginFetch");
                //the fetch retries stalled requests per scope, so allow well beyond the 20s default before giving up
                P.TaskManager.Enqueue(() => !UniversalisClient.IsBusy, "Undercut.WaitUniversalis", new(timeLimitMS: 60000));
            }
            foreach(var item in Items)
            {
                var x = item;
                P.TaskManager.Enqueue(() => UndercutHandlers.OpenListingContextMenu(x.Index), $"Undercut.OpenListingContextMenu({x.Index})");
                P.TaskManager.Enqueue(UndercutHandlers.SelectAdjustPrice, "Undercut.SelectAdjustPrice");
                P.TaskManager.Enqueue(UndercutHandlers.WaitForRetainerSellReady, "Undercut.WaitForRetainerSellReady");
                P.TaskManager.Enqueue(() => ReadCurrentPrice(x), "Undercut.ReadCurrentPrice");
                if(useInGame)
                {
                    //rows whose price was already decided for an earlier copy of the same item skip the market data request
                    P.TaskManager.Enqueue(() => x.CachedPrice != null ? true : UndercutHandlers.ClickComparePrices(), "Undercut.ClickComparePrices");
                    P.TaskManager.Enqueue(() => WaitAndComputeFromGameData(x), $"Undercut.WaitAndComputeFromGameData({x.ItemId})", new(timeLimitMS: 60000));
                    P.TaskManager.Enqueue(UndercutHandlers.CloseItemSearchResult, "Undercut.CloseItemSearchResult");
                }
                else
                {
                    P.TaskManager.Enqueue(() => ComputeFromUniversalis(x), "Undercut.ComputeFromUniversalis");
                }
                P.TaskManager.Enqueue(() => ApplyPrice(x), "Undercut.ApplyPrice");
                P.TaskManager.EnqueueDelay(250);
            }
            P.TaskManager.Enqueue(UndercutHandlers.CloseSellList, "Undercut.CloseSellList");
        }
        catch(Exception e)
        {
            e.Log();
        }
        P.TaskManager.InsertStack();
    }

    internal static bool? ReadCurrentPrice(ListedItem x)
    {
        if(TryGetAddonMaster<AddonMaster.RetainerSell>(out var m) && m.IsAddonReady)
        {
            x.CurrentPrice = m.AskingPrice;
            x.NewPrice = null;
            x.SearchDeadline = 0;
            x.Confirmed = false;
            x.RetriedSearch = false;
            x.RetryIssuing = false;
            x.RetryAt = 0;
            //when the same item is listed multiple times the sell list groups rows differently than the RetainerMarket
            //container, so resolve the actual item from the opened window instead of trusting the container order
            var rawName = m.ItemName ?? "";
            var isHq = rawName.Contains('');
            var cleanName = rawName.Replace("", "").Replace("", "").Trim().ToLowerInvariant();
            if(NameToItemId.TryGetValue(cleanName, out var resolvedId))
            {
                x.ItemId = resolvedId;
                x.Hq = isHq;
            }
            else
            {
                PluginLog.Warning($"Undercut: could not resolve listed item \"{rawName}\", assuming {ExcelItemHelper.GetName(x.ItemId)}");
            }
            x.CachedPrice = DecidedPrices.TryGetValue((x.ItemId, x.Hq), out var decided) ? decided : null;
            PluginLog.Debug($"Undercut: {ExcelItemHelper.GetName(x.ItemId)}{(x.Hq ? " (HQ)" : "")} current price {x.CurrentPrice}");
            return true;
        }
        return false;
    }

    internal static bool? WaitAndComputeFromGameData(ListedItem x)
    {
        if(x.CachedPrice != null)
        {
            x.NewPrice = x.CachedPrice.Value < x.CurrentPrice ? x.CachedPrice : null;
            PluginLog.Debug($"Undercut: {ExcelItemHelper.GetName(x.ItemId)}: reusing price {x.CachedPrice} decided for an earlier copy of this item{(x.NewPrice == null ? ", price unchanged" : "")}");
            return true;
        }
        if(x.SearchDeadline == 0)
        {
            x.SearchDeadline = Environment.TickCount64 + 15000;
        }
        var combined = C.UndercutPriceSource == UndercutPriceSource.In_Game_And_Universalis;
        var listener = S.UndercutMarketListener;
        var resultsVisible = TryGetAddonByName<AtkUnitBase>("ItemSearchResult", out var addon) && IsAddonReady(addon) && addon->IsVisible;
        if(resultsVisible && listener.LastItemId == x.ItemId && listener.ReceivedAt >= listener.CompareRequestedAt)
        {
            var listings = new List<UndercutListing>(listener.LastListings);
            if(combined) listings.AddRange(GetCombinedUniversalisListings(x.ItemId));
            ComputePrice(x, listings);
            return true;
        }
        //once a retry is committed, drive the close+re-fire to completion regardless of what the window currently shows;
        //closing it makes the message read below unavailable, so this can't be gated on it
        if(x.RetryIssuing)
        {
            if(UndercutHandlers.CloseItemSearchResult() != true) return false;
            var before = listener.CompareRequestedAt;
            UndercutHandlers.ClickComparePrices();
            if(listener.CompareRequestedAt != before)
            {
                x.RetryIssuing = false;
                x.RetriedSearch = true;
                x.SearchDeadline = Environment.TickCount64 + 15000;
            }
            return false;
        }
        //the 750ms settle avoids reading a message left over from the previous item while the window is being reused
        var message = resultsVisible && Environment.TickCount64 - listener.CompareRequestedAt > 750
            ? UndercutHandlers.GetPriceListMessage()
            : UndercutHandlers.PriceListMessage.None;
        //the server sometimes refuses the search under load with "Please wait and try your search again." - wait 5s and re-issue it once before giving up
        if(message == UndercutHandlers.PriceListMessage.Retry && !x.RetriedSearch)
        {
            if(x.RetryAt == 0)
            {
                x.RetryAt = Environment.TickCount64 + 5000;
                PluginLog.Debug($"Undercut: market search throttled for {ExcelItemHelper.GetName(x.ItemId)}, retrying once in 5s");
            }
            if(Environment.TickCount64 < x.RetryAt) return false;
            x.RetryIssuing = true;
            return false;
        }
        //the server can respond with no listings at all, showing "No items found." - stop waiting as soon as that message appears instead of sitting until the fallback below
        if(message != UndercutHandlers.PriceListMessage.None)
        {
            PluginLog.Debug($"Undercut: no market listings for {ExcelItemHelper.GetName(x.ItemId)}");
            ComputePrice(x, combined ? GetCombinedUniversalisListings(x.ItemId) : []);
            return true;
        }
        //fallback: the price list opened but neither listings nor a message ever arrived
        if(resultsVisible && Environment.TickCount64 > x.SearchDeadline - 10000)
        {
            ComputePrice(x, combined ? GetCombinedUniversalisListings(x.ItemId) : []);
            return true;
        }
        if(Environment.TickCount64 > x.SearchDeadline)
        {
            //the in-game lookup failed; fall back to whatever Universalis returned rather than skipping entirely
            if(combined && UniversalisClient.TryGetListings(x.ItemId, out var uni))
            {
                ComputePrice(x, uni);
                return true;
            }
            x.NewPrice = null;
            PluginLog.Warning($"Undercut: no market data received for {ExcelItemHelper.GetName(x.ItemId)}, leaving price unchanged");
            return true;
        }
        return false;
    }

    /// <summary>
    /// Universalis listings for the item with the current world's entries removed, since the in-game price list already provides fresher data for it.
    /// </summary>
    internal static List<UndercutListing> GetCombinedUniversalisListings(uint itemId)
    {
        if(UniversalisClient.TryGetListings(itemId, out var listings))
        {
            return listings.Where(l => l.World != CurrentWorldName).ToList();
        }
        return [];
    }

    internal static bool? ComputeFromUniversalis(ListedItem x)
    {
        if(x.CachedPrice != null)
        {
            x.NewPrice = x.CachedPrice.Value < x.CurrentPrice ? x.CachedPrice : null;
            PluginLog.Debug($"Undercut: {ExcelItemHelper.GetName(x.ItemId)}: reusing price {x.CachedPrice} decided for an earlier copy of this item{(x.NewPrice == null ? ", price unchanged" : "")}");
            return true;
        }
        if(UniversalisClient.TryGetListings(x.ItemId, out var listings))
        {
            ComputePrice(x, listings);
        }
        else
        {
            x.NewPrice = null;
            PluginLog.Warning($"Undercut: no Universalis data for {ExcelItemHelper.GetName(x.ItemId)}, leaving price unchanged");
        }
        return true;
    }

    internal static void ComputePrice(ListedItem x, List<UndercutListing> listings)
    {
        x.NewPrice = UndercutCalculator.ComputeNewPrice(x.CurrentPrice, x.Hq, listings, out var skipReason);
        //subsequent copies of the same item reuse this decision instead of requesting market data again
        DecidedPrices[(x.ItemId, x.Hq)] = x.NewPrice ?? x.CurrentPrice;
        if(x.NewPrice != null)
        {
            PluginLog.Information($"Undercut: {ExcelItemHelper.GetName(x.ItemId)}{(x.Hq ? " (HQ)" : "")}: {x.CurrentPrice} -> {x.NewPrice}");
        }
        else
        {
            PluginLog.Debug($"Undercut: {ExcelItemHelper.GetName(x.ItemId)}{(x.Hq ? " (HQ)" : "")}: price unchanged, {skipReason}");
        }
    }

    internal static bool? ApplyPrice(ListedItem x)
    {
        if(x.NewPrice == null)
        {
            return UndercutHandlers.CancelRetainerSell();
        }
        if(TryGetAddonMaster<AddonMaster.RetainerSell>(out var m) && m.IsAddonReady && m.Base->IsVisible)
        {
            //once confirmed, don't touch the addon again: it stays allocated while closing and firing events into it crashes the game
            if(x.Confirmed)
            {
                return false;
            }
            //write the numeric input component directly: firing the price callback crashes AddonRetainerSell.ReceiveEvent on current game versions
            var addon = (AddonRetainerSell*)m.Base;
            var input = addon->AskingPrice;
            if(input == null)
            {
                return false;
            }
            if(input->Value != x.NewPrice.Value)
            {
                if(Utils.GenericThrottle)
                {
                    input->SetValue(x.NewPrice.Value);
                }
            }
            else if(m.ConfirmButton != null && m.ConfirmButton->IsEnabled)
            {
                if(Utils.GenericThrottle)
                {
                    UndercutHandlers.ClickRetainerSellConfirm(addon);
                    x.Confirmed = true;
                }
            }
            return false;
        }
        return true;
    }
}
