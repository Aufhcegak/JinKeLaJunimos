using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Characters;
using StardewValley.TerrainFeatures;

namespace JinKeLa;

/// <summary>
/// 双倍收获:Postfix 追加产出,不碰原版 IL。
///
/// 原理:金坷垃 50% 命中时,在本次收获的产物之上再追加 1 份原版产物。
/// - 手收/镰刀:createItemDebris 掉出(与原版多产物的掉法一致)
/// - 祝尼魔:tryToAddItemToHut
/// - 多季复收作物(蓝莓等):每次成熟收获独立判定,一季可多次触发
/// - 经验:原版按 num4 计算并已结算,追加这份不再多给经验,与原版"多产物同经验"一致
///
/// 只在收获成功时触发(__result == true):背包满/枯萎/野葱不翻倍。
/// </summary>
internal static class HarvestPatches
{
    /// <summary>bool 进参(hasJinKeLa && roll 命中)之后,追加产出。</summary>
    public static void HarvestPostfix(Crop __instance, int xTile, int yTile, HoeDirt soil, JunimoHarvester junimoHarvester, bool __result)
    {
        // 只对成功收获生效
        if (!__result)
            return;

        // 枯萎作物 / 野葱不翻倍
        if (__instance.dead.Value || __instance.forageCrop.Value)
            return;

        if (soil is null)
            return;

        if (!FertilizerLogic.HasJinKeLa(soil))
            return;

        // 确定性判定:联机各客户端结果一致(原版 random2 同思路,加 timeOfDay 区分同日多轮)
        if (!FertilizerLogic.RollDoubleHarvest(xTile, yTile))
            return;

        // 原版向日葵的 indexOfHarvest 是 421(向日葵花),harvest 时转成 431(向日葵种子)掉落;
        // 追加产物要与原版一致,否则玩家会拿到不存在于原版掉落表的花
        string itemId = __instance.indexOfHarvest.Value;
        if (string.IsNullOrWhiteSpace(itemId))
            return;
        if (itemId == "421")
            itemId = "431";

        // 追加产物按原版同公式判定品质:与品质肥料共存时不再恒为普通品质
        int quality = RollCropQuality(xTile, yTile, soil);
        Item bonus = ItemRegistry.Create(itemId, 1, quality);

        Vector2 pos = new(xTile * 64 + 32, yTile * 64 + 32);
        if (junimoHarvester != null)
        {
            junimoHarvester.tryToAddItemToHut(bonus);
            return;
        }

        // 手收/镰刀:掉出地面与原版一致
        Game1.createItemDebris(bonus, pos, -1);
    }

    /// <summary>
    /// 与原版 Crop.harvest 同公式的品质判定:
    /// 基础肥料(368)+1 / 品质肥料(369)+2 / Deluxe 肥料(919)+3;
    /// 概率 = 0.2×(耕种/10) + 0.2×加成×((耕种+2)/12) + 0.01;Deluxe 肥料额外可出铱星(概率减半)。
    /// 用与 RollDoubleHarvest 同思路的确定性种子(联机各端一致),+0.5 偏移避免与双倍判定同序列。
    /// </summary>
    private static int RollCropQuality(int xTile, int yTile, HoeDirt soil)
    {
        int boost = soil.fertilizer.Value switch
        {
            "(O)368" => 1,
            "(O)369" => 2,
            "(O)919" => 3,
            _ => 0
        };
        int level = Game1.player?.FarmingLevel ?? 0;
        double chance = 0.2 * (level / 10.0) + 0.2 * boost * ((level + 2) / 12.0) + 0.01;
        Random r = Utility.CreateRandom(
            (double)xTile * 7.0, (double)yTile * 11.0,
            Game1.stats.DaysPlayed, Game1.uniqueIDForThisGame, Game1.timeOfDay + 0.5);
        if (boost >= 3 && r.NextDouble() < chance / 2.0)
            return 4; // 铱星(仅 Deluxe 肥料)
        if (r.NextDouble() < chance)
            return 2; // 金星
        if (r.NextDouble() < chance)
            return 1; // 银星
        return 0;
    }
}
