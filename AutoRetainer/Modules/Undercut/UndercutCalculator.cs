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
        var lowest = relevant.OrderBy(x => x.UnitPrice).FirstOrDefault();
        if(lowest == null)
        {
            skipReason = "no competing listings found";
            return null;
        }
        if(lowest.IsOwn)
        {
            skipReason = "lowest listing is your own";
            return null;
        }
        if(C.UndercutMaxDropPercent > 0 && currentPrice > 0 && lowest.UnitPrice < (long)currentPrice * (100 - C.UndercutMaxDropPercent) / 100)
        {
            skipReason = $"lowest price {lowest.UnitPrice} is more than {C.UndercutMaxDropPercent}% below current price {currentPrice}";
            return null;
        }
        var newPrice = Math.Max(1, lowest.UnitPrice - C.UndercutBy);
        if(newPrice >= currentPrice)
        {
            skipReason = "current price is already the lowest";
            return null;
        }
        return (int)newPrice;
    }
}
