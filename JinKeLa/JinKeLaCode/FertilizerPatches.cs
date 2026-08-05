using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.TerrainFeatures;

using Object = StardewValley.Object;

namespace JinKeLa;

/// <summary>
/// 撒施金坷垃的 Harmony patches:
/// 1. Object.performUseAction      —— 右键点击田时,金坷垃不走原版施肥,改存 modData
/// 2. HoeDirt.plant                —— 兜底:任何入口(含箱子)都不写原版 fertilizer 字段
/// 3. HoeDirt.CheckApplyFertilizerRules —— 金坷垃永远 Okay(杜绝"已施过肥"提示,绿框预览也正常)
/// 4. HoeDirt.draw                 —— 田上画出金坷垃小袋
/// </summary>
internal static class FertilizerPatches
{
    // ---------------------------------------------------------------
    // 1. performUseAction:右键点击,把金坷垃写进 modData
    // ---------------------------------------------------------------
    public static bool UsePrefix(Object __instance, GameLocation location, ref bool __result)
    {
        if (!FertilizerLogic.IsJinKeLaItem(__instance.QualifiedItemId))
            return true; // 不是金坷垃,走原版

        __result = TryApplyJinKeLa(__instance, location);
        return false; // 吞掉原版,不让它把金坷垃当普通肥料处理
    }

    /// <summary>右键施用金坷垃:对玩家面前的田(原版施肥同款 GetGrabTile;含花盆 IndoorPot,经 GetHoeDirtAtTile)。</summary>
    internal static bool TryApplyJinKeLa(Object item, GameLocation location)
    {
        if (location is null)
            return false;

        Vector2 front = Game1.player.GetGrabTile();
        if (location.GetHoeDirtAtTile(front) is HoeDirt dirt)
        {
            if (ApplyToDirtWithConsume(dirt, location))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 对一块田施用金坷垃(不限制阶段:种前/生长中/成熟后都能撒)。
    /// 只写状态,不消耗物品——消耗由调用方处理(玩家右键消耗手持物)。
    /// </summary>
    internal static bool ApplyToDirt(HoeDirt dirt, GameLocation location)
    {
        if (FertilizerLogic.HasJinKeLa(dirt))
        {
            // 已撒过:提示但不消耗物品。
            // 注意:不能用原版 HoeDirt.cs.13916 —— 那条原文是"必须在种植之前添加"
            // (原版给发芽后施 368/369 用的),会造成误解。用自定义文案。
            Game1.showRedMessage("这块田已经施过金坷垃了");
            return false;
        }

        dirt.modData[FertilizerLogic.ModDataKey] = "1";
        location?.playSound("dirtyHit");
        return true;
    }

    /// <summary>右键施用金坷垃:成功时消耗玩家手持的金坷垃 1 个(与原版施肥一致)。</summary>
    internal static bool ApplyToDirtWithConsume(HoeDirt dirt, GameLocation location)
    {
        if (!ApplyToDirt(dirt, location))
            return false;

        if (Game1.player?.ActiveObject is Object active && FertilizerLogic.IsJinKeLaItem(active.QualifiedItemId))
            active.ConsumeStack(1);
        return true;
    }

    // ---------------------------------------------------------------
    // 3. HoeDirt.plant:兜底拦截,金坷垃不写原版 fertilizer 字段
    //    (箱子施肥路径也会走到这里,物品从手持/箱子消耗由原版处理)
    // ---------------------------------------------------------------
    public static bool PlantPrefix(HoeDirt __instance, string itemId, bool isFertilizer, ref bool __result)
    {
        if (!isFertilizer || !FertilizerLogic.IsJinKeLaItem(ItemRegistry.QualifyItemId(itemId) ?? itemId))
            return true;

        __result = ApplyToDirt(__instance, __instance.Location ?? Game1.currentLocation);
        return false;
    }

    // ---------------------------------------------------------------
    // 4. CheckApplyFertilizerRules:未撒永远 Okay(杜绝误弹"已施过肥");
    //    已撒过返回 HasThisFertilizer —— 预览框正确变红,箱子施肥路径也不会重复消耗
    // ---------------------------------------------------------------
    public static bool CheckRulesPrefix(HoeDirt __instance, string fertilizerId, ref HoeDirtFertilizerApplyStatus __result)
    {
        if (FertilizerLogic.IsJinKeLaItem(ItemRegistry.QualifyItemId(fertilizerId) ?? fertilizerId))
        {
            __result = FertilizerLogic.HasJinKeLa(__instance)
                ? HoeDirtFertilizerApplyStatus.HasThisFertilizer
                : HoeDirtFertilizerApplyStatus.Okay;
            return false;
        }
        return true;
    }

    // ---------------------------------------------------------------
    // 5. draw:田上有金坷垃就多画一袋(叠在原版肥料贴图之上,偏右上)
    //    贴图缓存 + 失败降级:CP 内容包缺失时不画,不炸渲染
    // ---------------------------------------------------------------
    private static Texture2D? _spriteTexture;
    private static bool _textureLoadFailed;

    public static void DrawPostfix(HoeDirt __instance, SpriteBatch spriteBatch)
    {
        if (!FertilizerLogic.HasJinKeLa(__instance))
            return;
        if (__instance.state.Value == 2) // 挖掘后的状态不画贴图
            return;
        if (_textureLoadFailed)
            return;
        if (_spriteTexture is null)
        {
            try
            {
                _spriteTexture = Game1.content.Load<Texture2D>("Mods/Claude.JinKeLa/Sprites");
            }
            catch (Exception)
            {
                _textureLoadFailed = true; // CP 包缺失等:降级为不画贴图
                return;
            }
        }

        Vector2 pos = Game1.GlobalToLocal(Game1.viewport, __instance.Tile * 64f);
        // 1.9E-08f 与原版肥料层一致,叠在其上偏右上一点
        spriteBatch.Draw(_spriteTexture, pos + new Vector2(8f, 8f), new Rectangle(0, 16, 16, 16),
            Color.White, 0f, Vector2.Zero, 2f, SpriteEffects.None, 1.91E-08f);
    }
}
