using StardewModdingAPI;
using StardewValley;
using StardewValley.TerrainFeatures;

namespace JinKeLa;

/// <summary>
/// 金坷垃:50% 概率双倍收获,与原版肥料/生长激素完全独立共存。
///
/// 设计:
/// - 原版每块田只能存一种肥料(HoeDirt.fertilizer 字段),这是"肥料互斥"的根源。
///   金坷垃不写该字段,而是存进 HoeDirt.modData["Claude.JinKeLa"],与原版肥料物理上并存。
/// - 撒施走原版入口 HoeDirt.plant(itemId, who, isFertilizer: true):
///   · CanApplyFertilizer 对 Category -19 物品被 Object.cs 施放处调用,返回 false 会弹"已施过肥"——
///     必须 prefix 拦截,让金坷垃永远允许。
///   · plant() 内部会写 fertilizer 字段 —— 必须 patch 掉,改走 modData。
/// - 收获翻倍:transpiler 插入到 crop.harvest() 的 num4 累加逻辑之后
///   (num4 = 基础产量 + ExtraHarvestChance 累加 + 幸运翻倍),之后所有产出/经验都基于 num4。
///   金坷垃 50% 命中时 num4 翻倍;经验值按原版公式算在 num4 之上,与翻倍一致。
/// </summary>
public sealed class FertilizerLogic
{
    public const string ItemId = "Claude.JinKeLa";
    public const string QualifiedItemId = "(O)Claude.JinKeLa";

    /// <summary>modData key,存在 HoeDirt.modData 里。</summary>
    public const string ModDataKey = "Claude.JinKeLa.Applied";

    /// <summary>金坷垃是否撒在这块田上。任何玩家/祝尼魔/机器都能判断。</summary>
    public static bool HasJinKeLa(HoeDirt? dirt)
        => dirt is not null && dirt.modData.TryGetValue(ModDataKey, out var v) && v == "1";

    /// <summary>这是否是金坷垃物品。</summary>
    public static bool IsJinKeLaItem(string qualifiedItemId)
        => qualifiedItemId == QualifiedItemId;

    /// <summary>
    /// 50% 双倍判定。用确定性种子随机(与 harvest 原版的 random2 同思路):
    /// 种子 = 格坐标 + 天数 + uniqueID + 当日时间 —— 各客户端(主机/客机)结果一致,
    /// 且同格不同收获轮次独立(复收作物每轮可不同)。联机存档同步不崩。
    /// </summary>
    public static bool RollDoubleHarvest(int xTile, int yTile)
        => Utility.CreateRandom(
                (double)xTile * 7.0,
                (double)yTile * 11.0,
                Game1.stats.DaysPlayed,
                Game1.uniqueIDForThisGame,
                Game1.timeOfDay
            ).NextDouble() < 0.5;
}
