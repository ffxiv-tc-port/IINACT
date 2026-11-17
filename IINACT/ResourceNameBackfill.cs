using FFXIV_ACT_Plugin.Common;
using FFXIV_ACT_Plugin.Resource;
using Lumina.Excel;
using ActionSheet = Lumina.Excel.Sheets.Action;
using ContentFinderConditionSheet = Lumina.Excel.Sheets.ContentFinderCondition;
using StatusSheet = Lumina.Excel.Sheets.Status;
using TerritoryTypeSheet = Lumina.Excel.Sheets.TerritoryType;

namespace IINACT;

// 本檔契約：FFXIV_ACT_Plugin 內嵌的名稱表跟不上台服改版，新技能／狀態／區域會顯示成 unknown。
// 只在 ResourceManager.LoadResources() 之後、任何解析執行緒啟動之前呼叫一次 ⇒ 直接改 SortedDictionary 不加鎖。
// 只新增名稱表裡沒有的鍵，以及把技能／狀態的 _rsv_ 佔位值換成遊戲名稱；遊戲表名稱空白或本身也是 _rsv_ 的列不動，
// 其他既有值一個都不改。
internal static class ResourceNameBackfill
{
    internal readonly record struct Counts(
        int Actions, int Statuses, int Territories, int ActionsReplaced, int StatusesReplaced);

    /// <param name="language">解析設定用的語言；名稱只寫進這個語言的表（遊戲表給的就是當下語言）。</param>
    public static Counts Apply(
        ResourceManager resources, Language language, ExcelSheet<ActionSheet> actions,
        ExcelSheet<StatusSheet> statuses, ExcelSheet<TerritoryTypeSheet> territories,
        ExcelSheet<ContentFinderConditionSheet> contents)
    {
        int actionCount = 0, actionReplaced = 0;
        if (resources._actionList is ActionList actionList &&
            actionList._actionList.TryGetValue(language, out var actionNames) && actionNames != null)
        {
            // 技能表查不到時 GetActionNameByID 會退回道具技能表；補進技能表會蓋掉那個既有名稱。
            var itemActions = actionList._itemActionList;
            foreach (var row in actions)
            {
                if (row.Name.IsEmpty || itemActions?.ContainsKey(row.RowId) == true)
                    continue;
                Fill(actionNames, row.RowId, row, static r => r.Name.ExtractText(), ref actionCount, ref actionReplaced);
            }
        }

        int statusCount = 0, statusReplaced = 0;
        if (resources._statusList is StatusList statusList &&
            statusList._statusList.TryGetValue(language, out var statusNames) && statusNames != null)
        {
            foreach (var row in statuses)
            {
                if (row.Name.IsEmpty)
                    continue;
                Fill(statusNames, row.RowId, row, static r => r.Name.ExtractText(), ref statusCount, ref statusReplaced);
            }
        }

        // 區域表只有英、韓、繁中三份，其他語言查的是英文表；刻意不把別種語言的名稱寫進英文表。
        var territoryCount = 0;
        if ((language is Language.English or Language.Korean or Language.TraditionalChinese) &&
            resources._territoryList is TerritoryList territoryList &&
            territoryList._territories.TryGetValue(language, out var territoryNames) && territoryNames != null)
        {
            var contentNames = ContentNamesByTerritory(contents);
            foreach (var row in territories)
            {
                if (territoryNames.ContainsKey(row.RowId))
                    continue;
                var name = TerritoryName(row, contentNames);
                if (!string.IsNullOrWhiteSpace(name))
                    territoryCount += TryAdd(territoryNames, row.RowId, territoryList.FixZoneNameNumbers(name));
            }
        }

        return new Counts(actionCount, statusCount, territoryCount, actionReplaced, statusReplaced);
    }

    /// <summary>
    /// 比照 FFXIV_ACT_Plugin 名稱表的產生方式：先找指向這個區域、列號最小的任務搜尋器名稱，
    /// 再看區域自己的任務搜尋器欄，都沒有才用地名。
    /// </summary>
    internal static string TerritoryName(TerritoryTypeSheet row, IReadOnlyDictionary<uint, string> contentNames)
    {
        if (contentNames.TryGetValue(row.RowId, out var byContent))
            return byContent;
        var content = row.ContentFinderCondition.ValueNullable?.Name.ExtractText();
        return string.IsNullOrWhiteSpace(content) ? row.PlaceName.ValueNullable?.Name.ExtractText() ?? "" : content;
    }

    internal static Dictionary<uint, string> ContentNamesByTerritory(ExcelSheet<ContentFinderConditionSheet> contents)
    {
        var names = new Dictionary<uint, string>();
        foreach (var row in contents)
        {
            var territory = row.TerritoryType.RowId;
            if (territory == 0 || row.Name.IsEmpty || names.ContainsKey(territory))
                continue;
            var name = row.Name.ExtractText();
            if (!string.IsNullOrWhiteSpace(name))
                names.Add(territory, name);
        }

        return names;
    }

    /// <summary>判準與 FFXIV_ACT_Plugin 的 ResourceFile 相同（前綴、不分大小寫）。</summary>
    internal static bool IsPlaceholder(string value) => value.StartsWith("_rsv_", StringComparison.OrdinalIgnoreCase);

    /// <summary>鍵不在就新增；值是 _rsv_ 佔位才取代。名稱只在需要寫入時才解出來。</summary>
    private static void Fill<TRow>(
        SortedDictionary<uint, string> names, uint id, TRow row, Func<TRow, string> getName, ref int added, ref int replaced)
    {
        if (!names.TryGetValue(id, out var existing))
        {
            added += TryAdd(names, id, getName(row));
            return;
        }

        if (!IsPlaceholder(existing))
            return;
        var name = getName(row);
        if (!IsUsableName(name))
            return;
        names[id] = name;
        replaced++;
    }

    /// <summary>遊戲表沒公開的名稱在客戶端資料裡也是 _rsv_ 佔位（實際名稱由伺服器另送），視同沒有名稱。</summary>
    private static bool IsUsableName(string name) => !string.IsNullOrWhiteSpace(name) && !IsPlaceholder(name);

    private static int TryAdd(SortedDictionary<uint, string> names, uint id, string name)
    {
        if (!IsUsableName(name))
            return 0;
        names.Add(id, name);
        return 1;
    }
}
