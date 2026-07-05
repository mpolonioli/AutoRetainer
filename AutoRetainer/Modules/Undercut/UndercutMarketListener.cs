using Dalamud.Game.Network.Structures;

namespace AutoRetainer.Modules.Undercut;

/// <summary>
/// Records the latest market board offerings received from the server so that the undercut task can consume them
/// without reading game memory directly.
/// </summary>
public sealed class UndercutMarketListener : IDisposable
{
    internal uint LastItemId = 0;
    internal long ReceivedAt = 0;
    internal long CompareRequestedAt = 0;
    internal readonly List<UndercutListing> LastListings = [];

    private UndercutMarketListener()
    {
        Svc.MarketBoard.OfferingsReceived += OnOfferingsReceived;
    }

    public void Dispose()
    {
        Svc.MarketBoard.OfferingsReceived -= OnOfferingsReceived;
    }

    private void OnOfferingsReceived(IMarketBoardCurrentOfferings offerings)
    {
        try
        {
            if(offerings.ItemListings.Count == 0) return;
            var itemId = offerings.ItemListings[0].ItemId;
            var now = Environment.TickCount64;
            //listings arrive in pages of up to 10; append follow-up pages of the same request instead of discarding the cheapest ones
            if(itemId != LastItemId || now - ReceivedAt > 2000)
            {
                LastListings.Clear();
            }
            foreach(var l in offerings.ItemListings)
            {
                if(l.PricePerUnit == 0) continue;
                var isOwn = TaskUndercutItems.OwnRetainerIds.Contains(l.RetainerId) || TaskUndercutItems.OwnRetainerNames.Contains(l.RetainerName);
                LastListings.Add(new(l.PricePerUnit, l.IsHq, isOwn));
            }
            LastItemId = itemId;
            ReceivedAt = now;
            PluginLog.Debug($"Undercut: received {offerings.ItemListings.Count} market listings for item {itemId}");
        }
        catch(Exception e)
        {
            e.Log();
        }
    }
}
