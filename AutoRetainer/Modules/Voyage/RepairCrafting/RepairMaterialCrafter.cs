using AutoRetainer.Internal;
using AutoRetainer.Modules.Voyage.Tasks;
using AutoRetainer.Modules.Voyage.VoyageCalculator;
using AutoRetainer.Scheduler.Tasks;
using Dalamud.Game.ClientState.Conditions;
using ECommons.Automation;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace AutoRetainer.Modules.Voyage.RepairCrafting;

// Crafts Magitek Repair Materials with Artisan when a submarine can't be repaired, then returns to the panel.
internal static unsafe class RepairMaterialCrafter
{
    internal const uint RepairMaterialsItemId = 10373;
    private const long RetryCooldownMS = 30 * 60 * 1000;

    private static readonly Dictionary<ulong, long> Attempts = [];
    private static bool Running = false;
    private static bool StartedByUs = false;
    private static bool StopRequested = false;
    private static int ListId = 0;

    internal static bool Active => Running && P.TaskManager.IsBusy;

    internal static bool TryHandleShortage(string name, VoyageType type, List<int> broken)
    {
        if(!C.CraftRepairMaterials || type != VoyageType.Submersible || !VoyageScheduler.Enabled) return false;
        try
        {
            var vesselIndex = VoyageUtils.GetVesselIndexByName(name, type);
            var cost = GetRepairCost(vesselIndex, broken);
            var owned = InventoryManager.Instance()->GetInventoryItemCount(RepairMaterialsItemId);
            if(cost == 0 || owned >= cost) return false;

            var cid = Svc.ClientState.LocalContentId;
            if(Attempts.TryGetValue(cid, out var last) && Environment.TickCount64 - last < RetryCooldownMS)
            {
                PluginLog.Information($"[RepairCraft] Already tried to craft repair materials recently, not retrying");
                return false;
            }
            if(!ArtisanCraftingIPC.IsLoaded)
            {
                S.AnomalyWindow.Add("Out of repair materials and Artisan is not loaded, could not craft them");
                return false;
            }
            if(!TryGetRecipe(out var recipe))
            {
                S.AnomalyWindow.Add("Out of repair materials and no crafter has the level to craft them");
                return false;
            }

            var needed = cost;
            for(var i = 0; i < 4; i++)
            {
                if(i != vesselIndex) needed += GetRepairCost(i, null);
            }
            var toMake = needed - (int)owned + C.CraftRepairMaterialsExtra;
            var yield = Math.Max(1, (int)recipe.AmountResult);
            var crafts = (toMake + yield - 1) / yield;

            Attempts[cid] = Environment.TickCount64;
            DuoLog.Information($"[Voyage] Out of repair materials ({owned}/{cost}), crafting {crafts * yield} with Artisan");
            Enqueue(recipe.RowId, crafts, cost);
            return true;
        }
        catch(Exception e)
        {
            e.Log();
            return false;
        }
    }

    private static int GetRepairCost(int vesselIndex, List<int> slots)
    {
        var sheet = Svc.Data.GetExcelSheet<SubmarinePart>();
        var cost = 0;
        for(var i = 0; i < 4; i++)
        {
            if(slots != null && !slots.Contains(i)) continue;
            var slot = VoyageUtils.GetVesselComponent(vesselIndex, VoyageType.Submersible, i);
            if(slot->ItemId == 0 || slot->Condition != 0) continue;
            var itemId = slot->ItemId;
            if(!ImportantItemsMethods.PartIdToItemId.TryGetFirst(x => x.Value == itemId, out var part)) continue;
            if(sheet.TryGetRow(part.Key, out var row)) cost += row.RepairMaterials;
        }
        return cost;
    }

    private static bool TryGetRecipe(out Recipe recipe)
    {
        recipe = default;
        if(!Svc.Data.GetExcelSheet<RecipeLookup>().TryGetRow(RepairMaterialsItemId, out var lookup)) return false;
        var candidates = new[] { lookup.CRP, lookup.BSM, lookup.ARM, lookup.GSM, lookup.LTW, lookup.WVR, lookup.ALC, lookup.CUL }
            .Where(x => x.RowId != 0 && x.IsValid)
            .Select(x => x.Value)
            .Where(CanCraft)
            .ToList();
        if(candidates.Count == 0) return false;
        var currentJob = Svc.ClientState.LocalPlayer?.ClassJob.RowId ?? 0;
        recipe = candidates.FirstOrDefault(x => x.CraftType.RowId + 8 == currentJob, candidates[0]);
        return true;
    }

    private static bool CanCraft(Recipe recipe)
    {
        if(!Svc.Data.GetExcelSheet<ClassJob>().TryGetRow(recipe.CraftType.RowId + 8, out var job)) return false;
        return PlayerState.Instance()->ClassJobLevels[job.ExpArrayIndex] >= recipe.RecipeLevelTable.Value.ClassJobLevel;
    }

    private static void Enqueue(uint recipeId, int crafts, int cost)
    {
        Running = true;
        StartedByUs = false;
        StopRequested = false;
        ListId = 0;
        VoyageScheduler.Enabled = false;
        P.TaskManager.Abort();
        TaskCleanupUi.Enqueue();

        P.TaskManager.Enqueue(() => !IsOccupied() && Player.Available, "RepairCraft.WaitFree", new(timeLimitMS: 60000, abortOnTimeout: false));
        P.TaskManager.Enqueue(() =>
        {
            try
            {
                ArtisanListBridge.DeleteGenerated();
                ListId = ArtisanListBridge.Create(recipeId, crafts);
                if(ArtisanCraftingIPC.GetEnduranceStatus()) ArtisanCraftingIPC.SetEnduranceStatus(false);
                ArtisanCraftingIPC.StartListById(ListId);
                StartedByUs = true;
            }
            catch(Exception e)
            {
                e.Log();
                S.AnomalyWindow.Add($"Could not create the Artisan list for repair materials: {e.Message}");
            }
        }, "RepairCraft.Start");
        P.TaskManager.Enqueue(() => !StartedByUs || ArtisanCraftingIPC.IsListRunning(), "RepairCraft.WaitStart", new(timeLimitMS: 30000, abortOnTimeout: false));
        P.TaskManager.Enqueue(() => !StartedByUs || !ArtisanCraftingIPC.IsListRunning(), "RepairCraft.Crafting", new(timeLimitMS: C.CraftRepairMaterialsTimeoutMinutes * 60 * 1000, abortOnTimeout: false));
        P.TaskManager.Enqueue(LeaveCraftingLog, "RepairCraft.LeaveCraftingLog", new(timeLimitMS: 120000, abortOnTimeout: false));
        P.TaskManager.Enqueue(() =>
        {
            Running = false;
            if(StopRequested) ArtisanCraftingIPC.SetStopRequest(false);
            if(ListId != 0) ArtisanListBridge.DeleteGenerated();
            var owned = InventoryManager.Instance()->GetInventoryItemCount(RepairMaterialsItemId);
            if(owned < cost)
            {
                S.AnomalyWindow.Add($"Crafting repair materials fell short ({owned}/{cost}), check the ingredients in your inventory");
            }
        }, "RepairCraft.Finish");
        TaskInteractWithNearestPanel.Enqueue();
        P.TaskManager.Enqueue(() => { VoyageScheduler.Enabled = true; }, "RepairCraft.ResumeVoyage");
    }

    private static bool LeaveCraftingLog()
    {
        if(!StartedByUs) return true;
        if(ArtisanCraftingIPC.IsListRunning())
        {
            if(EzThrottler.Throttle("RepairCraft.Stop", 5000))
            {
                ArtisanCraftingIPC.SetStopRequest(true);
                StopRequested = true;
            }
            return false;
        }
        if(Svc.Condition[ConditionFlag.Crafting] || Svc.Condition[ConditionFlag.ExecutingCraftingAction]) return false;
        if(TryGetAddonByName<AtkUnitBase>("RecipeNote", out var note) && note->IsVisible)
        {
            if(IsAddonReady(note) && EzThrottler.Throttle("RepairCraft.CloseRecipeNote", 1000)) Callback.Fire(note, true, -1);
            return false;
        }
        return !IsOccupied();
    }
}
