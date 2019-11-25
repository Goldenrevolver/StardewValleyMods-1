﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JsonAssets.Data;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Locations;
using StardewValley.TerrainFeatures;
using StardewValley.Objects;
using System.Reflection;
using Netcode;
using StardewValley.Buildings;
using Harmony;
using System.Text.RegularExpressions;
using JsonAssets.Overrides;
using Newtonsoft.Json;
using StardewValley.Tools;
using JsonAssets.Other.ContentPatcher;
using StardewValley.Network;
using SpaceShared;

// TODO: Refactor recipes

namespace JsonAssets
{
    public class Mod : StardewModdingAPI.Mod
    {
        public static Mod instance;
        private HarmonyInstance harmony;

        /// <summary>The mod entry point, called after the mod is first loaded.</summary>
        /// <param name="helper">Provides simplified APIs for writing mods.</param>
        public override void Entry(IModHelper helper)
        {
            instance = this;
            Log.Monitor = Monitor;

            helper.Events.Display.MenuChanged += onMenuChanged;
            helper.Events.GameLoop.Saved += onSaved;
            helper.Events.Player.InventoryChanged += onInventoryChanged;
            helper.Events.GameLoop.GameLaunched += onGameLaunched;
            helper.Events.GameLoop.SaveCreated += onCreated;
            helper.Events.Specialized.LoadStageChanged += onLoadStageChanged;
            helper.Events.Multiplayer.PeerContextReceived += clientConnected;

            SpaceCore.TileSheetExtensions.RegisterExtendedTileSheet("TileSheets\\crops", 32);
            SpaceCore.TileSheetExtensions.RegisterExtendedTileSheet("TileSheets\\fruitTrees", 80);

            Log.info("Loading content packs...");
            foreach (IContentPack contentPack in this.Helper.ContentPacks.GetOwned())
                loadData(contentPack);
            if (Directory.Exists(Path.Combine(Helper.DirectoryPath, "ContentPacks")))
            {
                foreach (string dir in Directory.EnumerateDirectories(Path.Combine(Helper.DirectoryPath, "ContentPacks")))
                    loadData(dir);
            }

            resetAtTitle();

            try
            {
                harmony = HarmonyInstance.Create("spacechase0.JsonAssets");
                doPrefix(typeof(StardewValley.Object), "canBePlacedHere", typeof(ObjectCanPlantHereOverride));
                doPrefix(typeof(StardewValley.Object), "checkForAction", typeof(ObjectNoActionHook));
                doPrefix(typeof(StardewValley.Object), "loadDisplayName", typeof(ObjectDisplayNameHook));
                doPostfix(typeof(StardewValley.Object), "isIndexOkForBasicShippedCategory", typeof(ObjectCollectionShippingHook));
                doPrefix(typeof(StardewValley.Objects.Ring), "loadDisplayFields", typeof(RingLoadDisplayFieldsHook));
                doPrefix(typeof(StardewValley.Crop), nameof(Crop.isPaddyCrop), typeof(PaddyCropHook));
                doTranspiler(typeof(StardewValley.Crop), nameof(Crop.newDay), typeof(IndoorOnlyCropHook));
            }
            catch (Exception e)
            {
                Log.error($"Exception doing harmony stuff: {e}");
            }
        }

        private void doPrefix(Type origType, string origMethod, Type newType)
        {
            doPrefix(origType.GetMethod(origMethod, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static), newType.GetMethod("Prefix"));
        }
        private void doPrefix(MethodInfo orig, MethodInfo prefix)
        {
            try
            {
                Log.trace($"Doing prefix patch {orig}:{prefix}...");
                harmony.Patch(orig, new HarmonyMethod(prefix));
            }
            catch (Exception e)
            {
                Log.error($"Exception doing prefix patch {orig}:{prefix}: {e}");
            }
        }
        private void doPostfix(Type origType, string origMethod, Type newType)
        {
            doPostfix(origType.GetMethod(origMethod, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static), newType.GetMethod("Postfix"));
        }
        private void doPostfix(MethodInfo orig, MethodInfo postfix)
        {
            try
            {
                Log.trace($"Doing postfix patch {orig}:{postfix}...");
                harmony.Patch(orig, null, new HarmonyMethod(postfix));
            }
            catch (Exception e)
            {
                Log.error($"Exception doing postfix patch {orig}:{postfix}: {e}");
            }
        }
        private void doTranspiler(Type origType, string origMethod, Type newType)
        {
            doTranspiler(origType.GetMethod(origMethod, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static), newType.GetMethod("Transpiler"));
        }
        private void doTranspiler(MethodInfo orig, MethodInfo transpiler)
        {
            try
            {
                Log.trace($"Doing transpiler patch {orig}:{transpiler}...");
                harmony.Patch(orig, null, null, new HarmonyMethod(transpiler));
            }
            catch (Exception e)
            {
                Log.error($"Exception doing transpiler patch {orig}:{transpiler}: {e}");
            }
        }

        private Api api;
        public override object GetApi()
        {
            return api ?? (api = new Api(this.loadData));
        }

        private void onGameLaunched(object sender, GameLaunchedEventArgs e)
        {
            ContentPatcherIntegration.Initialize();
        }

        private void loadData(string dir)
        {
            // read initial info
            IContentPack temp = this.Helper.ContentPacks.CreateFake(dir);
            ContentPackData info = temp.ReadJsonFile<ContentPackData>("content-pack.json");
            if (info == null)
            {
                Log.warn($"\tNo {dir}/content-pack.json!");
                return;
            }

            // load content pack
            IContentPack contentPack = this.Helper.ContentPacks.CreateTemporary(dir, id: Guid.NewGuid().ToString("N"), name: info.Name, description: info.Description, author: info.Author, version: new SemanticVersion(info.Version));
            this.loadData(contentPack);
        }

        private Dictionary<string, IContentPack> dupObjects = new Dictionary<string, IContentPack>();
        private Dictionary<string, IContentPack> dupCrops = new Dictionary<string, IContentPack>();
        private Dictionary<string, IContentPack> dupFruitTrees = new Dictionary<string, IContentPack>();
        private Dictionary<string, IContentPack> dupBigCraftables = new Dictionary<string, IContentPack>();
        private Dictionary<string, IContentPack> dupHats = new Dictionary<string, IContentPack>();
        private Dictionary<string, IContentPack> dupWeapons = new Dictionary<string, IContentPack>();
        private Dictionary<string, IContentPack> dupShirts = new Dictionary<string, IContentPack>();
        private Dictionary<string, IContentPack> dupPants = new Dictionary<string, IContentPack>();

        private readonly Regex SeasonLimiter = new Regex("(z(?: spring| summer| fall| winter){2,4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private void loadData(IContentPack contentPack)
        {
            Log.info($"\t{contentPack.Manifest.Name} {contentPack.Manifest.Version} by {contentPack.Manifest.Author} - {contentPack.Manifest.Description}");

            // load objects
            DirectoryInfo objectsDir = new DirectoryInfo(Path.Combine(contentPack.DirectoryPath, "Objects"));
            if (objectsDir.Exists)
            {
                foreach (DirectoryInfo dir in objectsDir.EnumerateDirectories())
                {
                    string relativePath = $"Objects/{dir.Name}";

                    // load data
                    ObjectData obj = contentPack.ReadJsonFile<ObjectData>($"{relativePath}/object.json");
                    if (obj == null || (obj.DisableWithMod != null && Helper.ModRegistry.IsLoaded(obj.DisableWithMod)))
                        continue;

                    // save object
                    obj.texture = contentPack.LoadAsset<Texture2D>($"{relativePath}/object.png");
                    if (obj.IsColored)
                        obj.textureColor = contentPack.LoadAsset<Texture2D>($"{relativePath}/color.png");
                    this.objects.Add(obj);

                    // save ring
                    if (obj.Category == ObjectData.Category_.Ring)
                        this.myRings.Add(obj);

                    // Duplicate check
                    if (dupObjects.ContainsKey(obj.Name))
                        Log.error($"Duplicate object: {obj.Name} just added by {contentPack.Manifest.Name}, already added by {dupObjects[obj.Name].Manifest.Name}!");
                    else
                        dupObjects[obj.Name] = contentPack;
                }
            }

            // load crops
            DirectoryInfo cropsDir = new DirectoryInfo(Path.Combine(contentPack.DirectoryPath, "Crops"));
            if (cropsDir.Exists)
            {
                foreach (DirectoryInfo dir in cropsDir.EnumerateDirectories())
                {
                    string relativePath = $"Crops/{dir.Name}";

                    // load data
                    CropData crop = contentPack.ReadJsonFile<CropData>($"{relativePath}/crop.json");
                    if (crop == null || (crop.DisableWithMod != null && Helper.ModRegistry.IsLoaded(crop.DisableWithMod)))
                        continue;

                    // save crop
                    crop.texture = contentPack.LoadAsset<Texture2D>($"{relativePath}/crop.png");
                    crops.Add(crop);

                    // save seeds
                    crop.seed = new ObjectData
                    {
                        texture = contentPack.LoadAsset<Texture2D>($"{relativePath}/seeds.png"),
                        Name = crop.SeedName,
                        Description = crop.SeedDescription,
                        Category = ObjectData.Category_.Seeds,
                        Price = crop.SeedSellPrice == -1 ? crop.SeedPurchasePrice : crop.SeedSellPrice,
                        CanPurchase = true,
                        PurchaseFrom = crop.SeedPurchaseFrom,
                        PurchasePrice = crop.SeedPurchasePrice,
                        PurchaseRequirements = crop.SeedPurchaseRequirements ?? new List<string>(),
                        NameLocalization = crop.SeedNameLocalization,
                        DescriptionLocalization = crop.SeedDescriptionLocalization
                    };

                    // TODO: Clean up this chunk
                    // I copy/pasted it from the unofficial update decompiled
                    string str = "";
                    string[] array =  new[] { "spring", "summer", "fall", "winter" }
                        .Except(crop.Seasons)
                        .ToArray();
                    foreach (var season in array)
                    {
                        str += $"/z {season}";
                    }
                    if (str != "")
                    {
                        string strtrimstart = str.TrimStart(new char[] { '/' });
                        if (crop.SeedPurchaseRequirements != null && crop.SeedPurchaseRequirements.Count > 0)
                        {
                            for (int index = 0; index < crop.SeedPurchaseRequirements.Count; index++)
                            {
                                if (SeasonLimiter.IsMatch(crop.SeedPurchaseRequirements[index]))
                                {
                                    crop.SeedPurchaseRequirements[index] = strtrimstart;
                                    Log.warn($"        Faulty season requirements for {crop.SeedName}!\n        Fixed season requirements: {crop.SeedPurchaseRequirements[index]}");
                                }
                            }
                            if (!crop.SeedPurchaseRequirements.Contains(str.TrimStart('/')))
                            {
                                Log.trace($"        Adding season requirements for {crop.SeedName}:\n        New season requirements: {strtrimstart}");
                                crop.seed.PurchaseRequirements.Add(strtrimstart);
                            }
                        }
                        else
                        {
                            Log.trace($"        Adding season requirements for {crop.SeedName}:\n        New season requirements: {strtrimstart}");
                            crop.seed.PurchaseRequirements.Add(strtrimstart);
                        }
                    }

                    objects.Add(crop.seed);

                    // Duplicate check
                    if (dupCrops.ContainsKey(crop.Name))
                        Log.error($"Duplicate crop: {crop.Name} just added by {contentPack.Manifest.Name}, already added by {dupCrops[crop.Name].Manifest.Name}!");
                    else
                        dupCrops[crop.Name] = contentPack;
                }
            }

            // load fruit trees
            DirectoryInfo fruitTreesDir = new DirectoryInfo(Path.Combine(contentPack.DirectoryPath, "FruitTrees"));
            if (fruitTreesDir.Exists)
            {
                foreach (DirectoryInfo dir in fruitTreesDir.EnumerateDirectories())
                {
                    string relativePath = $"FruitTrees/{dir.Name}";

                    // load data
                    FruitTreeData tree = contentPack.ReadJsonFile<FruitTreeData>($"{relativePath}/tree.json");
                    if (tree == null || (tree.DisableWithMod != null && Helper.ModRegistry.IsLoaded(tree.DisableWithMod)))
                        continue;

                    // save fruit tree
                    tree.texture = contentPack.LoadAsset<Texture2D>($"{relativePath}/tree.png");
                    fruitTrees.Add(tree);

                    // save seed
                    tree.sapling = new ObjectData
                    {
                        texture = contentPack.LoadAsset<Texture2D>($"{relativePath}/sapling.png"),
                        Name = tree.SaplingName,
                        Description = tree.SaplingDescription,
                        Category = ObjectData.Category_.Seeds,
                        Price = tree.SaplingPurchasePrice,
                        CanPurchase = true,
                        PurchaseRequirements = tree.SaplingPurchaseRequirements,
                        PurchaseFrom = tree.SaplingPurchaseFrom,
                        PurchasePrice = tree.SaplingPurchasePrice,
                        NameLocalization = tree.SaplingNameLocalization,
                        DescriptionLocalization = tree.SaplingDescriptionLocalization
                    };
                    objects.Add(tree.sapling);

                    // Duplicate check
                    if (dupFruitTrees.ContainsKey(tree.Name))
                        Log.error($"Duplicate fruit tree: {tree.Name} just added by {contentPack.Manifest.Name}, already added by {dupFruitTrees[tree.Name].Manifest.Name}!");
                    else
                        dupFruitTrees[tree.Name] = contentPack;
                }
            }

            // load big craftables
            DirectoryInfo bigCraftablesDir = new DirectoryInfo(Path.Combine(contentPack.DirectoryPath, "BigCraftables"));
            if (bigCraftablesDir.Exists)
            {
                foreach (DirectoryInfo dir in bigCraftablesDir.EnumerateDirectories())
                {
                    string relativePath = $"BigCraftables/{dir.Name}";

                    // load data
                    BigCraftableData craftable = contentPack.ReadJsonFile<BigCraftableData>($"{relativePath}/big-craftable.json");
                    if (craftable == null || (craftable.DisableWithMod != null && Helper.ModRegistry.IsLoaded(craftable.DisableWithMod)))
                        continue;

                    // save craftable
                    craftable.texture = contentPack.LoadAsset<Texture2D>($"{relativePath}/big-craftable.png");
                    bigCraftables.Add(craftable);

                    // Duplicate check
                    if (dupBigCraftables.ContainsKey(craftable.Name))
                        Log.error($"Duplicate big craftable: {craftable.Name} just added by {contentPack.Manifest.Name}, already added by {dupBigCraftables[craftable.Name].Manifest.Name}!");
                    else
                        dupBigCraftables[craftable.Name] = contentPack;
                }
            }

            // load hats
            DirectoryInfo hatsDir = new DirectoryInfo(Path.Combine(contentPack.DirectoryPath, "Hats"));
            if (hatsDir.Exists)
            {
                foreach (DirectoryInfo dir in hatsDir.EnumerateDirectories())
                {
                    string relativePath = $"Hats/{dir.Name}";

                    // load data
                    HatData hat = contentPack.ReadJsonFile<HatData>($"{relativePath}/hat.json");
                    if (hat == null || (hat.DisableWithMod != null && Helper.ModRegistry.IsLoaded(hat.DisableWithMod)))
                        continue;

                    // save object
                    hat.texture = contentPack.LoadAsset<Texture2D>($"{relativePath}/hat.png");
                    hats.Add(hat);

                    // Duplicate check
                    if (dupHats.ContainsKey(hat.Name))
                        Log.error($"Duplicate hat: {hat.Name} just added by {contentPack.Manifest.Name}, already added by {dupHats[hat.Name].Manifest.Name}!");
                    else
                        dupHats[hat.Name] = contentPack;
                }
            }

            // Load weapons
            DirectoryInfo weaponsDir = new DirectoryInfo(Path.Combine(contentPack.DirectoryPath, "Weapons"));
            if (weaponsDir.Exists)
            {
                foreach (DirectoryInfo dir in weaponsDir.EnumerateDirectories())
                {
                    string relativePath = $"Weapons/{dir.Name}";

                    // load data
                    WeaponData weapon = contentPack.ReadJsonFile<WeaponData>($"{relativePath}/weapon.json");
                    if (weapon == null || (weapon.DisableWithMod != null && Helper.ModRegistry.IsLoaded(weapon.DisableWithMod)))
                        continue;

                    // save object
                    weapon.texture = contentPack.LoadAsset<Texture2D>($"{relativePath}/weapon.png");
                    weapons.Add(weapon);

                    // Duplicate check
                    if (dupWeapons.ContainsKey(weapon.Name))
                        Log.error($"Duplicate weapon: {weapon.Name} just added by {contentPack.Manifest.Name}, already added by {dupWeapons[weapon.Name].Manifest.Name}!");
                    else
                        dupWeapons[weapon.Name] = contentPack;
                }
            }

            // Load shirts
            DirectoryInfo shirtsDir = new DirectoryInfo(Path.Combine(contentPack.DirectoryPath, "Shirts"));
            if (shirtsDir.Exists)
            {
                foreach (DirectoryInfo dir in shirtsDir.EnumerateDirectories())
                {
                    string relativePath = $"Shirts/{dir.Name}";

                    // load data
                    ShirtData shirt = contentPack.ReadJsonFile<ShirtData>($"{relativePath}/shirt.json");
                    if (shirt == null || (shirt.DisableWithMod != null && Helper.ModRegistry.IsLoaded(shirt.DisableWithMod)))
                        continue;

                    // save shirt
                    shirt.textureMale = contentPack.LoadAsset<Texture2D>($"{relativePath}/male.png");
                    if (shirt.Dyeable)
                        shirt.textureMaleColor = contentPack.LoadAsset<Texture2D>($"{relativePath}/male-color.png");
                    if (shirt.HasFemaleVariant)
                    {
                        shirt.textureFemale = contentPack.LoadAsset<Texture2D>($"{relativePath}/female.png");
                        if (shirt.Dyeable)
                            shirt.textureFemaleColor = contentPack.LoadAsset<Texture2D>($"{relativePath}/female-color.png");
                    }
                    shirts.Add(shirt);

                    // Duplicate check
                    if (dupShirts.ContainsKey(shirt.Name))
                        Log.error($"Duplicate shirt: {shirt.Name} just added by {contentPack.Manifest.Name}, already added by {dupWeapons[shirt.Name].Manifest.Name}!");
                    else
                        dupShirts[shirt.Name] = contentPack;
                }
            }

            // Load pants
            DirectoryInfo pantsDir = new DirectoryInfo(Path.Combine(contentPack.DirectoryPath, "Pants"));
            if (pantsDir.Exists)
            {
                foreach (DirectoryInfo dir in pantsDir.EnumerateDirectories())
                {
                    string relativePath = $"Pants/{dir.Name}";

                    // load data
                    PantsData pants = contentPack.ReadJsonFile<PantsData>($"{relativePath}/pants.json");
                    if (pants == null || (pants.DisableWithMod != null && Helper.ModRegistry.IsLoaded(pants.DisableWithMod)))
                        continue;

                    // save pants
                    pants.texture = contentPack.LoadAsset<Texture2D>($"{relativePath}/pants.png");
                    pantss.Add(pants);

                    // Duplicate check
                    if (dupPants.ContainsKey(pants.Name))
                        Log.error($"Duplicate pants: {pants.Name} just added by {contentPack.Manifest.Name}, already added by {dupWeapons[pants.Name].Manifest.Name}!");
                    else
                        dupPants[pants.Name] = contentPack;
                }
            }

            // Load tailoring
            DirectoryInfo tailoringDir = new DirectoryInfo(Path.Combine(contentPack.DirectoryPath, "Tailoring"));
            if (tailoringDir.Exists)
            {
                foreach (DirectoryInfo dir in tailoringDir.EnumerateDirectories())
                {
                    string relativePath = $"Tailoring/{dir.Name}";

                    // load data
                    TailoringRecipeData recipe = contentPack.ReadJsonFile<TailoringRecipeData>($"{relativePath}/recipe.json");
                    if (recipe == null || (recipe.DisableWithMod != null && Helper.ModRegistry.IsLoaded(recipe.DisableWithMod)))
                        continue;

                    tailoring.Add(recipe);
                }
            }
        }

        private void resetAtTitle()
        {
            didInit = false;
            // When we go back to the title menu we need to reset things so things don't break when
            // going back to a save.
            clearIds(out objectIds, objects.ToList<DataNeedsId>());
            clearIds(out cropIds, crops.ToList<DataNeedsId>());
            clearIds(out fruitTreeIds, fruitTrees.ToList<DataNeedsId>());
            clearIds(out bigCraftableIds, bigCraftables.ToList<DataNeedsId>());
            clearIds(out hatIds, hats.ToList<DataNeedsId>());
            clearIds(out weaponIds, weapons.ToList<DataNeedsId>());
            List<DataNeedsId> clothing = new List<DataNeedsId>();
            clothing.AddRange(shirts);
            clothing.AddRange(pantss);
            clearIds(out clothingIds, clothing.ToList<DataNeedsId>());

            var editor = Helper.Content.AssetEditors.FirstOrDefault(p => p is ContentInjector);
            if (editor != null)
                Helper.Content.AssetEditors.Remove(editor);
        }

        private void onCreated(object sender, SaveCreatedEventArgs e)
        {
            Log.debug("Loading stuff early (creation)");
            initStuff( loadIdFiles: false );
        }

        private void onLoadStageChanged(object sender, LoadStageChangedEventArgs e)
        {
            if (e.NewStage == StardewModdingAPI.Enums.LoadStage.SaveParsed)
            {
                Log.debug("Loading stuff early (loading)");
                initStuff( loadIdFiles: true );
            }
            else if ( e.NewStage == StardewModdingAPI.Enums.LoadStage.SaveLoadedLocations )
            {
                Log.debug("Fixing IDs");
                fixIdsEverywhere();
            }
            else if ( e.NewStage == StardewModdingAPI.Enums.LoadStage.Loaded )
            {
                Log.debug("Adding default recipes");
                foreach (var obj in objects)
                {
                    if (obj.Recipe != null && obj.Recipe.IsDefault && !Game1.player.knowsRecipe(obj.Name))
                    {
                        if (obj.Category == ObjectData.Category_.Cooking)
                        {
                            Game1.player.cookingRecipes.Add(obj.Name, 0);
                        }
                        else
                        {
                            Game1.player.craftingRecipes.Add(obj.Name, 0);
                        }
                    }
                }
                foreach (var big in bigCraftables)
                {
                    if (big.Recipe != null && big.Recipe.IsDefault && !Game1.player.knowsRecipe(big.Name))
                    {
                        Game1.player.craftingRecipes.Add(big.Name, 0);
                    }
                }
            }
        }

        private void clientConnected(object sender, PeerContextReceivedEventArgs e)
        {
            if (!Context.IsMainPlayer && !didInit)
            {
                Log.debug("Loading stuff early (MP client)");
                initStuff( loadIdFiles: false );
            }
        }

        /// <summary>Raised after a game menu is opened, closed, or replaced.</summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event arguments.</param>
        private void onMenuChanged(object sender, MenuChangedEventArgs e)
        {
            if ( e.NewMenu == null )
                return;

            if ( e.NewMenu is TitleMenu )
            {
                resetAtTitle();
                return;
            }

            var menu = e.NewMenu as ShopMenu;
            bool hatMouse = menu != null && menu.potraitPersonDialogue == Game1.parseText(Game1.content.LoadString("Strings\\StringsFromCSFiles:ShopMenu.cs.11494"), Game1.dialogueFont, Game1.tileSize * 5 - Game1.pixelZoom * 4);
            string portraitPerson = menu?.portraitPerson?.Name;
            if (portraitPerson == null && Game1.currentLocation?.Name == "Hospital")
                portraitPerson = "Harvey";
            if (menu == null || ( portraitPerson == null || portraitPerson == "" ) && !hatMouse)
                return;

            //if (menu.portraitPerson.name == "Pierre")
            {
                Log.trace($"Adding objects to {portraitPerson}'s shop");

                var forSale = Helper.Reflection.GetField<List<ISalable>>(menu, "forSale").GetValue();
                var itemPriceAndStock = Helper.Reflection.GetField<Dictionary<ISalable, int[]>>(menu, "itemPriceAndStock").GetValue();

                var precondMeth = Helper.Reflection.GetMethod(Game1.currentLocation, "checkEventPrecondition");
                foreach (var obj in objects)
                {
                    if (obj.Recipe != null && obj.Recipe.CanPurchase)
                    {
                        bool add = true;
                        // Can't use continue here or the item might not sell
                        if (obj.Recipe.PurchaseFrom != portraitPerson || (obj.Recipe.PurchaseFrom == "HatMouse" && hatMouse) )
                            add = false;
                        if (Game1.player.craftingRecipes.ContainsKey(obj.Name) || Game1.player.cookingRecipes.ContainsKey(obj.Name))
                            add = false;
                        if (obj.Recipe.PurchaseRequirements != null && obj.Recipe.PurchaseRequirements.Count > 0 &&
                            precondMeth.Invoke<int>(new object[] { obj.Recipe.GetPurchaseRequirementString() }) == -1)
                            add = false;
                        if (add)
                        {
                            var recipeObj = new StardewValley.Object(obj.id, 1, true, obj.Recipe.PurchasePrice, 0);
                            forSale.Add(recipeObj);
                            itemPriceAndStock.Add(recipeObj, new int[] { obj.Recipe.PurchasePrice, 1 });
                            Log.trace($"\tAdding recipe for {obj.Name}");
                        }
                    }
                    if (!obj.CanPurchase)
                        continue;
                    if (obj.PurchaseFrom != portraitPerson || (obj.PurchaseFrom == "HatMouse" && hatMouse))
                        continue;
                    if (obj.PurchaseRequirements != null && obj.PurchaseRequirements.Count > 0 &&
                        precondMeth.Invoke<int>(new object[] { obj.GetPurchaseRequirementString() }) == -1)
                        continue;
                    Item item = new StardewValley.Object(Vector2.Zero, obj.id, int.MaxValue);
                    forSale.Add(item);
                    int price = obj.PurchasePrice;
                    if ( obj.Category == ObjectData.Category_.Seeds )
                    {
                        price = (int)(price * Game1.MasterPlayer.difficultyModifier);
                    }
                    itemPriceAndStock.Add(item, new int[] { price, int.MaxValue });
                    Log.trace($"\tAdding {obj.Name}");
                }
                foreach (var big in bigCraftables)
                {
                    if (big.Recipe != null && big.Recipe.CanPurchase)
                    {
                        bool add = true;
                        // Can't use continue here or the item might not sell
                        if (big.Recipe.PurchaseFrom != portraitPerson || (big.Recipe.PurchaseFrom == "HatMouse" && hatMouse))
                            add = false;
                        if (Game1.player.craftingRecipes.ContainsKey(big.Name) || Game1.player.cookingRecipes.ContainsKey(big.Name))
                            add = false;
                        if (big.Recipe.PurchaseRequirements != null && big.Recipe.PurchaseRequirements.Count > 0 &&
                            precondMeth.Invoke<int>(new object[] { big.Recipe.GetPurchaseRequirementString() }) == -1)
                            add = false;
                        if (add)
                        {
                            var recipeObj = new StardewValley.Object(new Vector2(0, 0), big.id, true);
                            forSale.Add(recipeObj);
                            itemPriceAndStock.Add(recipeObj, new int[] { big.Recipe.PurchasePrice, 1 });
                            Log.trace($"\tAdding recipe for {big.Name}");
                        }
                    }
                    if (!big.CanPurchase)
                        continue;
                    if (big.PurchaseFrom != portraitPerson || (big.PurchaseFrom == "HatMouse" && hatMouse))
                        continue;
                    if (big.PurchaseRequirements != null && big.PurchaseRequirements.Count > 0 &&
                        precondMeth.Invoke<int>(new object[] { big.GetPurchaseRequirementString() }) == -1)
                        continue;
                    Item item = new StardewValley.Object(Vector2.Zero, big.id, false);
                    forSale.Add(item);
                    itemPriceAndStock.Add(item, new int[] { big.PurchasePrice, int.MaxValue });
                    Log.trace($"\tAdding {big.Name}");
                }
                if ( hatMouse )
                {
                    foreach ( var hat in hats )
                    {
                        Item item = new Hat(hat.GetHatId());
                        forSale.Add(item);
                        itemPriceAndStock.Add(item, new int[] { hat.PurchasePrice, int.MaxValue });
                        Log.trace($"\tAdding {hat.Name}");
                    }
                }
                foreach (var weapon in weapons)
                {
                    if (!weapon.CanPurchase)
                        continue;
                    if (weapon.PurchaseFrom != portraitPerson || (weapon.PurchaseFrom == "HatMouse" && hatMouse))
                        continue;
                    if (weapon.PurchaseRequirements != null && weapon.PurchaseRequirements.Count > 0 &&
                        precondMeth.Invoke<int>(new object[] { weapon.GetPurchaseRequirementString() }) == -1)
                        continue;
                    Item item = new StardewValley.Tools.MeleeWeapon(weapon.id);
                    forSale.Add(item);
                    itemPriceAndStock.Add(item, new int[] { weapon.PurchasePrice, int.MaxValue });
                    Log.trace($"\tAdding {weapon.Name}");
                }
            }

            ( ( Api ) api ).InvokeAddedItemsToShop();
        }

        private bool didInit = false;
        private void initStuff( bool loadIdFiles )
        {
            if (didInit)
                return;
            didInit = true;

            // load object ID mappings from save folder
            if (loadIdFiles)
            {
                IDictionary<TKey, TValue> LoadDictionary<TKey, TValue>(string filename)
                {
                    string path = Path.Combine(Constants.CurrentSavePath, "JsonAssets", filename);
                    return File.Exists(path)
                        ? JsonConvert.DeserializeObject<Dictionary<TKey, TValue>>(File.ReadAllText(path))
                        : new Dictionary<TKey, TValue>();
                }
                Directory.CreateDirectory(Path.Combine(Constants.CurrentSavePath, "JsonAssets"));
                oldObjectIds = LoadDictionary<string, int>("ids-objects.json") ?? new Dictionary<string, int>();
                oldCropIds = LoadDictionary<string, int>("ids-crops.json") ?? new Dictionary<string, int>();
                oldFruitTreeIds = LoadDictionary<string, int>("ids-fruittrees.json") ?? new Dictionary<string, int>();
                oldBigCraftableIds = LoadDictionary<string, int>("ids-big-craftables.json") ?? new Dictionary<string, int>();
                oldHatIds = LoadDictionary<string, int>("ids-hats.json") ?? new Dictionary<string, int>();
                oldWeaponIds = LoadDictionary<string, int>("ids-weapons.json") ?? new Dictionary<string, int>();
                oldClothingIds = LoadDictionary<string, int>("ids-clothing.json") ?? new Dictionary<string, int>();

                Log.trace("OLD IDS START");
                foreach (var id in oldObjectIds)
                    Log.trace("\tObject " + id.Key + " = " + id.Value);
                foreach (var id in oldCropIds)
                    Log.trace("\tCrop " + id.Key + " = " + id.Value);
                foreach (var id in oldFruitTreeIds)
                    Log.trace("\tFruit Tree " + id.Key + " = " + id.Value);
                foreach (var id in oldBigCraftableIds)
                    Log.trace("\tBigCraftable " + id.Key + " = " + id.Value);
                foreach (var id in oldHatIds)
                    Log.trace("\tHat " + id.Key + " = " + id.Value);
                foreach (var id in oldWeaponIds)
                    Log.trace("\tWeapon " + id.Key + " = " + id.Value);
                foreach (var id in oldClothingIds)
                    Log.trace("\tClothing " + id.Key + " = " + id.Value);
                Log.trace("OLD IDS END");
            }

            // assign IDs
            objectIds = AssignIds("objects", StartingObjectId, objects.ToList<DataNeedsId>());
            cropIds = AssignIds("crops", StartingCropId, crops.ToList<DataNeedsId>());
            fruitTreeIds = AssignIds("fruittrees", StartingFruitTreeId, fruitTrees.ToList<DataNeedsId>());
            bigCraftableIds = AssignIds("big-craftables", StartingBigCraftableId, bigCraftables.ToList<DataNeedsId>());
            hatIds = AssignIds("hats", StartingHatId, hats.ToList<DataNeedsId>());
            weaponIds = AssignIds("weapons", StartingWeaponId, weapons.ToList<DataNeedsId>());
            List<DataNeedsId> clothing = new List<DataNeedsId>();
            clothing.AddRange(shirts);
            clothing.AddRange(pantss);
            clothingIds = AssignIds("clothing", StartingClothingId, clothing.ToList<DataNeedsId>());

            AssignTextureIndices("shirts", StartingShirtTextureIndex, shirts.ToList<DataSeparateTextureIndex>());
            AssignTextureIndices("pants", StartingPantsTextureIndex, pantss.ToList<DataSeparateTextureIndex>());

            Log.trace("Resetting max shirt/pants value");
            Helper.Reflection.GetField<int>(typeof(Clothing), "_maxShirtValue").SetValue(-1);
            Helper.Reflection.GetField<int>(typeof(Clothing), "_maxPantsValue").SetValue(-1);

            api.InvokeIdsAssigned();

            // init
            Helper.Content.AssetEditors.Add(new ContentInjector());
        }

        /// <summary>Raised after the game finishes writing data to the save file (except the initial save creation).</summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event arguments.</param>
        private void onSaved(object sender, SavedEventArgs e)
        {
            if (!Game1.IsMasterGame)
                return;

            if (!Directory.Exists(Path.Combine(Constants.CurrentSavePath, "JsonAssets")))
                Directory.CreateDirectory(Path.Combine(Constants.CurrentSavePath, "JsonAssets"));

            File.WriteAllText(Path.Combine(Constants.CurrentSavePath, "JsonAssets", "ids-objects.json"), JsonConvert.SerializeObject(objectIds));
            File.WriteAllText(Path.Combine(Constants.CurrentSavePath, "JsonAssets", "ids-crops.json"), JsonConvert.SerializeObject(cropIds));
            File.WriteAllText(Path.Combine(Constants.CurrentSavePath, "JsonAssets", "ids-fruittrees.json"), JsonConvert.SerializeObject(fruitTreeIds));
            File.WriteAllText(Path.Combine(Constants.CurrentSavePath, "JsonAssets", "ids-big-craftables.json"), JsonConvert.SerializeObject(bigCraftableIds));
            File.WriteAllText(Path.Combine(Constants.CurrentSavePath, "JsonAssets", "ids-hats.json"), JsonConvert.SerializeObject(hatIds));
            File.WriteAllText(Path.Combine(Constants.CurrentSavePath, "JsonAssets", "ids-weapons.json"), JsonConvert.SerializeObject(weaponIds));
            File.WriteAllText(Path.Combine(Constants.CurrentSavePath, "JsonAssets", "ids-clothing.json"), JsonConvert.SerializeObject(clothingIds));
        }

        internal IList<ObjectData> myRings = new List<ObjectData>();

        /// <summary>Raised after items are added or removed to a player's inventory. NOTE: this event is currently only raised for the current player.</summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event arguments.</param>
        private void onInventoryChanged(object sender, InventoryChangedEventArgs e)
        {
            if (!e.IsLocalPlayer)
                return;

            IList<int> ringIds = new List<int>();
            foreach (var ring in myRings)
                ringIds.Add(ring.id);

            for (int i = 0; i < Game1.player.Items.Count; ++i)
            {
                var item = Game1.player.Items[i];
                if (item is StardewValley.Object obj && ringIds.Contains(obj.ParentSheetIndex))
                {
                    Log.trace($"Turning a ring-object of {obj.ParentSheetIndex} into a proper ring");
                    Game1.player.Items[i] = new StardewValley.Objects.Ring(obj.ParentSheetIndex);
                }
            }
        }

        private const int StartingObjectId = 2000;
        private const int StartingCropId = 100;
        private const int StartingFruitTreeId = 10;
        private const int StartingBigCraftableId = 300;
        private const int StartingHatId = 80;
        private const int StartingWeaponId = 64;
        private const int StartingClothingId = 3000;
        private const int StartingShirtTextureIndex = 750;
        private const int StartingPantsTextureIndex = 20;

        internal IList<ObjectData> objects = new List<ObjectData>();
        internal IList<CropData> crops = new List<CropData>();
        internal IList<FruitTreeData> fruitTrees = new List<FruitTreeData>();
        internal IList<BigCraftableData> bigCraftables = new List<BigCraftableData>();
        internal IList<HatData> hats = new List<HatData>();
        internal IList<WeaponData> weapons = new List<WeaponData>();
        internal IList<ShirtData> shirts = new List<ShirtData>();
        internal IList<PantsData> pantss = new List<PantsData>();
        internal IList<TailoringRecipeData> tailoring = new List<TailoringRecipeData>();

        internal IDictionary<string, int> objectIds;
        internal IDictionary<string, int> cropIds;
        internal IDictionary<string, int> fruitTreeIds;
        internal IDictionary<string, int> bigCraftableIds;
        internal IDictionary<string, int> hatIds;
        internal IDictionary<string, int> weaponIds;
        internal IDictionary<string, int> clothingIds;

        internal IDictionary<string, int> oldObjectIds;
        internal IDictionary<string, int> oldCropIds;
        internal IDictionary<string, int> oldFruitTreeIds;
        internal IDictionary<string, int> oldBigCraftableIds;
        internal IDictionary<string, int> oldHatIds;
        internal IDictionary<string, int> oldWeaponIds;
        internal IDictionary<string, int> oldClothingIds;

        internal IDictionary<int, string> origObjects;
        internal IDictionary<int, string> origCrops;
        internal IDictionary<int, string> origFruitTrees;
        internal IDictionary<int, string> origBigCraftables;
        internal IDictionary<int, string> origHats;
        internal IDictionary<int, string> origWeapons;
        internal IDictionary<int, string> origClothing;

        public int ResolveObjectId(object data)
        {
            if (data.GetType() == typeof(long))
                return (int)(long)data;
            else
            {
                if (objectIds.ContainsKey((string)data))
                    return objectIds[(string)data];

                foreach ( var obj in Game1.objectInformation )
                {
                    if (obj.Value.Split('/')[0] == (string)data)
                        return obj.Key;
                }

                Log.warn($"No idea what '{data}' is!");
                return 0;
            }
        }

        public int ResolveClothingId(object data)
        {
            if (data.GetType() == typeof(long))
                return (int)(long)data;
            else
            {
                if (clothingIds.ContainsKey((string)data))
                    return clothingIds[(string)data];

                foreach (var obj in Game1.clothingInformation)
                {
                    if (obj.Value.Split('/')[0] == (string)data)
                        return obj.Key;
                }

                Log.warn($"No idea what '{data}' is!");
                return 0;
            }
        }

        private Dictionary<string, int> AssignIds(string type, int starting, IList<DataNeedsId> data)
        {
            Dictionary<string, int> ids = new Dictionary<string, int>();

            int currId = starting;
            foreach (var d in data)
            {
                if (d.id == -1)
                {
                    Log.trace($"New ID: {d.Name} = {currId}");
                    ids.Add(d.Name, currId++);
                    if (type == "objects" && ((ObjectData)d).IsColored)
                        ++currId;
                    d.id = ids[d.Name];
                }
            }

            return ids;
        }

        private void AssignTextureIndices(string type, int starting, IList<DataSeparateTextureIndex> data)
        {
            Dictionary<string, int> idxs = new Dictionary<string, int>();

            int currIdx = starting;
            foreach (var d in data)
            {
                if (d.textureIndex == -1)
                {
                    Log.trace($"New texture index: {d.Name} = {currIdx}");
                    idxs.Add(d.Name, currIdx++);
                    if (type == "shirts" && ((ClothingData)d).HasFemaleVariant)
                        ++currIdx;
                    d.textureIndex = idxs[d.Name];
                }
            }
        }

        private void clearIds(out IDictionary<string, int> ids, List<DataNeedsId> objs)
        {
            ids = null;
            foreach ( DataNeedsId obj in objs )
            {
                obj.id = -1;
            }
        }

        private IDictionary<int, string> cloneIdDictAndRemoveOurs( IDictionary<int, string> full, IDictionary<string, int> ours )
        {
            var ret = new Dictionary<int, string>(full);
            foreach (var obj in ours)
                ret.Remove(obj.Value);
            return ret;
        }

        private void fixIdsEverywhere()
        {
            origObjects = cloneIdDictAndRemoveOurs(Game1.objectInformation, objectIds);
            origCrops = cloneIdDictAndRemoveOurs(Game1.content.Load<Dictionary<int, string>>("Data\\Crops"), cropIds);
            origFruitTrees = cloneIdDictAndRemoveOurs(Game1.content.Load<Dictionary<int, string>>("Data\\fruitTrees"), fruitTreeIds);
            origBigCraftables = cloneIdDictAndRemoveOurs(Game1.bigCraftablesInformation, bigCraftableIds);
            origHats = cloneIdDictAndRemoveOurs(Game1.content.Load<Dictionary<int, string>>("Data\\hats"), hatIds);
            origWeapons = cloneIdDictAndRemoveOurs(Game1.content.Load<Dictionary<int, string>>("Data\\weapons"), weaponIds);
            origClothing = cloneIdDictAndRemoveOurs(Game1.content.Load<Dictionary<int, string>>("Data\\ClothingInformation"), clothingIds);

            fixItemList(Game1.player.Items);
#pragma warning disable AvoidNetField
            if (Game1.player.leftRing.Value != null && fixId(oldObjectIds, objectIds, Game1.player.leftRing.Value.parentSheetIndex, origObjects))
                Game1.player.leftRing.Value = null;
            if (Game1.player.rightRing.Value != null && fixId(oldObjectIds, objectIds, Game1.player.rightRing.Value.parentSheetIndex, origObjects))
                Game1.player.rightRing.Value = null;
            if (Game1.player.hat.Value != null && fixId(oldObjectIds, objectIds, Game1.player.hat.Value.parentSheetIndex, origObjects))
                Game1.player.hat.Value = null;
            if (Game1.player.shirtItem.Value != null && fixId(oldClothingIds, clothingIds, Game1.player.shirtItem.Value.parentSheetIndex, origClothing))
                Game1.player.shirtItem.Value = null;
            if (Game1.player.pantsItem.Value != null && fixId(oldClothingIds, clothingIds, Game1.player.pantsItem.Value.parentSheetIndex, origClothing))
                Game1.player.pantsItem.Value = null;
#pragma warning restore AvoidNetField
            foreach ( var loc in Game1.locations )
                fixLocation(loc);

            fixIdDict(Game1.player.basicShipped);
            fixIdDict(Game1.player.mineralsFound);
            fixIdDict(Game1.player.recipesCooked);
            fixIdDict2(Game1.player.archaeologyFound);
            fixIdDict2(Game1.player.fishCaught);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage( "SMAPI.CommonErrors", "AvoidNetField") ]
        private void fixLocation( GameLocation loc )
        {
            if (loc is FarmHouse fh)
            {
#pragma warning disable AvoidImplicitNetFieldCast
                if (fh.fridge.Value?.items != null)
#pragma warning restore AvoidImplicitNetFieldCast
                    fixItemList(fh.fridge.Value.items);
            }

            IList<Vector2> toRemove = new List<Vector2>();
            foreach ( var tfk in loc.terrainFeatures.Keys )
            {
                var tf = loc.terrainFeatures[tfk];
                if ( tf is HoeDirt hd )
                {
                    if (hd.crop == null)
                        continue;

                    var oldId = hd.crop.rowInSpriteSheet.Value;
                    if (fixId(oldCropIds, cropIds, hd.crop.rowInSpriteSheet, origCrops))
                        hd.crop = null;
                    else
                    {
                        var key = cropIds.FirstOrDefault(x => x.Value == hd.crop.rowInSpriteSheet.Value).Key;
                        var c = crops.FirstOrDefault(x => x.Name == key);
                        if ( c != null ) // Non-JA crop
                        {
                            Log.trace("Fixing crop product: From " + hd.crop.indexOfHarvest.Value + " to " + c.Product + "=" + ResolveObjectId(c.Product));
                            hd.crop.indexOfHarvest.Value = ResolveObjectId(c.Product);
                        }
                    }
                }
                else if ( tf is FruitTree ft )
                {
                    var oldId = ft.treeType.Value;
                    if (fixId(oldFruitTreeIds, fruitTreeIds, ft.treeType, origFruitTrees))
                        toRemove.Add(tfk);
                    else
                    {
                        var key = fruitTreeIds.FirstOrDefault(x => x.Value == ft.treeType.Value).Key;
                        var ftt = fruitTrees.FirstOrDefault(x => x.Name == key);
                        if ( ftt != null ) // Non-JA fruit tree
                        {
                            Log.trace("Fixing fruit tree product: From " + ft.indexOfFruit.Value + " to " + ftt.Product + "=" + ResolveObjectId(ftt.Product));
                            ft.indexOfFruit.Value = ResolveObjectId(ftt.Product);
                        }
                    }
                }
            }
            foreach (var rem in toRemove)
                loc.terrainFeatures.Remove(rem);

            toRemove.Clear();
            foreach ( var objk in loc.netObjects.Keys )
            {
                var obj = loc.netObjects[objk];
                if ( obj is Chest chest )
                {
                    fixItemList(chest.items);
                }
                else
                {
                    if (!obj.bigCraftable.Value)
                    {
                        if (fixId(oldObjectIds, objectIds, obj.parentSheetIndex, origObjects))
                            toRemove.Add(objk);
                    }
                    else
                    {
                        if (fixId(oldBigCraftableIds, bigCraftableIds, obj.parentSheetIndex, origBigCraftables))
                            toRemove.Add(objk);
                    }
                }
                
                if ( obj.heldObject.Value != null )
                {
                    if (fixId(oldObjectIds, objectIds, obj.heldObject.Value.parentSheetIndex, origObjects))
                        obj.heldObject.Value = null;

                    if ( obj.heldObject.Value is Chest chest2 )
                    {
                        fixItemList(chest2.items);
                    }
                }
            }
            foreach (var rem in toRemove)
                loc.objects.Remove(rem);

            toRemove.Clear();
            foreach (var objk in loc.overlayObjects.Keys)
            {
                var obj = loc.overlayObjects[objk];
                if (obj is Chest chest)
                {
                    fixItemList(chest.items);
                }
                else
                {
                    if (!obj.bigCraftable.Value)
                    {
                        if (fixId(oldObjectIds, objectIds, obj.parentSheetIndex, origObjects))
                            toRemove.Add(objk);
                    }
                    else
                    {
                        if (fixId(oldBigCraftableIds, bigCraftableIds, obj.parentSheetIndex, origBigCraftables))
                            toRemove.Add(objk);
                    }
                }

                if (obj.heldObject.Value != null)
                {
                    if (fixId(oldObjectIds, objectIds, obj.heldObject.Value.parentSheetIndex, origObjects))
                        obj.heldObject.Value = null;

                    if (obj.heldObject.Value is Chest chest2)
                    {
                        fixItemList(chest2.items);
                    }
                }
            }
            foreach (var rem in toRemove)
                loc.overlayObjects.Remove(rem);

            if (loc is BuildableGameLocation buildLoc)
                foreach (var building in buildLoc.buildings)
                {
                    if (building.indoors.Value != null)
                        fixLocation(building.indoors.Value);
                    if ( building is Mill mill )
                    {
                        fixItemList(mill.input.Value.items);
                        fixItemList(mill.output.Value.items);
                    }
                    else if ( building is FishPond pond )
                    {
                        if (fixId(oldObjectIds, objectIds, pond.fishType, origObjects))
                            pond.fishType.Value = -1;
                        if (pond.GetFishObject() != null && fixId(oldObjectIds, objectIds, pond.GetFishObject().parentSheetIndex, origObjects))
                            Helper.Reflection.GetField<StardewValley.Object>(pond, "_fishObject").SetValue(null);
                        if (pond.sign.Value != null && fixId(oldObjectIds, objectIds, pond.sign.Value.parentSheetIndex, origObjects))
                            pond.sign.Value = null;
                        if (pond.output.Value != null && fixId(oldObjectIds, objectIds, pond.output.Value.parentSheetIndex, origObjects))
                            pond.output.Value = null;
                        if (pond.neededItem.Value != null && fixId(oldObjectIds, objectIds, pond.neededItem.Value.parentSheetIndex, origObjects))
                            pond.neededItem.Value = null;
                    }
                }
            
            if (loc is DecoratableLocation decoLoc)
                foreach (var furniture in decoLoc.furniture)
                {
                    if (furniture is StorageFurniture storage)
                        fixItemList(storage.heldItems);
                }
            
            if (loc is Farm farm)
            {
                foreach (var animal in farm.Animals.Values)
                {
                    if (animal.currentProduce.Value != -1)
                        if (fixId(oldObjectIds, objectIds, animal.currentProduce, origObjects))
                            animal.currentProduce.Value = -1;
                    if (animal.defaultProduceIndex.Value != -1)
                        if (fixId(oldObjectIds, objectIds, animal.defaultProduceIndex, origObjects))
                            animal.defaultProduceIndex.Value = 0;
                    if (animal.deluxeProduceIndex.Value != -1)
                        if (fixId(oldObjectIds, objectIds, animal.deluxeProduceIndex, origObjects))
                            animal.deluxeProduceIndex.Value = 0;
                }
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("SMAPI.CommonErrors", "AvoidNetField")]
        private void fixItemList( IList< Item > items )
        {
            for ( int i = 0; i < items.Count; ++i )
            {
                var item = items[i];
                if ( item is StardewValley.Object obj )
                {
                    if (!obj.bigCraftable.Value)
                    {
                        if (fixId(oldObjectIds, objectIds, obj.parentSheetIndex, origObjects))
                            items[i] = null;
                    }
                    else
                    {
                        if (fixId(oldBigCraftableIds, bigCraftableIds, obj.parentSheetIndex, origBigCraftables))
                            items[i] = null;
                    }
                }
                else if ( item is Hat hat )
                {
                    if (fixId(oldHatIds, hatIds, hat.which, origHats))
                        items[i] = null;
                }
                else if ( item is MeleeWeapon weapon )
                {
                    if (fixId(oldWeaponIds, weaponIds, weapon.initialParentTileIndex, origWeapons))
                        items[i] = null;
                    else if (fixId(oldWeaponIds, weaponIds, weapon.currentParentTileIndex, origWeapons))
                        items[i] = null;
                    else if (fixId(oldWeaponIds, weaponIds, weapon.currentParentTileIndex, origWeapons))
                        items[i] = null;
                }
                else if ( item is Ring ring )
                {
                    if (fixId(oldObjectIds, objectIds, ring.indexInTileSheet, origObjects))
                        items[i] = null;
                }
                else if ( item is Clothing clothing )
                {
                    if (fixId(oldClothingIds, clothingIds, clothing.parentSheetIndex, origClothing))
                        items[i] = null;
                }
            }
        }

        private void fixIdDict(NetIntDictionary<int, NetInt> dict)
        {
            var toRemove = new List<int>();
            var toAdd = new Dictionary<int, int>();
            foreach (var entry in dict.Keys)
            {
                if (origObjects.ContainsKey(entry))
                    continue;
                else if (oldObjectIds.Values.Contains(entry))
                {
                    var key = oldObjectIds.FirstOrDefault(x => x.Value == entry).Key;

                    if (objectIds.ContainsKey(key))
                    {
                        toRemove.Add(entry);
                        toAdd.Add(objectIds[key], dict[entry]);
                    }
                }
            }
            foreach (var entry in toRemove)
                dict.Remove(entry);
            foreach (var entry in toAdd)
                dict.Add(entry.Key, entry.Value);
        }

        private void fixIdDict2(NetIntIntArrayDictionary dict)
        {
            var toRemove = new List<int>();
            var toAdd = new Dictionary<int, int[]>();
            foreach (var entry in dict.Keys)
            {
                if (origObjects.ContainsKey(entry))
                    continue;
                else if (oldObjectIds.Values.Contains(entry))
                {
                    var key = oldObjectIds.FirstOrDefault(x => x.Value == entry).Key;

                    if (objectIds.ContainsKey(key))
                    {
                        toRemove.Add(entry);
                        toAdd.Add(objectIds[key], dict[entry]);
                    }
                }
            }
            foreach (var entry in toRemove)
                dict.Remove(entry);
            foreach (var entry in toAdd)
                dict.Add(entry.Key, entry.Value);
        }

        // Return true if the item should be deleted, false otherwise.
        // Only remove something if old has it but not new
        private bool fixId(IDictionary<string, int> oldIds, IDictionary<string, int> newIds, NetInt id, IDictionary<int, string> origData )
        {
            if (origData.ContainsKey(id.Value))
                return false;

            if (oldIds.Values.Contains(id.Value))
            {
                int id_ = id.Value;
                var key = oldIds.FirstOrDefault(x => x.Value == id_).Key;

                if (newIds.ContainsKey(key))
                {
                    id.Value = newIds[key];
                    Log.trace("Changing ID: " + key + " from ID " + id_ + " to " + id.Value);
                    return false;
                }
                else
                {
                    Log.trace("Deleting missing item " + key + " with old ID " + id_);
                    return true;
                }
            }
            else return false;
        }
    }
}
