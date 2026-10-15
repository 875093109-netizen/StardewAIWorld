namespace AIWorld.Core.Chat;

/// <summary>Language aliases only; available destinations always come from the live world.</summary>
public static class TravelLocationAliases
{
    private static readonly IReadOnlyDictionary<string, string[]> Chinese = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["Town"] = new[] { "小镇", "鹈鹕镇", "镇上" },
        ["Beach"] = new[] { "沙滩", "海滩" },
        ["Saloon"] = new[] { "酒吧", "星之果实酒吧" },
        ["Mountain"] = new[] { "山区", "山上" },
        ["Forest"] = new[] { "森林", "煤矿森林" },
        ["Farm"] = new[] { "农场" },
        ["FarmHouse"] = new[] { "农舍" },
        ["SeedShop"] = new[] { "杂货店", "皮埃尔的杂货店" },
        ["Hospital"] = new[] { "诊所", "医院" },
        ["Mine"] = new[] { "矿洞", "矿井" },
        ["LibraryMuseum"] = new[] { "博物馆", "图书馆" },
        ["WizardHouse"] = new[] { "巫师塔", "法师塔" },
        ["BusStop"] = new[] { "巴士站", "公交站" }
    };

    public static (string Canonical, string[] Aliases)[] Build(IEnumerable<(string Name, string? DisplayName)> locations) =>
        locations.GroupBy(l => l.Name, StringComparer.Ordinal).Select(group =>
        {
            var aliases = group.Select(l => l.DisplayName).Where(n => !string.IsNullOrWhiteSpace(n)).Cast<string>();
            if (Chinese.TryGetValue(group.Key, out var translated)) aliases = aliases.Concat(translated);
            return (group.Key, aliases.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        }).ToArray();
}
