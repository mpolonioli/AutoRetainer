using ECommons.Reflection;
using System.Collections;
using System.Reflection;

namespace AutoRetainer.Modules.Voyage.RepairCrafting;

// Artisan's IPC can start a list but not create one, so the list is added to
// Artisan.Config.NewCraftingLists through reflection and saved with Config.Save().
internal static class ArtisanListBridge
{
    internal const string ListPrefix = "[AutoRetainer]";

    private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    internal static int Create(uint recipeId, int crafts)
    {
        var (config, lists, listType) = GetLists();
        RemoveGenerated(lists);

        var recipesProperty = listType.GetProperty("Recipes") ?? throw Missing("NewCraftingList.Recipes");
        var itemType = recipesProperty.PropertyType.GetGenericArguments()[0];
        var optionsProperty = itemType.GetProperty("ListItemOptions") ?? throw Missing("ListItem.ListItemOptions");

        var list = Activator.CreateInstance(listType);
        var id = NewId(lists);
        Set(list, "ID", id);
        Set(list, "Name", $"{ListPrefix} Repair materials {DateTime.Now:yyyy-MM-dd HH:mm}");

        var item = Activator.CreateInstance(itemType);
        Set(item, "ID", recipeId);
        Set(item, "Quantity", crafts);
        optionsProperty.SetValue(item, Activator.CreateInstance(optionsProperty.PropertyType));
        ((IList)recipesProperty.GetValue(list)).Add(item);

        lists.Add(list);
        Save(config);
        return id;
    }

    internal static void DeleteGenerated()
    {
        try
        {
            var (config, lists, _) = GetLists();
            if(RemoveGenerated(lists)) Save(config);
        }
        catch(Exception e)
        {
            e.LogWarning();
        }
    }

    private static bool RemoveGenerated(IList lists)
    {
        var removed = false;
        for(var i = lists.Count - 1; i >= 0; i--)
        {
            if(lists[i]?.GetType().GetProperty("Name")?.GetValue(lists[i]) is string name && name.StartsWith(ListPrefix))
            {
                lists.RemoveAt(i);
                removed = true;
            }
        }
        return removed;
    }

    private static int NewId(IList lists)
    {
        var used = lists.Cast<object>().Select(l => l.GetType().GetProperty("ID")?.GetValue(l) as int? ?? 0).ToHashSet();
        int id;
        do id = Random.Shared.Next(100, 50000);
        while(used.Contains(id));
        return id;
    }

    private static (object Config, IList Lists, Type ListType) GetLists()
    {
        if(!DalamudReflector.TryGetDalamudPlugin(ArtisanCraftingIPC.InternalName, out var plugin, false, true))
            throw new InvalidOperationException("Artisan is not loaded");

        var type = plugin.GetType();
        var config = type.GetField("Config", AnyInstance)?.GetValue(plugin)
                     ?? type.GetProperty("Config", AnyInstance)?.GetValue(plugin)
                     ?? throw Missing("Artisan.Config");

        var listsProperty = config.GetType().GetProperty("NewCraftingLists") ?? throw Missing("Configuration.NewCraftingLists");
        var lists = listsProperty.GetValue(config) as IList ?? throw Missing("Configuration.NewCraftingLists value");
        return (config, lists, listsProperty.PropertyType.GetGenericArguments()[0]);
    }

    private static void Save(object config)
    {
        var save = config.GetType().GetMethod("Save", AnyInstance, Type.EmptyTypes) ?? throw Missing("Configuration.Save()");
        save.Invoke(config, null);
    }

    private static void Set(object target, string property, object value)
    {
        var info = target.GetType().GetProperty(property, AnyInstance) ?? throw Missing($"{target.GetType().Name}.{property}");
        info.SetValue(target, Convert.ChangeType(value, info.PropertyType));
    }

    private static InvalidOperationException Missing(string member) => new($"Artisan member {member} not found");
}
