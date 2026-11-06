using ECommons.EzIpcManager;

#pragma warning disable CS0649

namespace AutoRetainer.Modules.Voyage.RepairCrafting;

// No initializers on the [EzIPC] fields: static initializers run in source order and would
// overwrite the delegates EzIPC.Init has just assigned.
internal static class ArtisanCraftingIPC
{
    internal const string InternalName = "Artisan";

    private static readonly EzIPCDisposalToken[] DisposalTokens = EzIPC.Init(typeof(ArtisanCraftingIPC), InternalName, SafeWrapper.IPCException);

    [EzIPC] internal static readonly Func<bool> IsListRunning;
    [EzIPC] internal static readonly Action<int> StartListById;
    [EzIPC] internal static readonly Func<bool> GetEnduranceStatus;
    [EzIPC] internal static readonly Action<bool> SetEnduranceStatus;
    [EzIPC] internal static readonly Action<bool> SetStopRequest;

    internal static bool IsLoaded => Svc.PluginInterface.InstalledPlugins.Any(x => x.InternalName == InternalName && x.IsLoaded);
}
