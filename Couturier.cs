using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Cloners;
using System.Reflection;
using Path = System.IO.Path;

namespace Couturier;

public record ModMetadata : AbstractModMetadata {
    public override string ModGuid { get; init; } = "com.turbodestroyer.couturier";
    public override string Name { get; init; } = "Couturier";
    public override string Author { get; init; } = "turbodestroyer";
    public override List<string>? Contributors { get; init; }
    public override SemanticVersioning.Version Version { get; init; } = new("2.0.2");
    public override SemanticVersioning.Range SptVersion { get; init; } = new("~4.0.3");
    public override List<string>? Incompatibilities { get; init; }
    public override Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public override string? Url { get; init; }
    public override bool? IsBundleMod { get; init; } = true;
    public override string? License { get; init; } = "MIT";
}

[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 1)]
public class Couturier(ModHelper modHelper, ISptLogger<Couturier> logger, ImageRouter imageRouter, ConfigServer configServer, TimeUtil timeUtil, ICloner cloner, DatabaseService databaseService, LocaleService localeService) : IOnLoad {
    private readonly TraderConfig _traderConfig = configServer.GetConfig<TraderConfig>();
    private readonly RagfairConfig _ragfairConfig = configServer.GetConfig<RagfairConfig>();
    private readonly WeatherConfig _weatherConfig = configServer.GetConfig<WeatherConfig>();
    private ModConfig config;
    private TraderBase traderBase;
    private SPTarkov.Server.Core.Models.Spt.Server.DatabaseTables db;
    private Dictionary<string, Dictionary<string, LocaleContent>> locales = new Dictionary<string, Dictionary<string, LocaleContent>>();
    private Dictionary<string, SPTarkov.Server.Core.Utils.Json.LazyLoad<Dictionary<string, string>>> globalLocales;
    private int currentSeason = -1;

    public Task OnLoad() {
        var pathToMod = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var customItemData = modHelper.GetJsonDataFromFile<CustomItemData>(pathToMod, "db/items/itemData.json");
        traderBase = modHelper.GetJsonDataFromFile<TraderBase>(pathToMod, "db/base.json");
        config = modHelper.GetJsonDataFromFile<ModConfig>(pathToMod, "config.json");
        db = databaseService.GetTables();
        var fleaPresets = modHelper.GetJsonDataFromFile<Dictionary<MongoId, Preset>>(pathToMod, "db/items/fleaPresets.json");
        var fleaPrices = modHelper.GetJsonDataFromFile<Dictionary<MongoId, double>>(pathToMod, "db/items/prices.json");

        // scan folder with locales, add each one to 'locales' variable
        var localesPath = Path.Combine(pathToMod, "db/locales");
        var localeFiles = Directory.GetFiles(localesPath, "*.json");
        foreach (var localeFile in localeFiles) {
            var localeName = Path.GetFileNameWithoutExtension(localeFile);
            var localeData = modHelper.GetJsonDataFromFile<Dictionary<string, LocaleContent>>(localesPath, Path.GetFileName(localeFile));
            locales.Add(localeName, localeData);
        }
        globalLocales = databaseService.GetTables().Locales.Global;
        foreach (var (id, preset) in fleaPresets) {
            if (!db.Globals.ItemPresets.ContainsKey(id)) {
                db.Globals.ItemPresets.Add(id, preset);
            }
        }

        currentSeason = GetCurrentSeason();

        // if the trader is enabled, then init him and the clothing service
        if (config.traderEnabled) {
            var MY_ID = traderBase.Id;

            var traderImagePath = Path.Combine(pathToMod, "res/couturier.jpg");
            imageRouter.AddRoute(traderBase.Avatar.Replace(".jpg", ""), traderImagePath);
            SetTraderUpdateTime(_traderConfig, traderBase, timeUtil.GetHoursAsSeconds(1), timeUtil.GetHoursAsSeconds(2));
            _ragfairConfig.Traders.TryAdd((MongoId)MY_ID, true);
            AddTraderWithEmptyAssortToDb(traderBase);
            AddTraderToLocales(traderBase, "Couturier", "Only fine fashion here");
        }

        var suits = new List<Suit>();
        var clothingIds = new Dictionary<string, Dictionary<string, List<MongoId>>> {
            ["Bear"] = new Dictionary<string, List<MongoId>> {
                ["Tops"] = new List<MongoId>(),
                ["Bottoms"] = new List<MongoId>()
            },
            ["Usec"] = new Dictionary<string, List<MongoId>> {
                ["Tops"] = new List<MongoId>(),
                ["Bottoms"] = new List<MongoId>()
            }
        };
        var gearCount = 0;

        // gear
        if (customItemData.Gear != null) {
            foreach (var (key, gearItem) in customItemData.Gear) {
                if (!gearItem.enabled) continue;
                gearCount++;
                // add item to db
                AddGearItem(gearItem);
                // add locales
                foreach (var (localeKey, localeKvP) in globalLocales) {
                    localeKvP.AddTransformer(lazyloadedLocaleData => {
                        LocaleContent localLocaleObj;
                        if (locales.TryGetValue(localeKey, out var localeDict) && localeDict.TryGetValue(gearItem.name, out var foundLocale)) {
                            localLocaleObj = foundLocale;
                        }
                        else {
                            localLocaleObj = new LocaleContent
                            {
                                Name = gearItem.name ?? "Unknown",
                                ShortName = gearItem.name ?? "Unknown",
                                Description = "No description available."
                            };
                        }
                        lazyloadedLocaleData.Add($"{gearItem.id} Name", localLocaleObj.Name ?? gearItem.name ?? "Unknown");
                        lazyloadedLocaleData.Add($"{gearItem.id} ShortName", localLocaleObj.ShortName ?? gearItem.name ?? "Unknown");
                        lazyloadedLocaleData.Add($"{gearItem.id} Description", localLocaleObj.Description ?? "No description available.");
                        return lazyloadedLocaleData;
                    });
                }
                // add gear to bots
                if (config.botsUseGear && (gearItem.season == null || currentSeason == -1 || gearItem.season.Contains(currentSeason))) {
                    foreach (var (botTypeKey, botType) in db.Bots.Types) {
                        if ((!botTypeKey.Equals("usec") && !botTypeKey.Equals("bear")) || !config.pmcsUseFactionedGearOnly || (gearItem.side != null && botTypeKey.Equals(gearItem.side))) {
                            foreach (var inventoryT in new[] { EquipmentSlots.ArmorVest, EquipmentSlots.TacticalVest, EquipmentSlots.Backpack, EquipmentSlots.Headwear, EquipmentSlots.FaceCover })
                            {
                                if (botType.BotInventory.Equipment != null && botType.BotInventory.Equipment.TryGetValue(inventoryT, out var equipmentDict) && equipmentDict.ContainsKey(gearItem.BaseItemID))
                                {
                                    var chance = Math.Max(1, (int)Math.Round(equipmentDict[gearItem.BaseItemID]));
                                    equipmentDict[gearItem.id] = chance;
                                    if (botType.BotInventory.Mods != null && botType.BotInventory.Mods.TryGetValue(gearItem.BaseItemID, out var modChance))
                                    {
                                        botType.BotInventory.Mods[gearItem.id] = modChance;
                                    }
                                }
                            }
                        }
                    }
                }
                // set flea prices
                if (config.sellGearOnFlea) {
                    // check if gearItem.id is in fleaPrices
                    if (fleaPrices.TryGetValue(gearItem.id, out var price)) {
                        db.Templates.Prices[gearItem.id] = price;
                    }
                }
            }
        }
        else {
            logger.Warning("No gear items found in itemData.json");
        }

        // bottoms
        if (customItemData.Bottoms != null) {
            foreach (var (key, bottomItem) in customItemData.Bottoms) {
                if (bottomItem.enabled == true) {
                    var suit = AddBottomItem(bottomItem, config.traderEnabled);
                    suits.Add(suit);

                    if (bottomItem.season == null || currentSeason == -1 || bottomItem.season.Contains(currentSeason)) {
                        if (bottomItem.Side != null && bottomItem.Side.Any()) {
                            foreach (var side in bottomItem.Side)
                            {
                                clothingIds[side]["Bottoms"].Add(bottomItem.id);
                            }
                        }
                        else {
                            clothingIds["Bear"]["Bottoms"].Add(bottomItem.id);
                            clothingIds["Usec"]["Bottoms"].Add(bottomItem.id);
                        }
                    }
                }
            }
        }

        // tops
        if (customItemData?.Tops != null) {
            foreach (var (key, topItem) in customItemData.Tops) {
                if (topItem.enabled == true) {
                    var suit = AddTopItem(topItem, config.traderEnabled);
                    suits.Add(suit);

                    if (topItem.season == null || currentSeason == -1 || topItem.season.Contains(currentSeason)) {
                        if (topItem.Side != null && topItem.Side.Any()) {
                            foreach (var side in topItem.Side)
                            {
                                clothingIds[side]["Tops"].Add(topItem.id);
                            }
                        }
                        else {
                            clothingIds["Bear"]["Tops"].Add(topItem.id);
                            clothingIds["Usec"]["Tops"].Add(topItem.id);
                        }
                    }
                }
            }
        }

        if (config.unlockOthersFactionsClothing) {
            foreach (var (customizationKey, customizationValue) in db.Templates.Customization) {
                if (customizationValue.Properties?.Side != null &&
                    customizationValue.Properties.Side.Count == 1 &&
                    (customizationValue.Properties.Feet != null || customizationValue.Properties.Hands != null) &&
                    (customizationValue.Properties.Side[0] == "Usec" || customizationValue.Properties.Side[0] == "Bear")) {
                    customizationValue.Properties.Side = new List<string> { "Bear", "Usec" };
                }
            }
        }

        if (config.botsUseClothing && db.Bots.Types.ContainsKey("usec") && db.Bots.Types.ContainsKey("bear")) {
            var usecBot = db.Bots.Types["usec"];
            var bearBot = db.Bots.Types["bear"];

            var usecBottomsMaxCount = usecBot.BotAppearance.Feet.Count / 2;
            var bearBottomsMaxCount = bearBot.BotAppearance.Feet.Count / 2;
            var usecTopsMaxCount = usecBot.BotAppearance.Body.Count / 2;
            var bearTopsMaxCount = bearBot.BotAppearance.Body.Count / 2;

            var rfBottomsUsec = RandomSample(clothingIds["Usec"]["Bottoms"], usecBottomsMaxCount);
            var rfBottomsBear = RandomSample(clothingIds["Bear"]["Bottoms"], bearBottomsMaxCount);
            var rfTopsUsec = RandomSample(clothingIds["Usec"]["Tops"], usecTopsMaxCount);
            var rfTopsBear = RandomSample(clothingIds["Bear"]["Tops"], bearTopsMaxCount);

            foreach (var bId in rfBottomsUsec) {
                if (!usecBot.BotAppearance.Feet.ContainsKey(bId)) usecBot.BotAppearance.Feet.Add(bId, 1);
            }
            foreach (var bId in rfTopsUsec) {
                if (!usecBot.BotAppearance.Body.ContainsKey(bId)) usecBot.BotAppearance.Body.Add(bId, 1);
            }
            foreach (var bId in rfBottomsBear) {
                if (!bearBot.BotAppearance.Feet.ContainsKey(bId)) bearBot.BotAppearance.Feet.Add(bId, 1);
            }
            foreach (var bId in rfTopsBear) {
                if (!bearBot.BotAppearance.Body.ContainsKey(bId)) bearBot.BotAppearance.Body.Add(bId, 1);
            }
        }

        // add suits assort to the trader if it's enabled
        if (config.traderEnabled) {
            var assort = modHelper.GetJsonDataFromFile<TraderAssort>(pathToMod, "db/assort.json");
            OverwriteTraderAssort(traderBase.Id, assort);
            var traderToEdit = db.Traders[traderBase.Id];
            if (traderToEdit != null) {
                if (traderToEdit.Suits == null) {
                    traderToEdit.Suits = new List<Suit>();
                }
                foreach (var suit in suits) {
                    traderToEdit.Suits.Add(suit);
                }
            }
        }
        else {
            var ragmanId = (MongoId)"5ac3b934156ae10c4430e83c";
            if (db.Traders.ContainsKey(ragmanId)) {
                var ragman = db.Traders[ragmanId];
                if (ragman.Suits == null) {
                    ragman.Suits = new List<Suit>();
                }
                foreach (var suit in suits) {
                    suit.Tid = ragmanId;
                    ragman.Suits.Add(suit);
                }
                logger.Info($"[Couturier] Added {suits.Count} suits to Ragman");
            }
        }

        logger.Info($"[Couturier] Clothing items added: {suits.Count}");
        logger.Info($"[Couturier] Gear items added: {gearCount}");

        return Task.CompletedTask;
    }

    public void SetTraderUpdateTime(TraderConfig traderConfig, TraderBase baseJson, int refreshTimeSecondsMin, int refreshTimeSecondsMax) {
        // Add refresh time in seconds to config
        var traderRefreshRecord = new UpdateTime { TraderId = baseJson.Id, Seconds = new MinMax<int>(refreshTimeSecondsMin, refreshTimeSecondsMax) };
        traderConfig.UpdateTime.Add(traderRefreshRecord);
    }

    public void AddTraderWithEmptyAssortToDb(TraderBase traderDetailsToAdd) {
        var emptyTraderItemAssortObject = new TraderAssort {
            Items = [],
            BarterScheme = new Dictionary<MongoId, List<List<BarterScheme>>>(),
            LoyalLevelItems = new Dictionary<MongoId, int>()
        };

        // Create trader data ready to add to database
        var traderDataToAdd = new Trader {
            Assort = emptyTraderItemAssortObject,
            Base = cloner.Clone(traderDetailsToAdd),
            QuestAssort = new() {
                    { "Started", new() },
                    { "Success", new() },
                    { "Fail", new() }
                },
            Dialogue = []
        };

        if (!databaseService.GetTables().Traders.TryAdd(traderDetailsToAdd.Id, traderDataToAdd)) {
        }
    }

    public void AddTraderToLocales(TraderBase baseJson, string firstName, string description) {
        // For each language, add locale for the new trader
        var locales = databaseService.GetTables().Locales.Global;
        var newTraderId = baseJson.Id;
        var fullName = baseJson.Name;
        var nickName = baseJson.Nickname;
        var location = baseJson.Location;

        foreach (var (localeKey, localeKvP) in locales) {
            // We have to add a transformer here, because locales are lazy loaded due to them taking up huge space in memory
            // The transformer will make sure that each time the locales are requested, the ones added below are included
            localeKvP.AddTransformer(lazyloadedLocaleData => {
                lazyloadedLocaleData.Add($"{newTraderId} FullName", fullName);
                lazyloadedLocaleData.Add($"{newTraderId} FirstName", firstName);
                lazyloadedLocaleData.Add($"{newTraderId} Nickname", nickName);
                lazyloadedLocaleData.Add($"{newTraderId} Location", location);
                lazyloadedLocaleData.Add($"{newTraderId} Description", description);
                return lazyloadedLocaleData;
            });
        }
    }

    public void OverwriteTraderAssort(string traderId, TraderAssort newAssorts) {
        if (!databaseService.GetTables().Traders.TryGetValue(traderId, out var traderToEdit)) {
            logger.Warning($"Unable to update assorts for trader: {traderId}, they couldn't be found on the server");

            return;
        }
        traderToEdit.Assort = newAssorts;
    }

    private void AddGearItem(GearItem gearItem) {
        // take BaseItemID, copy item from db with this id, change its data and add it to database
        var dbItems = db.Templates.Items;
        var baseItemID = gearItem.BaseItemID;
        var item = cloner.Clone(dbItems[baseItemID]);
        item.Id = gearItem.id;
        item.Name = gearItem.name;
        if (config.allGearAvailableOnFlea && config.sellGearOnFlea) {
            item.Properties.CanSellOnRagfair = true;
            item.Properties.CanRequireOnRagfair = true;
        }
        else if (!config.sellGearOnFlea) {
            item.Properties.CanSellOnRagfair = false;
            item.Properties.CanRequireOnRagfair = false;
        }
        item.Properties.Prefab.Path = gearItem.BundlePath;
        dbItems[gearItem.id] = item;
        if (gearItem.needsFilterUpdate == true) UpdateFilters(gearItem.id, baseItemID);
        // handbook, flea, filters
        var handbookItem = cloner.Clone(db.Templates.Handbook.Items.Find(i => i.Id == baseItemID));
        handbookItem.Id = gearItem.id;
        db.Templates.Handbook.Items.Add(handbookItem);
    }

    private Suit AddBottomItem(ClothingItem clothingItem, bool trader_enabled) {
        var trader_id = trader_enabled ? (MongoId)traderBase.Id : (MongoId)"5ac3b934156ae10c4430e83c";
        // add bottom
        var newBottom = cloner.Clone(db.Templates.Customization["5cc085bb14c02e000e67a5c5"]);
        newBottom.Id = clothingItem.id;
        newBottom.Name = clothingItem.name;
        newBottom.Properties.Prefab = new Dictionary<string, string> { { "Path", clothingItem.BundlePath }, { "rcid", "" } };
        db.Templates.Customization.Add(clothingItem.id, newBottom);
        // add suite
        var newSuite = cloner.Clone(db.Templates.Customization["5cd946231388ce000d572fe3"]);
        newSuite.Id = clothingItem.SuiteId;
        newSuite.Name = $"{clothingItem.name}_suite".ToLower();
        newSuite.Properties.Feet = clothingItem.id;
        if (clothingItem.Side != null && clothingItem.Side.Any()) newSuite.Properties.Side = clothingItem.Side; else newSuite.Properties.Side = new List<string> { "Usec", "Bear" };
        db.Templates.Customization.Add(clothingItem.SuiteId, newSuite);
        // add suite to the trader
        var newSuitTraderObject = new Suit();
        newSuitTraderObject.Id = clothingItem.id;
        newSuitTraderObject.Tid = trader_id;
        newSuitTraderObject.SuiteId = clothingItem.SuiteId;
        newSuitTraderObject.IsActive = true;
        newSuitTraderObject.IsHiddenInPVE = false;
        newSuitTraderObject.ExternalObtain = false;
        newSuitTraderObject.InternalObtain = true;
        newSuitTraderObject.Requirements = new() {
            LoyaltyLevel = clothingItem.LoyaltyLevel,
            ProfileLevel = clothingItem.ProfileLevel,
            Standing = clothingItem.Standing,
            SkillRequirements = [],
            QuestRequirements = [],
            AchievementRequirements = [],
            ItemRequirements = new List<ItemRequirement> {
                new ItemRequirement {
                    Count = clothingItem.Price,
                    Tpl = (MongoId)"5449016a4bdc2d6f028b456f",
                    OnlyFunctional = true
                }
            },
            RequiredTid = trader_id
        };
        // locales
        foreach (var (localeKey, localeKvP) in globalLocales) {
            localeKvP.AddTransformer(lazyloadedLocaleData => {
                lazyloadedLocaleData.Add($"{clothingItem.SuiteId} Name", clothingItem.displayName ?? "Unknown");
                lazyloadedLocaleData.Add($"{clothingItem.SuiteId} ShortName", "Couturier's bottom");
                lazyloadedLocaleData.Add($"{clothingItem.SuiteId} Description", "Couturier's bottom");
                return lazyloadedLocaleData;
            });
        }
        return newSuitTraderObject;
    }

    private Suit AddTopItem(ClothingItemTop clothingItem, bool trader_enabled) {
        var trader_id = trader_enabled ? traderBase.Id : (MongoId)"5ac3b934156ae10c4430e83c";
        // add top
        var newTop = cloner.Clone(db.Templates.Customization["5d28adcb86f77429242fc893"]);
        newTop.Id = clothingItem.id;
        newTop.Name = clothingItem.name;
        newTop.Properties.Prefab = clothingItem.BundlePath;
        newTop.Properties.Prefab = new Dictionary<string, string> { { "Path", clothingItem.BundlePath }, { "rcid", "" } };
        db.Templates.Customization.Add(clothingItem.id, newTop);
        // add hands
        if (clothingItem.BundlePath?.Length > 0 && clothingItem.HandsId != clothingItem.HandsBaseID) {
            var newHands = cloner.Clone(db.Templates.Customization[clothingItem.HandsBaseID]);
            newHands.Id = clothingItem.HandsId;
            newHands.Name = clothingItem.HandsId.ToString();
            newHands.Properties.Prefab = new Dictionary<string, string> { { "Path", clothingItem.HandsBundlePath }, { "rcid", "" } };
            // check if clothingItem.HandsId already exists in db, if not, add it
            if (!db.Templates.Customization.ContainsKey(clothingItem.HandsId)) {
                db.Templates.Customization.Add(clothingItem.HandsId, newHands);
            }

        }
        // add suite
        var newSuite = cloner.Clone(db.Templates.Customization["5d1f623e86f7744bce0ef705"]);
        newSuite.Id = clothingItem.SuiteId;
        newSuite.Name = $"{clothingItem.name}_suite".ToLower();
        newSuite.Properties.Body = clothingItem.id;

        if (clothingItem.HandsBundlePath != null) newSuite.Properties.Hands = clothingItem.HandsId;
        else newSuite.Properties.Hands = clothingItem.HandsBaseID;

        if (clothingItem.Side != null && clothingItem.Side.Any()) newSuite.Properties.Side = clothingItem.Side; else newSuite.Properties.Side = new List<string> { "Usec", "Bear" };
        db.Templates.Customization.Add(clothingItem.SuiteId, newSuite);

        // trader obj
        var newSuitTraderObject2 = cloner.Clone(db.Traders["5ac3b934156ae10c4430e83c"].Suits.Find(s => s.Id == (MongoId)"5d1f65e586f7744bce0ef714"));
        newSuitTraderObject2.Id = clothingItem.id;
        newSuitTraderObject2.ExtensionData["tid"] = trader_id;
        newSuitTraderObject2.SuiteId = clothingItem.SuiteId;
        newSuitTraderObject2.IsActive = true;
        newSuitTraderObject2.IsHiddenInPVE = false;
        newSuitTraderObject2.ExternalObtain = false;
        newSuitTraderObject2.InternalObtain = true;
        newSuitTraderObject2.Requirements = new() {
            LoyaltyLevel = clothingItem.LoyaltyLevel,
            ProfileLevel = clothingItem.ProfileLevel,
            Standing = clothingItem.Standing,
            SkillRequirements = [],
            QuestRequirements = [],
            AchievementRequirements = [],
            ItemRequirements = new List<ItemRequirement> {
                new ItemRequirement {
                    Count = clothingItem.Price,
                    Tpl = (MongoId)"5449016a4bdc2d6f028b456f",
                    OnlyFunctional = true
                }
            },
            RequiredTid = trader_id
        };

        // locales
        foreach (var (localeKey, localeKvP) in globalLocales) {
            localeKvP.AddTransformer(lazyloadedLocaleData => {
                lazyloadedLocaleData.Add($"{clothingItem.SuiteId} Name", clothingItem.displayName ?? "Unknown");
                lazyloadedLocaleData.Add($"{clothingItem.SuiteId} ShortName", "Couturier's top");
                lazyloadedLocaleData.Add($"{clothingItem.SuiteId} Description", "Couturier's top");
                return lazyloadedLocaleData;
            });
        }

        return newSuitTraderObject2;
    }

    private void UpdateFilters(MongoId itemId, MongoId baseItemID) {
        foreach (var item in db.Templates.Items.Values) {
            if (item.Properties == null) continue;
            var itemConflictId = item.Properties.ConflictingItems;
            if (itemConflictId != null) {
                if (itemConflictId.Contains(baseItemID)) {
                    itemConflictId.Add(itemId);
                }
            }
            if (item.Properties.Slots == null) continue;
            foreach (var slot in item.Properties.Slots) {
                if (slot.Properties == null) continue;
                var filters = slot.Properties.Filters;
                if (filters != null && filters.Any()) {
                    var slotsId = filters.First().Filter;
                    if (slotsId != null) {
                        if (slotsId.Contains(baseItemID)) {
                            slotsId.Add(itemId);
                        }
                    }
                }
            }
        }
    }

    private List<T> RandomSample<T>(List<T> arr, int N) {
        if (N <= 0) return new List<T>();
        if (arr.Count <= N) return new List<T>(arr);

        var copy = new List<T>(arr);
        var random = new Random();

        for (int i = 0; i < N; i++) {
            int j = i + random.Next(copy.Count - i);
            (copy[i], copy[j]) = (copy[j], copy[i]);
        }

        return copy.Take(N).ToList();
    }

    private int GetCurrentSeason(DateTime? date = null) {
        var currentDate = date ?? DateTime.Now;

        if (_weatherConfig.OverrideSeason.HasValue) {
            return (int)_weatherConfig.OverrideSeason.Value;
        }

        var seasonDates = _weatherConfig.SeasonDates;
        if (seasonDates == null || !seasonDates.Any()) {
            return -1;
        }

        var md = currentDate.Month * 100 + currentDate.Day;

        foreach (var s in seasonDates) {
            var start = s.StartMonth.GetValueOrDefault() * 100 + s.StartDay.GetValueOrDefault();
            var end = s.EndMonth.GetValueOrDefault() * 100 + s.EndDay.GetValueOrDefault();
            var wraps = end < start;
            var inRange = wraps ? (md >= start || md <= end) : (md >= start && md <= end);

            if (inRange) {
                return (int)s.SeasonType.GetValueOrDefault();
            }
        }

        return -1;
    }
}

public class ModConfig {
    public bool sellGearOnFlea { get; set; }
    public bool allGearAvailableOnFlea { get; set; }
    public bool traderEnabled { get; set; }
    public bool botsUseGear { get; set; }
    public bool pmcsUseFactionedGearOnly { get; set; }
    public bool botsUseClothing { get; set; }
    public bool unlockOthersFactionsClothing { get; set; }
}

public class GearItem {
    public MongoId BaseItemID { get; set; }
    public string BundlePath { get; set; }
    public MongoId id { get; set; }
    public string name { get; set; }
    public bool enabled { get; set; }
    public string? side { get; set; }
    public List<int>? season { get; set; }
    public bool? needsFilterUpdate { get; set; }
}

public class ClothingItem {
    public string name { get; set; }
    public string displayName { get; set; }
    public MongoId id { get; set; }
    public MongoId SuiteId { get; set; }
    public string BundlePath { get; set; }
    public int LoyaltyLevel { get; set; }
    public int ProfileLevel { get; set; }
    public float Standing { get; set; }
    public int Price { get; set; }
    public bool? enabled { get; set; }
    public List<int>? season { get; set; }
    public List<string>? Side { get; set; }
}

// inherit from ClothingItem
public class ClothingItemTop : ClothingItem {
    public MongoId HandsId { get; set; }
    public MongoId HandsBaseID { get; set; }
    public string? HandsBundlePath { get; set; }
}

public class CustomItemData {
    public Dictionary<string, GearItem> Gear { get; set; }
    public Dictionary<string, ClothingItemTop> Tops { get; set; }
    public Dictionary<string, ClothingItem> Bottoms { get; set; }
}

public class LocaleContent {
    public string Name { get; set; }
    public string ShortName { get; set; }
    public string Description { get; set; }
}