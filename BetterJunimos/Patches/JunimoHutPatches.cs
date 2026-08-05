using System;
using System.Linq;
using System.Collections.Generic;
using BetterJunimos.Utils;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Characters;

// ReSharper disable InconsistentNaming

namespace BetterJunimos.Patches {
    /* areThereMatureCropsWithinRadius **OVERWRITES PREFIX**
     *
     * Search for actionable tiles
     * Completely rewrite original function.
     */
    internal class PatchSearchAroundHut {
        // 性能修复：按 (小屋, 日期, 游戏时间) 缓存扫描结果。
        // 原版每 tick 清缓存 = 每 10 分钟切换帧全扫温室 630 格 + 半径 289 格，
        // 每格 IdentifyJunimoAbility 查全部能力 = 大棚/温室大时切换帧卡顿主因。
        //
        // **缓存正确性修复**：缓存命中时直接返回旧结果有两个 bug——
        //   1. 同一 10 分钟内作物被玩家收割/浇水后，缓存仍返回旧值，小屋不停派祝尼魔出工空跑；
        //   2. 缓存命中不更新 hut.lastKnownCropLocation，祝尼魔找不到作物。
        // 修复：缓存有效期缩短为 30 tick（0.5 秒，足够消切换帧尖峰，又不掩盖真实变化），
        // 且每次真正扫描（含缓存失效后的重扫）都更新 lastKnownCropLocation。
        private static readonly Dictionary<(JunimoHut, int, int), (bool Result, int Tick)> _scanCache = new();

        public static bool Prefix(JunimoHut __instance, ref bool __result) {
            if (!Context.IsMainPlayer) return true;

            // 缓存命中条件：同一天 + 同一游戏时间 + 距上次扫描 < 30 tick。
            // 切换帧（每 10 分钟）只触发一次扫描；30 tick 内的小间隔查询全部走缓存，
            // 削平切换帧尖峰；超过 30 tick 后强制重扫，保证作物被收/状态变化能反映出来。
            var key = (__instance, Game1.dayOfMonth, Game1.timeOfDay);
            int tick = (int)Game1.ticks;
            if (_scanCache.TryGetValue(key, out var cached)
                && cached.Tick >= 0
                && tick - cached.Tick < 30) {
                __result = cached.Result;
                return false;
            }

            // Prevent unnecessary searching when unpaid
            if (BetterJunimos.Config.JunimoPayment.WorkForWages && !Util.Payments.WereJunimosPaidToday) {
                __instance.lastKnownCropLocation = Point.Zero;
                _scanCache[key] = (false, tick);
                return false;
            }

            __result = SearchAroundHut(__instance);
            // 缓存结果连同扫描时刻一起存（30 tick 后失效）
            _scanCache[key] = (__result, tick);
            return false;
        }

        // search for crops + open plantable spots
        private static bool SearchAroundHut(JunimoHut hut) {
            var id = Util.GetHutIdFromHut(hut);
            var radius = Util.CurrentWorkingRadius;
            GameLocation farm = hut.GetParentLocation();

            // SearchHutGrid manages hut.lastKnownCropLocation and Util.Abilities.lastKnownCropLocations
            var foundWork = SearchHutGrid(hut, radius, farm, id);

            if (BetterJunimos.Config.JunimoImprovements.CanWorkInGreenhouse) {
                var ghb = Util.Greenhouse.GreenhouseBuildingNearHut(id);
                var gh = Game1.getLocationFromName("Greenhouse");
                if (ghb != null) {
                    gh = ghb;
                }

                if (!Util.Greenhouse.HutHasGreenhouse(id)) {
                    return foundWork;
                }

                // SearchGreenhouseGrid manages hut.lastKnownCropLocation (a hack!) and Util.Abilities.lastKnownCropLocations
                foundWork |= SearchGreenhouseGrid(hut, id, gh);
                Util.Abilities.lastKnownCropLocations.TryGetValue((hut, gh), out var lkc);
            }

            return foundWork;
        }

        /// <summary>
        /// Search the Greenhouse for work to do, and update
        /// hut.lastKnownCropLocation and
        /// Util.Abilities.lastKnownCropLocations
        /// with the location of any work found
        /// </summary>
        /// <param name="hut">JunimoHut to search</param>
        /// <param name="hut_guid">GUID of hut to search</param>
        /// <returns>True if there's any work to do</returns>
        internal static bool SearchGreenhouseGrid(JunimoHut hut, Guid hut_guid, GameLocation gl = null) {
            var gh = Game1.getLocationFromName("Greenhouse");
            if (gl != null) {
                gh = gl;
            }

            for (var x = 0; x < gh.map.Layers[0].LayerWidth; x++) {
                for (var y = 0; y < gh.map.Layers[0].LayerHeight; y++) {
                    var pos = new Vector2(x, y);
                    var ability = Util.Abilities.IdentifyJunimoAbility(gh, pos, hut_guid);
                    if (ability == null) continue;
                    hut.lastKnownCropLocation = new Point(x, y);
                    Util.Abilities.lastKnownCropLocations[(hut, gh)] = new Point(x, y);
                    return true;
                }
            }

            Util.Abilities.lastKnownCropLocations[(hut, gh)] = Point.Zero;
            return false;
        }

        private static bool SearchHutGrid(JunimoHut hut, int radius, GameLocation farm, Guid id) {
            // 外层 Prefix 已按 (hut, day, timeOfDay) 缓存，这里直接扫
            for (var x = hut.tileX.Value + 1 - radius; x < hut.tileX.Value + 2 + radius; ++x) {
                for (var y = hut.tileY.Value + 1 - radius; y < hut.tileY.Value + 2 + radius; ++y) {
                    var pos = new Vector2(x, y);
                    var ability = Util.Abilities.IdentifyJunimoAbility(farm, pos, id);
                    if (ability == null) continue;

                    hut.lastKnownCropLocation = new Point(x, y);
                    Util.Abilities.lastKnownCropLocations[(hut, farm)] = new Point(x, y);
                    return true;
                }
            }

            hut.lastKnownCropLocation = Point.Zero;
            Util.Abilities.lastKnownCropLocations[(hut, farm)] = Point.Zero;
            return false;
        }
    }

    /* Update
     *
     * To allow more junimos, allow working in rain
     */
    [HarmonyPriority(Priority.Low)]
    internal class ReplaceJunimoHutupdateWhenFarmNotCurrentLocation {
        // This is to prevent the update function from running, other than base.Update()
        // Capture sendOutTimer and use to stop execution
        public static bool Prefix(JunimoHut __instance, GameTime time, ref int ___junimoSendOutTimer, out int __state) {
            __state = ___junimoSendOutTimer;
            ___junimoSendOutTimer = 0;
            if (__state <= 0) return true;
            if (!Context.IsMainPlayer) return true;

            ___junimoSendOutTimer = __state - time.ElapsedGameTime.Milliseconds;
            // Winter
            if (__instance.GetParentLocation().IsWinterHere() && !Util.Progression.CanWorkInWinter) {
                return true;
            }

            // Rain
            if (__instance.GetParentLocation().IsRainingHere() && !Util.Progression.CanWorkInRain) {
                return true;
            }

            // Currently sending out a junimo
            if (___junimoSendOutTimer > 0) {
                return true;
            }

            // Already enough junimos
            if (__instance.myJunimos.Count >= Util.Progression.MaxJunimosUnlocked) {
                return true;
            }

            // Nothing to do
            if (!__instance.areThereMatureCropsWithinRadius()) {
                return true;
            }

            Util.SpawnJunimoAtHut(__instance);
            ___junimoSendOutTimer = 1000;
            return true;
        }
    }

    /* dayUpdate
     *
     * To allow more junimos, allow working
     */
    [HarmonyPriority(Priority.VeryHigh)]
    internal class ReplaceJunimoHutdayUpdate {
        public static void Postfix(JunimoHut __instance, int dayOfMonth) {
            __instance.shouldSendOutJunimos.Value = true;
            __instance.cropHarvestRadius = Util.CurrentWorkingRadius;
        }
    }

    /* Update
     *
     * To allow more junimos, allow working in rain
     */
    [HarmonyPriority(Priority.Low)]
    internal class ReplaceJunimoHutUpdate {
        // This is to prevent the update function from running, other than base.Update()
        // Capture sendOutTimer and use to stop execution
        public static void Prefix(JunimoHut __instance, ref int ___junimoSendOutTimer, out int __state) {
            __state = ___junimoSendOutTimer;
            ___junimoSendOutTimer = 0;
        }

        public static void Postfix(JunimoHut __instance, GameTime time, ref int ___junimoSendOutTimer, int __state) {
            if (__state <= 0) return;
            if (!Context.IsMainPlayer) return;
            ___junimoSendOutTimer = __state - time.ElapsedGameTime.Milliseconds;

            // Don't work on farmEvent days
            // if (Game1.farmEvent != null)
            //     return;
            // Winter
            if (__instance.GetParentLocation().IsWinterHere() && !Util.Progression.CanWorkInWinter) {
                return;
            }

            // Rain
            if (__instance.GetParentLocation().IsRainingHere() && !Util.Progression.CanWorkInRain) {
                return;
            }

            // Currently sending out a junimo
            if (___junimoSendOutTimer > 0) {
                return;
            }

            // Already enough junimos
            if (__instance.myJunimos.Count >= Util.Progression.MaxJunimosUnlocked) {
                return;
            }

            // Nothing to do
            if (!__instance.areThereMatureCropsWithinRadius()) {
                return;
            }

            Util.SpawnJunimoAtHut(__instance);
            ___junimoSendOutTimer = 1000;
        }
    }

    /*
     * performTenMinuteAction
     *
     * Add the end to trigger more than 3 junimos to spawn
     */
    [HarmonyPriority(Priority.Low)]
    internal class ReplaceJunimoTimerNumber {
        public static void Postfix(JunimoHut __instance, ref int ___junimoSendOutTimer) {
            if (!Context.IsMainPlayer) return;

            foreach (var location in Game1.locations) {
                if (location.IsGreenhouse) {
                    foreach (var npc in location.characters) {
                        if (npc is JunimoHarvester jh && jh.home == __instance) {
                            if (!__instance.myJunimos.Contains(jh)) {
                                __instance.myJunimos.Add(jh);
                                jh.pokeToHarvest();
                            }
                        }
                    }
                }
            }

            var time = Util.Progression.CanWorkInEvenings ? 2400 : 1900;
            if (Game1.timeOfDay > time) return;

            if (__instance.myJunimos.Count < Util.Progression.MaxJunimosUnlocked) {
                ___junimoSendOutTimer = 1;
            }
        }
    }

    /*
     * performTenMinuteAction
     *
     * Complete rewrite to allow Junimos in greenhouse
     */
    internal class ReplaceTenMinuteAction {
        public static bool Prefix(int timeElapsed, JunimoHut __instance, ref int ___junimoSendOutTimer) {
            if (!Context.IsMainPlayer) return true;

            // ((Building) __instance).performTenMinuteAction(timeElapsed);
            for (var index = __instance.myJunimos.Count - 1; index >= 0; --index) {
                // if (!Game1.getFarm().characters.Contains(__instance.myJunimos[index]))
                //     __instance.myJunimos.RemoveAt(index);
                // else
                __instance.myJunimos[index].pokeToHarvest();
            }

            if (Game1.timeOfDay is >= 2000 and < 2400 && !__instance.GetParentLocation().IsWinterHere() && Game1.random.NextDouble() < 0.2) {
                __instance.wasLit.Value = true;
            } else {
                if (Game1.timeOfDay != 2400 || __instance.GetParentLocation().IsWinterHere()) return false;
                __instance.wasLit.Value = false;
            }

            var time = Util.Progression.CanWorkInEvenings ? 2400 : 1900;
            if (Game1.timeOfDay > time) return false;

            if (__instance.myJunimos.Count < Util.Progression.MaxJunimosUnlocked) {
                ___junimoSendOutTimer = 1;
            }

            return false;
        }
    }

    /* getUnusedJunimoNumber
     *
     * Completely rewrite method to support more than 3 junimos
     * The only difference is the use of MaxJunimos
     */
    [HarmonyPriority(Priority.Low)]
    internal class ReplaceJunimoHutNumber {
        public static bool Prefix(JunimoHut __instance, ref int __result) {
            if (!Context.IsMainPlayer) return true;
            for (var index = 0; index < Util.Progression.MaxJunimosUnlocked; ++index) {
                if (index >= __instance.myJunimos.Count) {
                    __result = index;
                    return false;
                }

                var flag = __instance.myJunimos.Any(junimo => junimo.whichJunimoFromThisHut == index);

                if (flag) continue;
                __result = index;
                return false;
            }

            __result = 2;
            return false;
        }
    }
}