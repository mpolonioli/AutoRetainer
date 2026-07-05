using Dalamud.Utility;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoRetainer.Modules.Undercut;

public static unsafe class UndercutHandlers
{
    internal static bool? SelectSellItems()
    {
        //2380	Sell items in my possession.
        var text = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRow(2380).Text.ToDalamudString().GetText(true);
        return Utils.TrySelectSpecificEntry(text);
    }

    internal static bool? WaitForSellListReady()
    {
        if(TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addon) && IsAddonReady(addon))
        {
            return true;
        }
        return false;
    }

    internal static bool? OpenListingContextMenu(int index)
    {
        if(TryGetAddonMaster<AddonMaster.ContextMenu>(out var m) && m.IsAddonReady && m.Base->IsVisible)
        {
            return true;
        }
        if(TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addon) && IsAddonReady(addon))
        {
            if(Utils.GenericThrottle)
            {
                Callback.Fire(addon, true, 0, index, 1);
            }
        }
        else
        {
            Utils.RethrottleGeneric();
        }
        return false;
    }

    internal static bool? SelectAdjustPrice()
    {
        if(TryGetAddonMaster<AddonMaster.ContextMenu>(out var m) && m.IsAddonReady && m.Base->IsVisible)
        {
            foreach(var entry in m.Entries)
            {
                //adjust price is the first entry of a market listing's context menu
                if(entry.Enabled && Utils.GenericThrottle)
                {
                    PluginLog.Debug($"Undercut: selecting context menu entry \"{entry.Text}\"");
                    entry.Select();
                    return true;
                }
                break;
            }
        }
        else
        {
            Utils.RethrottleGeneric();
        }
        return false;
    }

    internal static bool? WaitForRetainerSellReady()
    {
        if(TryGetAddonMaster<AddonMaster.RetainerSell>(out var m) && m.IsAddonReady && m.Base->IsVisible)
        {
            return true;
        }
        return false;
    }

    internal static bool? ClickComparePrices()
    {
        //the ItemSearchResult addon stays allocated in a hidden state after being closed, existence alone doesn't mean the price list is open
        if(TryGetAddonByName<AtkUnitBase>("ItemSearchResult", out var results) && results->IsVisible)
        {
            return true;
        }
        if(TryGetAddonMaster<AddonMaster.RetainerSell>(out var m) && m.IsAddonReady && m.Base->IsVisible)
        {
            //the game ignores market data requests that are sent too quickly one after another
            if(Environment.TickCount64 - S.UndercutMarketListener.CompareRequestedAt < C.UndercutDelayMs)
            {
                return false;
            }
            if(Utils.GenericThrottle)
            {
                //only market data received after this point may be consumed by the wait step
                S.UndercutMarketListener.CompareRequestedAt = Environment.TickCount64;
                m.ComparePrices();
            }
        }
        else
        {
            Utils.RethrottleGeneric();
        }
        return false;
    }

    internal enum PriceListMessage { None, NoListings, Retry }

    private static HashSet<string> retrySearchMessages;
    //the search-context "Please wait and try your search again." variants; the same English text also exists for the
    //history tab under different rows, but only the search rows carry the wording the price list actually shows
    private static HashSet<string> RetrySearchMessages
    {
        get
        {
            if(retrySearchMessages == null)
            {
                retrySearchMessages = [];
                var sheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>();
                foreach(var id in new uint[] { 1997, 1998 })
                {
                    var s = sheet.GetRow(id).Text.GetText()?.Trim();
                    if(!string.IsNullOrEmpty(s)) retrySearchMessages.Add(s);
                }
            }
            return retrySearchMessages;
        }
    }

    /// <summary>
    /// Classifies the message the price list window is currently showing: no listings ("No items found."), a transient
    /// "please wait and try again" the server sends under load, or none (still loading or listings present).
    /// </summary>
    internal static PriceListMessage GetPriceListMessage()
    {
        if(TryGetAddonByName<AtkUnitBase>("ItemSearchResult", out var addon) && IsAddonReady(addon) && addon->IsVisible)
        {
            var msg = ((AddonItemSearchResult*)addon)->ErrorMessage;
            if(msg != null && ((AtkResNode*)msg)->IsVisible())
            {
                var text = msg->NodeText.GetText();
                if(!string.IsNullOrEmpty(text))
                {
                    return RetrySearchMessages.Contains(text.Trim()) ? PriceListMessage.Retry : PriceListMessage.NoListings;
                }
            }
        }
        return PriceListMessage.None;
    }

    internal static bool? CloseItemSearchResult()
    {
        if(TryGetAddonByName<AtkUnitBase>("ItemSearchResult", out var addon) && IsAddonReady(addon) && addon->IsVisible)
        {
            if(Utils.GenericThrottle)
            {
                addon->Close(true);
            }
            return false;
        }
        return true;
    }

    /// <summary>
    /// Clicks the RetainerSell confirm button with a valid zeroed AtkEventData. The confirm handler dereferences the
    /// event data; standard ECommons clicks pass null there, which crashes the game.
    /// </summary>
    internal static void ClickRetainerSellConfirm(FFXIVClientStructs.FFXIV.Client.UI.AddonRetainerSell* addon)
    {
        var evt = new AtkEvent()
        {
            Target = (AtkEventTarget*)addon->Confirm->AtkComponentBase.OwnerNode,
            Listener = (AtkEventListener*)addon,
        };
        var data = new AtkEventData();
        ((AtkUnitBase*)addon)->ReceiveEvent((AtkEventType)25, 21, &evt, &data);
    }

    internal static bool? CancelRetainerSell()
    {
        if(TryGetAddonMaster<AddonMaster.RetainerSell>(out var m) && m.IsAddonReady && m.Base->IsVisible)
        {
            if(Utils.GenericThrottle)
            {
                m.Cancel();
            }
            return false;
        }
        return true;
    }

    internal static bool? CloseSellList()
    {
        if(TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addon) && IsAddonReady(addon) && addon->IsVisible)
        {
            if(Utils.GenericThrottle)
            {
                addon->Close(true);
            }
            return false;
        }
        return true;
    }
}
