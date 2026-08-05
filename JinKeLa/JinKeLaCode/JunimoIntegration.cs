using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Characters;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;

using Object = StardewValley.Object;

namespace JinKeLa;

/// <summary>
/// BetterJunimos 集成:祝尼魔小屋箱子里的金坷垃,祝尼魔会自动撒到"还没有金坷垃"的田上。
///
/// 为什么要 patch(而不是只用它的 API):
/// - BJ 内置 FertilizeAbility 的判断要求"无原版肥料 && (无作物 || 作物阶段≤1)",
///   它不认金坷垃的 modData,覆盖不了"已有原版肥料/作物长大了"的田。
/// - 更危险的是它 PerformAction 会拿箱子里第一个 -19 类物品(金坷垃也是 -19),
///   直接写 fertilizer.Value = ParentSheetIndex,既消耗金坷垃又写坏肥料字段。
///
/// 做法(全反射 + 手动 Harmony,金坷垃对 BJ 零编译依赖;BJ 不在就整体跳过):
/// 1. PerformAction 前缀:箱子扫描顺序与 BJ 完全一致。
///    - 该田按原版规则能施肥、且排最前的是普通肥料 → 放行原版;
///    - 否则轮到金坷垃:已撒过→不消耗;未撒→写 modData 并消耗 1 个。
///    - 该田原版规则不能施肥(已有肥料/作物阶段>1)→ 直接走金坷垃路径。
/// 2. IsActionAvailable 后置:原版判定不通过的田,只要没撒过金坷垃且箱子里有,
///    判定为可行动(覆盖"有原版肥料/作物长大了"的田)。
/// 边界:已撒过不重复消耗;箱子没金坷垃不动;消耗逻辑尊重 BJ 的 InfiniteJunimoInventory;
/// 联机由主机执行(祝尼魔 AI 只在主机跑);任何一步反射失败→放行原版,不影响 BJ 自身。
/// </summary>
internal static class JunimoIntegration
{
    private const string BjModId = "hawkfalcon.BetterJunimos";

    private static IMonitor? _monitor;
    private static MethodInfo? _getHutFromId;
    private static Func<bool> _infiniteInventory = static () => false;

    /// <summary>ModEntry.Entry 里调用。BJ 不在或反射失败都只记日志,绝不抛异常。</summary>
    public static void Apply(IModHelper helper, Harmony harmony, IMonitor monitor)
    {
        _monitor = monitor;

        if (!helper.ModRegistry.IsLoaded(BjModId))
        {
            monitor.Log("未检测到 BetterJunimos,祝尼魔集成跳过(原版祝尼魔不会施肥,无需处理)。", LogLevel.Debug);
            return;
        }

        try
        {
            Type? fertType = AccessTools.TypeByName("BetterJunimos.Abilities.FertilizeAbility");
            Type? utilType = AccessTools.TypeByName("BetterJunimos.Utils.Util");
            MethodInfo? isAvailable = fertType is null ? null : AccessTools.Method(fertType, "IsActionAvailable");
            MethodInfo? perform = fertType is null ? null : AccessTools.Method(fertType, "PerformAction");
            _getHutFromId = utilType is null ? null : AccessTools.Method(utilType, "GetHutFromId", new[] { typeof(Guid) });
            if (isAvailable is null || perform is null || _getHutFromId is null)
            {
                monitor.Log("接入 BetterJunimos 失败:找不到目标方法(BJ 版本可能变了),金坷垃本体不受影响。", LogLevel.Warn);
                return;
            }

            _infiniteInventory = BuildInfiniteInventoryProbe();

            harmony.Patch(
                isAvailable,
                postfix: new HarmonyMethod(typeof(JunimoIntegration), nameof(IsAvailablePostfix))
            );
            harmony.Patch(
                perform,
                prefix: new HarmonyMethod(typeof(JunimoIntegration), nameof(PerformPrefix))
            );
            monitor.Log("已接入 BetterJunimos:祝尼魔会用小屋箱子里的金坷垃给田施肥(已撒过/箱子没有/无限物品模式均有边界处理)。", LogLevel.Info);
        }
        catch (Exception e)
        {
            monitor.Log($"接入 BetterJunimos 失败(不影响金坷垃本体): {e}", LogLevel.Warn);
        }
    }

    /// <summary>反射 BJ 配置 FunChanges.InfiniteJunimoInventory;失败默认 false(正常消耗)。</summary>
    private static Func<bool> BuildInfiniteInventoryProbe()
    {
        try
        {
            Type? bjType = AccessTools.TypeByName("BetterJunimos.BetterJunimos");
            object? config = bjType is null ? null : AccessTools.Property(bjType, "Config")?.GetValue(null);
            object? fun = config is null ? null : AccessTools.Property(config.GetType(), "FunChanges")?.GetValue(config);
            PropertyInfo? inf = fun is null ? null : AccessTools.Property(fun.GetType(), "InfiniteJunimoInventory");
            if (inf is null)
                return static () => false;
            return () => inf.GetValue(fun) is true;
        }
        catch
        {
            return static () => false;
        }
    }

    // ---------------------------------------------------------------
    // IsActionAvailable 后置:原版不要的田,金坷垃要
    // ---------------------------------------------------------------
    public static void IsAvailablePostfix(GameLocation location, Vector2 pos, Guid guid, ref bool __result)
    {
        if (__result)
            return; // 原版判定已通过,不用管
        try
        {
            HoeDirt? dirt = GetDirt(location, pos);
            if (dirt is null || FertilizerLogic.HasJinKeLa(dirt))
                return;
            Chest? chest = GetHutChest(guid);
            if (FindJinKeLa(chest) is not null)
                __result = true;
        }
        catch
        {
            // 静默:判定失败就维持原样,不影响 BJ
        }
    }

    // ---------------------------------------------------------------
    // PerformAction 前缀:拦截"把金坷垃当普通肥料乱写"的路径
    // ---------------------------------------------------------------
    public static bool PerformPrefix(GameLocation location, Vector2 pos, JunimoHarvester junimo, Guid guid, ref bool __result)
    {
        try
        {
            HoeDirt? dirt = GetDirt(location, pos);
            Chest? chest = GetHutChest(guid);
            if (dirt is null || chest is null)
                return true; // 异常情况,完全交给原版

            Item? firstFert = FirstVanillaStyleFertilizer(chest);
            Item? jinkela = FindJinKeLa(chest);

            // 与 BJ 原版判定保持一致的"这块田能不能按原版规则施肥"
            bool vanillaEligible = dirt.fertilizer.Value is null
                && (dirt.crop is null || dirt.crop.currentPhase.Value <= 1);

            // 原版能施、且箱子扫描排第一的是普通肥料 → 让原版逻辑去施普通肥料
            if (vanillaEligible && firstFert is not null && !ReferenceEquals(firstFert, jinkela))
                return true;

            // 金坷垃路径:没有金坷垃 / 已撒过 → 不消耗,报告失败(进 BJ 冷却,不反复尝试)
            if (jinkela is null || FertilizerLogic.HasJinKeLa(dirt))
            {
                __result = false;
                return false;
            }

            __result = FertilizerPatches.ApplyToDirt(dirt, location);
            if (__result)
                ConsumeOne(chest, jinkela);
            return false;
        }
        catch (Exception e)
        {
            _monitor?.Log($"祝尼魔撒金坷垃出错,已放行原版逻辑: {e.Message}", LogLevel.Warn);
            return true;
        }
    }

    // ---------------------------------------------------------------
    // 工具
    // ---------------------------------------------------------------
    private static HoeDirt? GetDirt(GameLocation location, Vector2 pos)
        => location.terrainFeatures.TryGetValue(pos, out TerrainFeature? feature) ? feature as HoeDirt : null;

    private static Chest? GetHutChest(Guid guid)
        => _getHutFromId?.Invoke(null, new object?[] { guid }) is JunimoHut hut ? hut.GetOutputChest() : null;

    /// <summary>与 BJ 完全一致的箱子扫描:第一个 Category==-19 且非树肥(805)的物品。</summary>
    private static Item? FirstVanillaStyleFertilizer(Chest chest)
        => chest.Items.FirstOrDefault(i => i is { Category: -19 } && i.ItemId != "805");

    private static Item? FindJinKeLa(Chest? chest)
        => chest?.Items.FirstOrDefault(i => i is Object o && FertilizerLogic.IsJinKeLaItem(o.QualifiedItemId));

    /// <summary>与 BJ 的 Util.RemoveItemFromChest 一致:尊重 InfiniteJunimoInventory 配置。</summary>
    private static void ConsumeOne(Chest chest, Item item)
    {
        if (_infiniteInventory())
            return;
        item.Stack -= 1;
        if (item.Stack <= 0)
            chest.Items.Remove(item);
    }
}
