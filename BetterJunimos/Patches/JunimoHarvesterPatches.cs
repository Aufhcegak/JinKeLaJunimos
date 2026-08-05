using StardewValley;
using Microsoft.Xna.Framework;
using StardewValley.Characters;
using HarmonyLib;
using BetterJunimos.Utils;
using System;
using System.Linq;
using Netcode;
using BetterJunimos.Abilities;
using StardewModdingAPI;
using StardewValley.Buildings;
using StardewValley.Pathfinding;

namespace BetterJunimos.Patches {
    /* foundCropEndFunction
     *
     * Is there an action to perform at the end of this pathfind?
     * Completely replace
     */
    public class PatchFindingCropEnd {
        public static bool Prefix(JunimoHarvester __instance, ref PathNode currentNode, ref NetGuid ___netHome, ref bool __result) {
            __result = Util.Abilities.IsActionable(__instance.currentLocation, new Vector2(currentNode.x, currentNode.y), ___netHome.Value);

            return false;
        }
    }

    /* tryToHarvestHere
     *
     * Try to perform ability
     * Except harvest
     * Completely replace
     *
     */
    public class PatchTryToHarvestHere {
        public static bool Prefix(JunimoHarvester __instance, ref int ___harvestTimer, ref NetGuid ___netHome) {
            if (!Context.IsMainPlayer) return true;
            var hut = Util.GetHutFromId(__instance.HomeId);
            if (hut is null) return false;
            var id = __instance.HomeId;
            var pos = __instance.Tile;
            int time;
            var junimoAbility = Util.Abilities.IdentifyJunimoAbility(__instance.currentLocation, pos, id);
            if (junimoAbility != null) {
                if (junimoAbility is HarvestBushesAbility) {
                    // Use the update() harvesting
                    time = 2000;
                } else if (!Util.Abilities.PerformAction(junimoAbility, id, __instance.currentLocation, pos, __instance)) {
                    // didn't succeed, move on
                    time = 0;

                    // add failed action to ability cooldowns
                    Util.Abilities.ActionFailed(__instance.currentLocation, junimoAbility, pos);
                } else {
                    // succeeded, shake
                    if (junimoAbility is HarvestCropsAbility) {
                        time = 2000;
                    }
                    else if (BetterJunimos.Config.JunimoImprovements.WorkRidiculouslyFast)
                        time = 20;
                    else
                        time = Util.Progression.WorkFaster ? 300 : 998;
                }
            } else {
                // nothing to do, wait a moment
                time = Util.Progression.WorkFaster ? 5 : 200;
                __instance.pokeToHarvest();
            }

            ___harvestTimer = time;

            return false;
        }
    }

    // update
    // Animate & handle action timer 
    public class PatchJunimoShake {
        public static void Postfix(JunimoHarvester __instance, ref int ___harvestTimer) {
            if (!Context.IsMainPlayer) return;

            if (Util.Progression.WorkFaster && ___harvestTimer == 999) {
                // skip last second of harvesting if faster
                ___harvestTimer = 0;
            } else if (___harvestTimer is > 500 and < 1000 || Util.Progression.WorkFaster && ___harvestTimer > 5) {
                __instance.shake(50);
            }
        }
    }

    // pathfindToRandomSpotAroundHut
    // Expand radius of random pathfinding
    public class PatchPathfindToRandomSpotAroundHut {
        public static void Postfix(JunimoHarvester __instance) {
            var hut = Util.GetHutFromId(__instance.HomeId);
            if (hut is null) return;

            var radius = Util.CurrentWorkingRadius;
            var retry = 0;
            // 性能修复：原版重试 6 次全图 A* 寻路，目标不可达时每次都是重活，
            // 多个祝尼魔 × 每 10 分钟 = 切换帧卡顿。改为最多 2 次：
            // 成功路径完全不变（第一次成功就跳出），失败也很快放弃。
            do {
                var endPoint = __instance.currentLocation.IsGreenhouse ?
                    EndPointInGreenhouse(__instance) : EndPointInFarm(hut, radius);

                // BetterJunimos.SMonitor.Log($"PatchPathfindToRandomSpotAroundHut: " +
                //                            $"#{__instance.whichJunimoFromThisHut} " +
                //                            $"in {__instance.currentLocation.Name} " +
                //                            $"from [{__instance.getTileX()} {__instance.getTileX()}] " +
                //                            $"to [{endPoint.X} {endPoint.Y}]",
                //     LogLevel.Debug);

                __instance.controller = new PathFindController(__instance, __instance.currentLocation, endPoint, -1, __instance.reachFirstDestinationFromHut, 100);
                retry++;
            } while (retry <= 1 && __instance.controller?.pathToEndPoint == null);
        }

        private static Point EndPointInGreenhouse(JunimoHarvester jh) {
            var gw = jh.currentLocation.map.Layers[0].LayerWidth;
            var gh = jh.currentLocation.map.Layers[0].LayerHeight;
            // **修复**：原版下限错用 gw/2、上限错用 gh/2，两坐标范围畸形，
            // 上限可能 ≤ 下限 → Game1.random.Next 抛 ArgumentOutOfRangeException（祝尼魔卡死）。
            // 改为各坐标对称范围：中心 ±(边长/2 - 2)，始终落在温室边界内（不踩墙）。
            var x = gw / 2 + Game1.random.Next(-(gw / 2 - 2), gw / 2 - 2);
            var y = gh / 2 + Game1.random.Next(-(gh / 2 - 2), gh / 2 - 2);
            return new Point(x, y);
        }

        private static Point EndPointInFarm(JunimoHut hut, int radius) {
            return Utility.Vector2ToPoint(new Vector2(hut.tileX.Value + 1 + Game1.random.Next(-radius, radius + 1), hut.tileY.Value + 1 + Game1.random.Next(-radius, radius + 1)));
        }
    }

    [HarmonyPriority(Priority.Low)]
    public class PatchGet_home {
        public static void Postfix(ref JunimoHut __result, ref NetGuid ___netHome) {
            __result = Util.GetHutFromId(___netHome.Value);
        }
    }

    [HarmonyPriority(Priority.Low)]
    public class PatchSet_home {
        public static void Postfix(JunimoHut value, ref NetGuid ___netHome) {
            ___netHome.Value = Util.GetHutIdFromHut(value);
        }
    }

    // pathfindToNewCrop - completely replace 
    // Remove the max distance boundary
    [HarmonyPriority(Priority.Low)]
    public class PatchPathfindDoWork {
        public static bool Prefix(JunimoHarvester __instance, ref NetEvent1Field<int, NetInt> ___netAnimationEvent) {
            if (!Context.IsMainPlayer) return true;
            var hut = Util.GetHutFromId(__instance.HomeId);
            if (hut is null) return true;

            var quittingTime = Util.Progression.CanWorkInEvenings ? 2400 : 1900;


            if (Game1.timeOfDay > quittingTime) {
                // bedtime, all Junimos return to huts and/or despawn
                Util.Progression.PromptForCanWorkInEvenings();
                if (__instance.controller != null) return false;

                if (__instance.currentLocation.NameOrUniqueName == hut.GetParentLocation().NameOrUniqueName) {
                    __instance.returnToJunimoHut(__instance.currentLocation);
                }

                if (__instance.currentLocation.IsGreenhouse) {
                    returnToGreenhouseDoor(__instance, __instance.currentLocation);
                } else {
                    // can't walk back to the hut from here, just despawn
                    __instance.junimoReachedHut(__instance, __instance.currentLocation);
                }
            }

            // Prevent working when not paid
            else if (BetterJunimos.Config.JunimoPayment.WorkForWages && !Util.Payments.WereJunimosPaidToday) {
                if (Game1.random.NextDouble() < 0.02) {
                    __instance.pathfindToRandomSpotAroundHut();
                } else {
                    // go on strike
                    ___netAnimationEvent.Fire(7);
                }
            } else if (hut.noHarvest.Value || (Game1.random.NextDouble() < 0.035 && !BetterJunimos.Config.JunimoImprovements.WorkRidiculouslyFast)) {
                // Hut has nothing to harvest
                // TODO: fix for greenhouse

                // BetterJunimos.SMonitor.Log($"PatchPathfindDoWork: {__instance.whichJunimoFromThisHut} hut noHarvest {hut.noHarvest.Value}", LogLevel.Debug);
                // if (__instance.currentLocation.IsGreenhouse)
                // {
                //     BetterJunimos.SMonitor.Log($"PatchPathfindDoWork v2, Is greenhouse but {hut.noHarvest.Value}", LogLevel.Debug);

                // }
                __instance.pathfindToRandomSpotAroundHut();
            } else {
                // walk to work?
                __instance.controller = new PathFindController(__instance, __instance.currentLocation, __instance.foundCropEndFunction, -1, __instance.reachFirstDestinationFromHut,
                    100, Point.Zero);

                var radius = Util.CurrentWorkingRadius;
                var outsideRadius = __instance.controller.pathToEndPoint is not null && hut.tileX is not null && hut.tileY is not null && __instance.currentLocation is not null &&
                    __instance.currentLocation.NameOrUniqueName == hut.GetParentLocation().NameOrUniqueName && (
                        Math.Abs(__instance.controller.pathToEndPoint.Last().X - hut.tileX.Value - 1) > radius || Math.Abs(__instance.controller.pathToEndPoint.Last().Y - hut.tileY.Value - 1) > radius);

                if (__instance.controller.pathToEndPoint != null && !outsideRadius) {
                    // Junimo has somewhere to be, let it happen
                    ___netAnimationEvent.Fire(0);
                } else {
                    // Junimo has no path, or path endpoint is outside the hut radius
                    Util.Abilities.lastKnownCropLocations.TryGetValue((hut, __instance.currentLocation), out var lkc);
                    if (Game1.random.NextDouble() < 0.5 && !lkc.Equals(Point.Zero)) {
                        // hut has some work to do, send Junimo there
                        __instance.controller = new PathFindController(__instance, __instance.currentLocation, lkc, -1, __instance.reachFirstDestinationFromHut, 100);
                    } else if (Game1.random.NextDouble() < 0.25) {
                        // unlucky, send Junimo home
                        ___netAnimationEvent.Fire(0);

                        if (__instance.currentLocation is Farm) {
                            __instance.returnToJunimoHut(__instance.currentLocation);
                        } else if (__instance.currentLocation.IsGreenhouse) {
                            returnToGreenhouseDoor(__instance, __instance.currentLocation);
                        } else {
                            // can't walk back to the hut from here, just despawn
                            __instance.junimoReachedHut(__instance, __instance.currentLocation);
                        }
                    } else {
                        // move Junimo randomly
                        __instance.pathfindToRandomSpotAroundHut();
                    }
                }
            }

            return false;
        }

        private static void returnToGreenhouseDoor(JunimoHarvester junimo, GameLocation location) {
            if (Utility.isOnScreen(Utility.Vector2ToPoint(junimo.position.Value / 64f), 64, junimo.currentLocation)) junimo.jump();
            junimo.collidesWithOtherCharacters.Value = false;

            if (Game1.IsMasterGame) {
                var door = GreenhouseDoor(junimo, location);
                if (door == Point.Zero) {
                    junimo.junimoReachedHut(junimo, junimo.currentLocation);
                    return;
                }

                junimo.controller = new PathFindController(junimo, location, door, 1, junimo.junimoReachedHut);
                if (junimo.controller.pathToEndPoint == null || junimo.controller.pathToEndPoint.Count == 0) {
                    junimo.junimoReachedHut(junimo, junimo.currentLocation);
                    return;
                }
            }

            if (!Utility.isOnScreen(Utility.Vector2ToPoint(junimo.position.Value / 64f), 64, junimo.currentLocation)) return;
            location.playSound("junimoMeep1");
        }

        public static Point GreenhouseDoor(JunimoHarvester junimo, GameLocation location) {
            //TryFind warp to hutlocation
            var warp = location.warps.FirstOrDefault(warp => warp.TargetName == junimo.home.GetParentLocation().NameOrUniqueName);
            if (warp != null) {
                return new Point(warp.X, warp.Y - 1);
            }

            return Point.Zero;
        }
    }

    // pokeToHarvest
    //public void pokeToHarvest()
    public class PatchPokeToHarvest {
        public static void Postfix(JunimoHarvester __instance, bool ___destroy) {
            if (___destroy) return;
            if (__instance.controller != null) return;
            if (!BetterJunimos.Config.JunimoImprovements.WorkRidiculouslyFast) return;
            __instance.pathfindToNewCrop();
        }
    }

    // 挡路修复：祝尼魔空闲且站在传送门（warp）tile 上时，把它挪开一格让出门口。
    // 访客反馈祝尼魔堵在温室/小屋门口导致进不去——原版祝尼魔寻路失败后
    // 会停在门口不动，挡住玩家。每帧检查一次，轻量。
    public class PatchJunimoMoveOffWarp {
        public static void Postfix(JunimoHarvester __instance) {
            if (!Context.IsMainPlayer) return;
            if (__instance.controller != null) return; // 在走路，不干预
            if (__instance.isMoving()) return;

            GameLocation location = __instance.currentLocation;
            if (location == null || location.warps == null || location.warps.Count == 0) return;

            Rectangle bounds = __instance.GetBoundingBox();
            if (location.isCollidingWithWarp(bounds, __instance) != null) {
                // 站在 warp 上：往小屋方向挪一格
                JunimoHut hut = Util.GetHutFromId(__instance.HomeId);
                Vector2 dir;
                if (hut != null && hut.GetParentLocation() != null && hut.GetParentLocation() == location) {
                    dir = new Vector2(hut.tileX.Value + 1, hut.tileY.Value + 1) - __instance.Tile;
                } else {
                    dir = new Vector2(0, -1); // 默认往上挪
                }
                if (dir.Length() < 0.01f) dir = new Vector2(0, -1);
                dir.Normalize();

                Vector2 newPos = __instance.Tile + dir;
                if (!location.isCollidingPosition(new Rectangle((int)(newPos.X * 64f), (int)(newPos.Y * 64f), 32, 32), Game1.viewport, false, 0, false, __instance, false, false)) {
                    __instance.Position = new Vector2(newPos.X * 64f, newPos.Y * 64f);
                } else if (!location.isCollidingPosition(new Rectangle((int)((__instance.Tile.X) * 64f), (int)((__instance.Tile.Y - 1f) * 64f), 32, 32), Game1.viewport, false, 0, false, __instance, false, false)) {
                    __instance.Position = new Vector2(__instance.Tile.X * 64f, (__instance.Tile.Y - 1f) * 64f);
                }
            }
        }
    }
}