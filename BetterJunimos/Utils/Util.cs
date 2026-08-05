using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Characters;
using StardewValley.Menus;
using StardewValley.Objects;
using SObject = StardewValley.Object;

namespace BetterJunimos.Utils {
    public class Util {
        private const int UnpaidRadius = 3;
        public const int CoffeeId = 433;
        public const string CoffeeItemId = "433";

        private const int GemCategory = -2;
        private const int MineralCategory = -12;

        public const int ForageCategory = -81;
        public const int FlowerCategory = -80;
        public const int FruitCategory = -79;
        public const int WineCategory = -26;

        internal static IReflectionHelper Reflection;
        internal static JunimoAbilities Abilities;
        internal static JunimoPayments Payments;
        internal static JunimoProgression Progression;
        internal static JunimoGreenhouse Greenhouse;

        public static List<GameLocation> GetAllFarms() {
            return Game1.locations.ToList();
        }

        // ------------------------------------------------------------------
        // 性能修复：GUID → 小屋 / 小屋 → GUID 加缓存。
        // 原实现每次调用都 GetAllFarms() 全扫所有地点×所有建筑（O(地点×建筑)），
        // 而 GetHutFromId 在祝尼魔 update / pathfind 里每帧多次调用，是隐性卡顿源。
        // 缓存：建筑 ID（NetGuid）与建筑引用在运行期稳定，换天/存档加载后失效即可。
        // ------------------------------------------------------------------
        private static readonly Dictionary<Guid, JunimoHut> _hutByIdCache = new();
        private static readonly Dictionary<JunimoHut, Guid> _hutIdCache = new();
        private static int _hutCacheDay = -1;

        private static void EnsureHutCacheFresh() {
            // 换天（过夜）时祝尼魔全部重置、小屋建筑可能重建 → 缓存失效。
            // 用 TotalDays（跨季节不重置）而不是 dayOfMonth（每季回到 1，会撞键）。
            int totalDays;
            try { totalDays = Game1.Date.TotalDays; } catch { return; }
            if (_hutCacheDay == totalDays) return;
            _hutCacheDay = totalDays;
            _hutByIdCache.Clear();
            _hutIdCache.Clear();
            foreach (var farm in GetAllFarms()) {
                foreach (var building in farm.buildings) {
                    if (building is JunimoHut hut) {
                        // 用 id.Value（NetGuid，建筑身份，1.6 稳定）做键
                        Guid id = hut.id.Value;
                        if (!_hutByIdCache.ContainsKey(id)) {
                            _hutByIdCache[id] = hut;
                        }
                        _hutIdCache[hut] = id;
                    }
                }
            }
        }

        public static Guid GetHutIdFromHut(JunimoHut hut) {
            if (hut == null) return Guid.Empty;
            EnsureHutCacheFresh();
            if (_hutIdCache.TryGetValue(hut, out var cached)) return cached;
            // 不在缓存（新造的小屋）→ 全扫补一次，并回填缓存
            var found = GetAllFarms().Select(farm => farm.buildings.GuidOf(hut)).ToList().Find(guid => guid != Guid.Empty);
            if (found != Guid.Empty) _hutIdCache[hut] = found;
            return found;
        }

        public static JunimoHut GetHutFromId(Guid id) {
            if (id == Guid.Empty) return null;
            EnsureHutCacheFresh();
            if (_hutByIdCache.TryGetValue(id, out var hut)) return hut;
            // 不在缓存（新造的小屋）→ 全扫补一次，并回填缓存
            foreach (var farm in GetAllFarms()) {
                if (farm.buildings.TryGetValue(id, out var b) && b is JunimoHut jh) {
                    _hutByIdCache[id] = jh;
                    return jh;
                }
            }
            BetterJunimos.SMonitor.Log($"Could not find hut with id {id}", LogLevel.Warn);
            return null;
        }

        public static int CurrentWorkingRadius {
            get {
                if (!BetterJunimos.Config.JunimoPayment.WorkForWages) return BetterJunimos.Config.JunimoHuts.MaxRadius;
                if (Payments.WereJunimosPaidToday) return BetterJunimos.Config.JunimoHuts.MaxRadius;
                return UnpaidRadius;
            }
        }

        public static List<JunimoHut> GetAllHuts() {
            return GetAllFarms().SelectMany(farm => farm.buildings.OfType<JunimoHut>().ToList()).ToList();
        }

        public static void AddItemToChest(GameLocation farm, Chest chest, SObject item) {
            Item obj = chest.addItem(item);
            if (obj == null) return;
            Vector2 pos = chest.TileLocation;
            for (int index = 0; index < obj.Stack; ++index) Game1.createObjectDebris(item.ItemId, (int)pos.X + 1, (int)pos.Y + 1, -1, item.Quality, 1f, farm);
        }

        public static void RemoveItemFromChest(Chest chest, Item item, int count = 1) {
            if (BetterJunimos.Config.FunChanges.InfiniteJunimoInventory) {
                return;
            }

            item.Stack -= count;
            if (item.Stack <= 0) {
                chest.Items.Remove(item);
            }
        }

        public static void SpawnJunimoAtHut(JunimoHut hut) {
            var pos = new Vector2((float)hut.tileX.Value + 1, (float)hut.tileY.Value + 1) * 64f + new Vector2(0.0f, 32f);
            SpawnJunimoAtPosition(hut.GetParentLocation(), pos, hut, hut.getUnusedJunimoNumber());
        }

        public static void SpawnJunimoAtPosition(GameLocation location, Vector2 pos, JunimoHut hut, int junimoNumber) {
            if (hut == null) {
                return;
            }

            /*
             * Added by Mizzion. This will set the color of the junimos based on what gem is inside the hut.
             */
            var isPrismatic = false;
            var gemColor = GetGemColor(ref isPrismatic, hut);
            /*
             * End added By Mizzion
             */

            var junimoHarvester = new JunimoHarvester(location, pos, hut, junimoNumber, gemColor);

            // the JunimoHarvester constructor sets the location to Farm and calls pathfindToRandomSpotAroundHut immediately
            // so we have to set the location explicitly then re-do pathfinding
            if (!location.Equals(Game1.getFarm())) {
                Reflection.GetField<bool>(junimoHarvester, "destroy").SetValue(false);
                junimoHarvester.currentLocation = location;
                junimoHarvester.Position = pos;
                junimoHarvester.pathfindToRandomSpotAroundHut();
            }

            junimoHarvester.isPrismatic.Value = isPrismatic;
            location.characters.Add(junimoHarvester);
            hut.myJunimos.Add(junimoHarvester);
            junimoHarvester.HomeId = Util.GetHutIdFromHut(hut);

            if (Game1.isRaining) {
                var alpha = Reflection.GetField<float>(junimoHarvester, "alpha");
                alpha.SetValue(BetterJunimos.Config.FunChanges.RainyJunimoSpiritFactor);
            }

            if (!Utility.isOnScreen(Utility.Vector2ToPoint(pos), 64, location)) return;
            location.playSound("junimoMeep1");
        }

/*
 * Added by Mizzion. This method is used to get the gem color, so the junimos can be colored
 * I ripped this from SDV and edited it to work with this mod.
 */
        private static Color? GetGemColor(ref bool isPrismatic, JunimoHut hut) {
            var colorList = new List<Color>();
            var chest = hut.GetOutputChest();
            foreach (Item dyeObject in chest.Items) {
                if (dyeObject != null && (dyeObject.Category == MineralCategory || dyeObject.Category == GemCategory)) {
                    Color? dyeColor = TailoringMenu.GetDyeColor(dyeObject);
                    if (dyeObject.Name == "Prismatic Shard") isPrismatic = true;
                    if (dyeColor.HasValue) colorList.Add(dyeColor.Value);
                }
            }

            if (colorList.Count > 0) return colorList[Game1.random.Next(colorList.Count)];
            return new Color?();
        }

        public static void SendMessage(string msg) {
            if (!BetterJunimos.Config.Other.ReceiveMessages) return;

            Game1.addHUDMessage(new HUDMessage(msg, 3) {
                noIcon = true,
                timeLeft = HUDMessage.defaultTime
            });
        }

        public static void SpawnParticles(Vector2 pos) {
            Game1.Multiplayer.broadcastSprites(Game1.currentLocation,
                new TemporaryAnimatedSprite(17, new Vector2(pos.X * 64f, pos.Y * 64f), Color.White, 7, Game1.random.NextDouble() < 0.5, 125f));
            Game1.Multiplayer.broadcastSprites(Game1.currentLocation,
                new TemporaryAnimatedSprite(14, new Vector2(pos.X * 64f, pos.Y * 64f), Color.White, 7, Game1.random.NextDouble() < 0.5, 50f));
        }

        internal static int ExperienceForCrop(Crop crop) {
            if (crop == null || crop.forageCrop.Value) {
                return 3;
            }

            string ioh = crop.indexOfHarvest.Value?.ToString();
            if (ioh != null && Game1.objectData.TryGetValue(ioh, out var oi)) {
                double num = Math.Round(16.0 * Math.Log(0.018 * (double)oi.Price + 1.0));
                return Convert.ToInt32(num);
            }

            return 0;
        }
    }
}