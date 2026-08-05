using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Characters;
using StardewValley.TerrainFeatures;

using Object = StardewValley.Object;

namespace JinKeLa;

/// <summary>
/// 金坷垃:与原版肥料/生长激素完全独立共存的增产化肥。
/// - 耕种 9 级解锁配方:金锭1 + 铱锭1 + 纤维1 → 25 个
/// - 皮埃尔商店 200g/个
/// - 收获时 50% 概率双倍产出
/// </summary>
public sealed class ModEntry : Mod
{
    private Harmony? _harmony;

    public override void Entry(IModHelper helper)
    {
        this._harmony = new Harmony(this.ModManifest.UniqueID);
        this._harmony.PatchAll(typeof(ModEntry).Assembly);
        JunimoIntegration.Apply(this.Helper, this._harmony, this.Monitor);
        JunimoAutoFertilizer.Register(helper, this.Monitor);

        helper.ConsoleCommands.Add("jinkela_test", "金坷垃测试:给10个,并选正前方一格地施放。", this.CommandTest);
        helper.ConsoleCommands.Add("jinkela_crop", "金坷垃测试:把当前田上的作物设为成熟。", this.CommandCrop);
        SmokeTest.Register(helper, this.Monitor);
        this.Monitor.Log("金坷垃 loaded。配方:耕种9级;商店:皮埃尔200g;效果:50%双倍收获。", LogLevel.Info);
    }

    private void CommandTest(string command, string[] args)
    {
        Game1.player.addItemToInventory(ItemRegistry.Create("(O)Claude.JinKeLa", 10));
        this.Monitor.Log("给了10个金坷垃。", LogLevel.Info);
    }

    private void CommandCrop(string command, string[] args)
    {
        Vector2 front = Game1.player.GetGrabTile();
        if (Game1.currentLocation.terrainFeatures.TryGetValue(front, out var feature) && feature is HoeDirt dirt && dirt.crop is not null)
        {
            dirt.crop.currentPhase.Value = dirt.crop.phaseDays.Count - 1;
            dirt.crop.fullyGrown.Value = true;
            dirt.crop.dayOfCurrentPhase.Value = 0;
            this.Monitor.Log("作物已设为成熟。", LogLevel.Info);
        }
        else
        {
            this.Monitor.Log("前方没有作物。", LogLevel.Info);
        }
    }
}

// ============================================================
// Patch 声明(供 PatchAll 自动注册)
// ============================================================

/// <summary>Object.performUseAction:右键撒施金坷垃。</summary>
[HarmonyPatch(typeof(Object), nameof(Object.performUseAction))]
internal static class PatchUse
{
    [HarmonyPrefix]
    private static bool Prefix(Object __instance, GameLocation location, ref bool __result)
        => FertilizerPatches.UsePrefix(__instance, location, ref __result);
}

/// <summary>HoeDirt.plant:金坷垃不写原版 fertilizer 字段(含箱子路径)。</summary>
[HarmonyPatch(typeof(HoeDirt), nameof(HoeDirt.plant))]
internal static class PatchPlant
{
    [HarmonyPrefix]
    private static bool Prefix(HoeDirt __instance, string itemId, bool isFertilizer, ref bool __result)
        => FertilizerPatches.PlantPrefix(__instance, itemId, isFertilizer, ref __result);
}

/// <summary>HoeDirt.CheckApplyFertilizerRules:金坷垃永远 Okay。</summary>
[HarmonyPatch(typeof(HoeDirt), nameof(HoeDirt.CheckApplyFertilizerRules))]
internal static class PatchRules
{
    [HarmonyPrefix]
    private static bool Prefix(HoeDirt __instance, string fertilizerId, ref HoeDirtFertilizerApplyStatus __result)
        => FertilizerPatches.CheckRulesPrefix(__instance, fertilizerId, ref __result);
}

/// <summary>HoeDirt.draw:田上画金坷垃小袋。</summary>
[HarmonyPatch(typeof(HoeDirt), nameof(HoeDirt.draw))]
internal static class PatchDraw
{
    [HarmonyPostfix]
    private static void Postfix(HoeDirt __instance, SpriteBatch spriteBatch)
        => FertilizerPatches.DrawPostfix(__instance, spriteBatch);
}

/// <summary>Crop.harvest:50% 概率追加一份产出。</summary>
[HarmonyPatch(typeof(Crop), nameof(Crop.harvest))]
internal static class PatchHarvest
{
    [HarmonyPostfix]
    private static void Postfix(Crop __instance, int xTile, int yTile, HoeDirt soil, JunimoHarvester junimoHarvester, bool __result)
        => HarvestPatches.HarvestPostfix(__instance, xTile, yTile, soil, junimoHarvester, __result);
}
