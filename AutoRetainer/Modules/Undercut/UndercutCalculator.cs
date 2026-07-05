namespace AutoRetainer.Modules.Undercut;

public sealed record UndercutListing(long UnitPrice, bool Hq, bool IsOwn, string World = null);

public static class UndercutCalculator
{
    /// <summary>
    /// Computes the new price for a listed item or null if the price should be left unchanged.
    /// </summary>
    public static int? ComputeNewPrice(int currentPrice, bool itemIsHq, IEnumerable<UndercutListing> listings, out string skipReason)
    {
        skipReason = null;
        var relevant = listings;
        if(itemIsHq && C.UndercutCompareHQOnly)
        {
            relevant = relevant.Where(x => x.Hq);
        }
        //on a tie prefer the listing that isn't ours, so a competitor sitting at the same price still gets undercut
        var lowest = relevant.OrderBy(x => x.UnitPrice).ThenBy(x => x.IsOwn).FirstOrDefault();
        if(lowest == null)
        {
            skipReason = "no competing listings found";
            return null;
        }
        if(C.UndercutMaxDropPercent > 0 && currentPrice > 0 && lowest.UnitPrice < (long)currentPrice * (100 - C.UndercutMaxDropPercent) / 100)
        {
            skipReason = $"lowest price {lowest.UnitPrice} is more than {C.UndercutMaxDropPercent}% below current price {currentPrice}";
            return null;
        }
        //when the cheapest listing is one of our own retainers there is nothing to undercut, but our other listings
        //still come down to match it rather than being left above the price we already settled on
        var newPrice = lowest.IsOwn ? lowest.UnitPrice : Math.Max(1, lowest.UnitPrice - C.UndercutBy);
        if(newPrice >= currentPrice)
        {
            skipReason = lowest.IsOwn ? "already at or below your own lowest listing" : "current price is already the lowest";
            return null;
        }
        return (int)newPrice;
    }
}
