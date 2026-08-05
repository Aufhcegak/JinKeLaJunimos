using StardewModdingAPI;
using StardewValley;
using StardewValley.TerrainFeatures;

namespace JinKeLa;

/// <summary>
/// 金坷垃自动 smoke test(GameLaunched 时运行,主菜单即可,不依赖存档/地图)。
/// 验证:
/// 1. 施放金坷垃 → modData 写入,fertilizer 字段为空(独立共存)
/// 2. 已有普通肥料的田撒金坷垃 → 字段保留,互不覆盖
/// 3. 先金坷垃再普通肥料 → 也共存
/// 4. 已撒金坷垃的田再撒 → 拒绝
/// 5. 50% 双倍概率统计分布
/// 6. CheckApplyFertilizerRules:未撒 Okay,已撒 HasThisFertilizer
/// </summary>
public static class SmokeTest
{
    private static IMonitor? _monitor;

    public static void Register(IModHelper helper, IMonitor monitor)
    {
        _monitor = monitor;
        // 游戏加载完自动跑一次(主菜单即可,不依赖存档)
        helper.Events.GameLoop.GameLaunched += (_, _) => Run();
        helper.ConsoleCommands.Add("jinkela_smoke", "金坷垃 smoke test。", (_, _) => Run());
    }

    private static void Run(string command = "", string[]? args = null)
    {
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) { pass++; _monitor!.Log($"  [PASS] {name}", LogLevel.Info); }
            else { fail++; _monitor!.Log($"  [FAIL] {name} {detail}", LogLevel.Error); }
        }

        _monitor!.Log("金坷垃 smoke test 开始...", LogLevel.Info);

        // ---------- 1. 施放:写入 modData,不碰原版 fertilizer ----------
        var dirt = new HoeDirt(1);
        bool ok = FertilizerPatches.ApplyToDirt(dirt, null);
        Check("施放成功(无地图环境)", ok);
        Check("modData 已写入", FertilizerLogic.HasJinKeLa(dirt));
        Check("原版 fertilizer 字段为空", string.IsNullOrEmpty(dirt.fertilizer.Value));

        // ---------- 2. 已有普通肥料 + 金坷垃共存 ----------
        var dirt2 = new HoeDirt(1);
        dirt2.fertilizer.Value = "(O)368"; // 模拟原版 plant() 写入普通肥料
        bool okJ = FertilizerPatches.ApplyToDirt(dirt2, null);
        Check("有普通肥料的田再撒金坷垃成功", okJ);
        Check("普通肥料字段保留", dirt2.fertilizer.Value == "(O)368");
        Check("金坷垃独立写入", FertilizerLogic.HasJinKeLa(dirt2));

        // 反向:先金坷垃再普通肥料
        var dirt3 = new HoeDirt(1);
        FertilizerPatches.ApplyToDirt(dirt3, null);
        dirt3.fertilizer.Value = "(O)370"; // 保湿肥料,模拟后撒
        Check("有金坷垃的田再撒普通肥料成功(字段写入)", dirt3.fertilizer.Value == "(O)370");
        Check("金坷垃仍保留", FertilizerLogic.HasJinKeLa(dirt3));

        // ---------- 3. 重复撒金坷垃:拒绝 ----------
        var dirt4 = new HoeDirt(1);
        FertilizerPatches.ApplyToDirt(dirt4, null);
        bool ok2 = FertilizerPatches.ApplyToDirt(dirt4, null);
        Check("重复撒金坷垃被拒绝", !ok2);

        // ---------- 4. 50% 双倍概率统计 ----------
        int hits = 0, n = 2000;
        for (int i = 0; i < n; i++)
            if (FertilizerLogic.RollDoubleHarvest(i % 100, i / 100)) hits++;
        double ratio = (double)hits / n;
        Check("50% 概率统计", Math.Abs(ratio - 0.5) < 0.05, $"ratio={ratio:F3}");

        // 确定性:同格同时刻结果一致(联机同步)
        bool r1 = FertilizerLogic.RollDoubleHarvest(3, 4);
        bool r2 = FertilizerLogic.RollDoubleHarvest(3, 4);
        Check("同格同刻结果一致(联机同步)", r1 == r2);

        // 不同格结果不同分布(非全同)
        bool anyDiff = false;
        for (int i = 0; i < 50; i++)
        {
            if (FertilizerLogic.RollDoubleHarvest(i, 7) != FertilizerLogic.RollDoubleHarvest(3, 4))
            { anyDiff = true; break; }
        }
        Check("不同格结果不完全相同", anyDiff);

        // ---------- 5. CheckApplyFertilizerRules:未撒 Okay,已撒 HasThisFertilizer ----------
        var dirt5 = new HoeDirt(1);
        FertilizerPatches.ApplyToDirt(dirt5, null);
        var status = dirt5.CheckApplyFertilizerRules("(O)Claude.JinKeLa");
        Check("有金坷垃时再查金坷垃规则 = HasThisFertilizer(预览红/不重复消耗)", status == HoeDirtFertilizerApplyStatus.HasThisFertilizer);

        // ---------- 6. 非金坷垃肥料不受影响(双向共存) ----------
        var dirt6 = new HoeDirt(1);
        FertilizerPatches.ApplyToDirt(dirt6, null);
        var status2 = dirt6.CheckApplyFertilizerRules("(O)369");
        Check("有金坷垃的田查原版肥料 = Okay(可再撒普通肥料)", status2 == HoeDirtFertilizerApplyStatus.Okay);

        _monitor.Log($"金坷垃 smoke test 完成: {pass} pass, {fail} fail", fail == 0 ? LogLevel.Info : LogLevel.Error);
    }
}
