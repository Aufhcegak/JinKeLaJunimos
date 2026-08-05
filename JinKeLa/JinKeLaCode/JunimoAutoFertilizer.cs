using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;

namespace JinKeLa;

/// <summary>
/// 祝尼魔自动施肥调度器(独立于 Better Junimos 能力系统)。
///
/// 为什么需要它:
/// 用户的 JunimoTaskScheduler 幽灵模式拦截了 SpawnJunimoAtHut + 接管
/// pathfindToNewCrop/tryToHarvestHere,BJ 的 IdentifyJunimoAbility→PerformAction
/// 能力链路根本不执行 → 挂在 BJ 能力上的金坷垃施肥永不触发。
/// (日志证据:SMAPI 里 [jts_ghost] 全游戏: 收割 0, 浇水 400(5 个小屋))
///
/// 做法:本 mod 自己用 SMAPI UpdateTicked(节流)扫描每座小屋周围(含温室),
/// 对"没撒过金坷垃"的田,从小屋输出箱取金坷垃施放(写 modData)。
/// 与 JTS 的幽灵浇水/收割互不冲突——它们管水/收,本调度只管金坷垃。
/// BJ 存在时也跑(双保险,同一块田不会重复撒)。
/// </summary>
public static class JunimoAutoFertilizer
{
    private const int ScanEveryTicks = 120;      // 每 2 秒扫一次
    private const int FullScanEvery = 15;        // 每 15 次(30秒)全量扫
    private const int FertilizeBudgetPerScan = 20; // 单帧施放预算(避免卡顿)

    private static IMonitor? _monitor;
    private static int _tickCounter;

    /// <summary>ModEntry.Entry 里调用。</summary>
    public static void Register(IModHelper helper, IMonitor monitor)
    {
        _monitor = monitor;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
    }

    private static void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (!Game1.hasLoadedGame || Game1.CurrentEvent != null || Game1.IsClient)
            return;

        _tickCounter++;
        if (_tickCounter % ScanEveryTicks != 0)
            return;

        bool fullScan = _tickCounter % (ScanEveryTicks * FullScanEvery) == 0;
        try
        {
            int applied = 0;
            foreach (var hut in GetAllHuts())
                applied += ProcessHut(hut, fullScan);

            if (applied > 0)
                _monitor?.Log($"[jkl_auto] 祝尼魔自动施放了 {applied} 次金坷垃。", LogLevel.Info);
        }
        catch (Exception ex)
        {
            _monitor?.Log($"[jkl_auto] 自动施肥扫描异常: {ex.Message}", LogLevel.Warn);
        }
    }

    private static IEnumerable<JunimoHut> GetAllHuts()
    {
        foreach (var farm in Game1.locations.OfType<Farm>())
        {
            foreach (var building in farm.buildings)
            {
                if (building is JunimoHut hut && hut.GetOutputChest() is not null)
                    yield return hut;
            }
        }
        // 其他可建造地点(如姜岛农场)
        foreach (var loc in Game1.locations)
        {
            if (loc is not Farm && loc.IsBuildableLocation())
            {
                foreach (var building in loc.buildings)
                {
                    if (building is JunimoHut hut && hut.GetOutputChest() is not null)
                        yield return hut;
                }
            }
        }
    }

    private static int ProcessHut(JunimoHut hut, bool fullScan)
    {
        var chest = hut.GetOutputChest();
        if (chest is null || !HasJinKeLaInChest(chest))
            return 0;

        int applied = 0;
        var parent = hut.GetParentLocation();
        if (parent != null)
        {
            int r = hut.cropHarvestRadius;
            int x0 = hut.tileX.Value + 1 - r, y0 = hut.tileY.Value + 1 - r;
            int x1 = hut.tileX.Value + 2 + r, y1 = hut.tileY.Value + 2 + r;
            applied += ScanLocation(parent, hut, chest, x0, y0, x1, y1, fullScan, applied);
        }

        // 小屋半径内的温室室内(原版 Greenhouse / 姜岛温室)
        if (applied < FertilizeBudgetPerScan)
        {
            foreach (var indoor in GreenhousesNearHut(hut))
            {
                applied += ScanLocation(indoor, hut, chest, 0, 0,
                    indoor.map?.DisplayWidth / 64 ?? 0, indoor.map?.DisplayHeight / 64 ?? 0,
                    fullScan, applied);
                if (applied >= FertilizeBudgetPerScan)
                    break;
            }
        }
        return applied;
    }

    private static int ScanLocation(GameLocation loc, JunimoHut hut, Chest chest,
        int x0, int y0, int x1, int y1, bool fullScan, int alreadyApplied)
    {
        if (x0 >= x1 || y0 >= y1)
            return 0;
        int mapW = loc.map?.DisplayWidth / 64 ?? x1;
        int mapH = loc.map?.DisplayHeight / 64 ?? y1;
        x0 = Math.Max(x0, 0); y0 = Math.Max(y0, 0);
        x1 = Math.Min(x1, mapW); y1 = Math.Min(y1, mapH);

        int applied = 0;
        for (int x = x0; x < x1 && alreadyApplied + applied < FertilizeBudgetPerScan; x++)
        {
            for (int y = y0; y < y1; y++)
            {
                var tile = new Vector2(x, y);
                if (!loc.terrainFeatures.TryGetValue(tile, out var tf) || tf is not HoeDirt dirt)
                    continue;
                if (FertilizerLogic.HasJinKeLa(dirt))
                    continue;

                if (TryApplyFromChest(chest, dirt, loc))
                {
                    applied++;
                    if (alreadyApplied + applied >= FertilizeBudgetPerScan)
                        break;
                }
            }
        }
        return applied;
    }

    private static bool HasJinKeLaInChest(Chest chest)
        => chest.Items.Any(i => i is StardewValley.Object o && FertilizerLogic.IsJinKeLaItem(o.QualifiedItemId));

    /// <summary>从箱子取一个金坷垃施到田上(写 modData)。返回是否施放成功。</summary>
    private static bool TryApplyFromChest(Chest chest, HoeDirt dirt, GameLocation loc)
    {
        var item = chest.Items.FirstOrDefault(i => i is StardewValley.Object o && FertilizerLogic.IsJinKeLaItem(o.QualifiedItemId));
        if (item is null)
            return false;

        dirt.modData[FertilizerLogic.ModDataKey] = "1";
        if (Utility.isOnScreen(Utility.Vector2ToPoint(dirt.Tile), 64, loc))
            loc.playSound("dirtyHit");

        // 消耗(尊重 BJ 无限库存配置)
        if (!BetterJunimosInfiniteInventory())
        {
            item.Stack -= 1;
            if (item.Stack <= 0)
                chest.Items.Remove(item);
        }
        return true;
    }

    /// <summary>反射读 BJ 的 InfiniteJunimoInventory;BJ 不在或失败 → false(正常消耗)。</summary>
    private static bool BetterJunimosInfiniteInventory()
    {
        try
        {
            Type? bjType = Type.GetType("BetterJunimos.BetterJunimos, BetterJunimos");
            if (bjType is null)
                return false;
            object? config = bjType.GetProperty("Config")?.GetValue(null);
            object? fun = config?.GetType().GetProperty("FunChanges")?.GetValue(config);
            return fun?.GetType().GetProperty("InfiniteJunimoInventory")?.GetValue(fun) is true;
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<GameLocation> GreenhousesNearHut(JunimoHut hut)
    {
        var parent = hut.GetParentLocation();
        if (parent is null || !parent.IsBuildableLocation())
            yield break;
        int r = hut.cropHarvestRadius;
        int x0 = hut.tileX.Value + 1 - r, y0 = hut.tileY.Value + 1 - r;
        var found = new HashSet<GameLocation>();
        for (int x = x0; x < hut.tileX.Value + 2 + r; x++)
        {
            for (int y = y0; y < hut.tileY.Value + 2 + r; y++)
            {
                var tile = new Vector2(x, y);
                if (!parent.isTilePassable(tile))
                    continue;
                foreach (var building in parent.buildings)
                {
                    if (!building.occupiesTile(tile))
                        continue;
                    var indoor = building.GetIndoors();
                    if (indoor != null && indoor.IsGreenhouse && found.Add(indoor))
                        yield return indoor;
                }
            }
        }
    }
}
