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
        internal bool Skipped;
        internal long DelayUntil;
    }

    internal class MarketListing
    {
        internal uint ItemId;
        internal bool Hq;
        internal int Price;
        internal bool Processed;
    }

    internal static readonly List<ListedItem> Items = [];
    //snapshot of what the retainer actually has listed, used to tell when the rows left to process cannot need a change
    internal static readonly List<MarketListing> MarketListings = [];
    internal static bool ListingsTrusted;
    //set once the sell list rows have been matched to the container, which is what allows single copies to be skipped
    internal static bool RowsResolved;
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
        MarketListings.Clear();
        ListingsTrusted = true;
        var inv = InventoryManager.Instance()->GetInventoryContainer(InventoryType.RetainerMarket);
        var prices = InventoryManager.Instance()->RetainerMarketPrices;
        if(inv != null)
        {
            for(var i = 0; i < inv->Size; i++)
            {
                var slot = inv->GetInventorySlot(i);
                if(slot != null && slot->ItemId != 0)
                {
                    var hq = slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
                    Items.Add(new()
                    {
                        Index = Items.Count,
                        ItemId = slot->ItemId,
                        Hq = hq,
                    });
                    MarketListings.Add(new()
                    {
                        ItemId = slot->ItemId,
                        Hq = hq,
                        Price = i < prices.Length ? (int)prices[i] : 0,
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
        ResolveRows();
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
                P.TaskManager.Enqueue(() => EvaluateSkip(x), $"Undercut.EvaluateSkip({x.Index})");
                P.TaskManager.Enqueue(() => x.Skipped ? true : UndercutHandlers.OpenListingContextMenu(x.Index), $"Undercut.OpenListingContextMenu({x.Index})");
                P.TaskManager.Enqueue(() => x.Skipped ? true : UndercutHandlers.SelectAdjustPrice(), "Undercut.SelectAdjustPrice");
                P.TaskManager.Enqueue(() => x.Skipped ? true : UndercutHandlers.WaitForRetainerSellReady(), "Undercut.WaitForRetainerSellReady");
                P.TaskManager.Enqueue(() => x.Skipped ? true : ReadCurrentPrice(x), "Undercut.ReadCurrentPrice");
                if(useInGame)
                {
                    //rows whose price was already decided for an earlier copy of the same item skip the market data request
                    P.TaskManager.Enqueue(() => x.Skipped || x.CachedPrice != null ? true : UndercutHandlers.ClickComparePrices(), "Undercut.ClickComparePrices");
                    P.TaskManager.Enqueue(() => x.Skipped ? true : WaitAndComputeFromGameData(x), $"Undercut.WaitAndComputeFromGameData({x.ItemId})", new(timeLimitMS: 60000));
                    P.TaskManager.Enqueue(() => x.Skipped ? true : UndercutHandlers.CloseItemSearchResult(), "Undercut.CloseItemSearchResult");
                }
                else
                {
                    P.TaskManager.Enqueue(() => x.Skipped ? true : ComputeFromUniversalis(x), "Undercut.ComputeFromUniversalis");
                }
                P.TaskManager.Enqueue(() => x.Skipped ? true : ApplyPrice(x), "Undercut.ApplyPrice");
                P.TaskManager.Enqueue(() => x.Skipped ? true : Delay(x, 250), "Undercut.Delay");
            }
            P.TaskManager.Enqueue(UndercutHandlers.CloseSellList, "Undercut.CloseSellList");
        }
        catch(Exception e)
        {
            e.Log();
        }
        P.TaskManager.InsertStack();
    }

    /// <summary>
    /// Tries to label every row with the item it holds by reading the sell list, which lets a row be left unopened on
    /// its own merits instead of only once nothing undecided is left. The reading is only accepted when it holds the
    /// same items as the market container, so a layout that no longer parses simply turns the labelling off.
    /// </summary>
    internal static void ResolveRows()
    {
        RowsResolved = false;
        var rows = UndercutSellList.ReadRows();
        if(rows.Count != Items.Count || Items.Count == 0) return;
        var snapshot = MarketListings.Select(l => (l.ItemId, l.Hq)).ToList();
        if(!SameContents(rows, snapshot))
        {
            //the HQ flag doesn't always survive the row text; ignoring it is still safe as long as the container gives
            //an unambiguous quality for every item it lists
            if(!SameContents(rows.Select(r => r.ItemId), snapshot.Select(r => r.ItemId))
                || MarketListings.GroupBy(l => l.ItemId).Any(g => g.Select(l => l.Hq).Distinct().Count() > 1))
            {
                PluginLog.Debug("Undercut: sell list rows don't match the market container, opening every listing");
                return;
            }
            var quality = MarketListings.GroupBy(l => l.ItemId).ToDictionary(g => g.Key, g => g.First().Hq);
            rows = rows.Select(r => (r.ItemId, Hq: quality[r.ItemId])).ToList();
        }
        for(var i = 0; i < Items.Count; i++)
        {
            Items[i].ItemId = rows[i].ItemId;
            Items[i].Hq = rows[i].Hq;
        }
        RowsResolved = true;
    }

    private static bool SameContents<T>(IEnumerable<T> a, IEnumerable<T> b)
    {
        var counts = new Dictionary<T, int>();
        foreach(var x in a) counts[x] = counts.GetValueOrDefault(x) + 1;
        foreach(var x in b)
        {
            if(!counts.TryGetValue(x, out var n) || n == 0) return false;
            counts[x] = n - 1;
        }
        return counts.Values.All(n => n == 0);
    }

    /// <summary>
    /// Decides whether this row can be left unopened, which is the case when opening it could only end in a cancel.
    /// With the rows labelled that means every copy of this row's item still to come is priced at or below the price
    /// already decided for it; without labels the row's item is unknown, so it takes every listing left to be in that
    /// position before anything can be skipped.
    /// </summary>
    internal static bool? EvaluateSkip(ListedItem x)
    {
        var pending = MarketListings.Where(l => !l.Processed).ToList();
        if(!ListingsTrusted || pending.Count == 0) return true;
        if(RowsResolved)
        {
            //every copy of this row's item still to come is priced at or below what was already decided for it
            var copies = pending.Where(l => l.ItemId == x.ItemId && l.Hq == x.Hq).ToList();
            x.Skipped = copies.Count > 0 && DecidedPrices.TryGetValue((x.ItemId, x.Hq), out var decided) && copies.All(l => l.Price <= decided);
            if(x.Skipped)
            {
                copies[0].Processed = true;
                PluginLog.Debug($"Undercut: leaving listing {x.Index} unopened, every remaining {ExcelItemHelper.GetName(x.ItemId)} is already at or below its decided price");
            }
        }
        else
        {
            x.Skipped = pending.All(l => DecidedPrices.TryGetValue((l.ItemId, l.Hq), out var decided) && l.Price <= decided);
            if(x.Skipped)
            {
                PluginLog.Debug($"Undercut: leaving listing {x.Index} unopened, every remaining listing is already at or below a decided price");
            }
        }
        return true;
    }

    /// <summary>
    /// Marks the listing this row turned out to be as processed. A row matching nothing means the container snapshot
    /// doesn't line up with the sell list, in which case every remaining row is opened as before.
    /// </summary>
    internal static void ConsumeListing(ListedItem x)
    {
        if(!ListingsTrusted) return;
        var match = MarketListings.FirstOrDefault(l => !l.Processed && l.ItemId == x.ItemId && l.Hq == x.Hq && l.Price == x.CurrentPrice);
        if(match == null)
        {
            ListingsTrusted = false;
            PluginLog.Warning($"Undercut: {ExcelItemHelper.GetName(x.ItemId)} at {x.CurrentPrice} is not in the retainer market snapshot, opening every remaining listing");
            return;
        }
        match.Processed = true;
    }

    internal static bool? Delay(ListedItem x, int ms)
    {
        if(x.DelayUntil == 0) x.DelayUntil = Environment.TickCount64 + ms;
        return Environment.TickCount64 >= x.DelayUntil;
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
                if(RowsResolved && (x.ItemId != resolvedId || x.Hq != isHq))
                {
                    RowsResolved = false;
                    PluginLog.Warning($"Undercut: listing {x.Index} holds {ExcelItemHelper.GetName(resolvedId)}, not the expected {ExcelItemHelper.GetName(x.ItemId)}, opening every remaining listing");
                }
                x.ItemId = resolvedId;
                x.Hq = isHq;
            }
            else
            {
                PluginLog.Warning($"Undercut: could not resolve listed item \"{rawName}\", assuming {ExcelItemHelper.GetName(x.ItemId)}");
            }
            ConsumeListing(x);
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
