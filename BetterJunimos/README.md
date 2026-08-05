# Better Junimos（性能修复版）

> ⚠️ **重要声明**：本仓库**不是原创 mod**，是 **N 网（Nexus Mods）Better Junimos**（作者 hawkfalcon/ceruleandeep）的**性能修复版**。
> 原版地址：https://www.nexusmods.com/stardewvalley/mods/2293
> 源码基于：https://github.com/hawkfalcon/Stardew-Mods

## 编译

```bash
# 仓库自带 csproj（照 ModBuildConfig 标准），直接：
dotnet build -c Release
# 产物在 bin/Release/net6.0/BetterJunimos.dll，复制到 Mods/BetterJunimos/ 即可
```

## 改动内容（相对 N 网原版 3.1.2）

### 性能修复：寻路重试限制
- **文件**：`Patches/JunimoHarvesterPatches.cs` → `PatchPathfindToRandomSpotAroundHut`
- **问题**：祝尼魔找干活目标点失败时（目标被栅栏/建筑围住），原版**最多重试 6 次全图 A* 寻路**，每次都是 CPU 重活。多个祝尼魔 × 每 10 分钟 = **切换帧 150ms+ 卡顿**。
- **修复**：重试上限从 5 降到 1（最多 2 次尝试）。成功路径完全不变（第一次成功就跳出），失败很快放弃，等下个 10 分钟再试。

### 性能修复：扫描结果缓存（30 tick 过期）
- **文件**：`Patches/JunimoHutPatches.cs` → `PatchSearchAroundHut`
- **问题**：原版每 tick 清缓存 = 每 10 分钟切换帧全扫温室 630 格 + 半径 289 格，每格 `IdentifyJunimoAbility` 查全部能力 = 大温室切换帧卡顿主因。
- **修复**：按 `(小屋, 日期, 游戏时间)` 缓存扫描结果，**30 tick（0.5 秒）过期**。既消切换帧尖峰，又不会像"整段 10 分钟不过期"那样**掩盖作物被玩家收割/状态变化**（旧缓存会让小屋不停派祝尼魔出工空跑）。扫描真正执行时更新 `lastKnownCropLocation`。

### 性能修复：小屋查找缓存（按天）
- **文件**：`Utils/Util.cs` → `GetHutFromId` / `GetHutIdFromHut`
- **问题**：原实现每次调用都 `GetAllFarms()` 全扫所有地点×所有建筑（O(地点×建筑)），而 `GetHutFromId` 在祝尼魔 `update` / `pathfind` 里每帧多次调用，是隐性卡顿源。
- **修复**：按天缓存 GUID→小屋 / 小屋→GUID 双向映射，换天失效。新造的小屋全扫补一次并回填。

### Bug 修复：温室随机终点参数错乱
- **文件**：`Patches/JunimoHarvesterPatches.cs` → `EndPointInGreenhouse`
- **问题**：原版 `Game1.random.Next(-(gw/2+2), gh/2-2)` 下限错用 `gw/2`、上限错用 `gh/2`，两坐标范围畸形，地图尺寸不标准时**上限 ≤ 下限直接抛 ArgumentOutOfRangeException**，祝尼魔卡死。
- **修复**：各坐标对称范围 `中心 ±(边长/2 - 2)`，始终落在边界内（不踩墙）。

## 与 N 网原版的差异

| 文件 | 差异 |
|---|---|
| `Patches/JunimoHarvesterPatches.cs` | 寻路重试 `retry <= 5` → `retry <= 1`；温室终点参数修复 |
| `Patches/JunimoHutPatches.cs` | 扫描缓存 30 tick 过期 + 命中时更新 lastKnownCropLocation |
| `Utils/Util.cs` | GetHutFromId/GetHutIdFromHut 按天缓存 |
| 其余所有文件 | 与原版一致，未改动 |

## 版本
- v3.1.2（基础版，未改动）
- 性能修复：见上文改动内容
