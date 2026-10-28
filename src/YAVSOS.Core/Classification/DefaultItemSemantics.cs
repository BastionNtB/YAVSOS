using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace YAVSOS.Core.Classification;

/// <summary>Versioned, exact definition semantics; never registration, ownership or rarity evidence.</summary>
internal static class DefaultItemSemantics
{
    internal const string Version = "yavsos-item-semantics-7";
    private const string Source = "default-item-semantics";
    private static readonly IReadOnlyDictionary<string, Definition> Definitions = Create();

    // Called only for actual catalog registrations. Unknown identities and changed type contexts
    // leave automatic/source rules responsible for classification rather than guessing a family.
    internal static IEnumerable<MembershipEvidence> Memberships(string prefab, string itemType)
    {
        if (prefab == null) throw new ArgumentNullException(nameof(prefab));
        if (itemType == null) throw new ArgumentNullException(nameof(itemType));
        if (!Definitions.TryGetValue(prefab, out var definition)) return Array.Empty<MembershipEvidence>();
        var supported = definition.Context == Context.Resource ? itemType == "Material" || itemType == "Misc"
            : definition.Context == Context.Trophy ? itemType == "Trophy" : itemType == "Consumable";
        return supported ? definition.Memberships : Array.Empty<MembershipEvidence>();
    }

    private enum Context { Resource, Trophy, Consumable }

    private sealed class Definition
    {
        internal Context Context { get; }
        internal IReadOnlyList<MembershipEvidence> Memberships { get; }
        internal Definition(Context context, IEnumerable<MembershipEvidence> memberships)
        {
            Context = context;
            Memberships = Array.AsReadOnly(memberships.OrderBy(x => x.Category, StringComparer.Ordinal).ToArray());
        }
    }

    private static IReadOnlyDictionary<string, Definition> Create()
    {
        var rows = new Dictionary<string, (Context Context, Dictionary<string, MembershipEvidence> Members)>(StringComparer.Ordinal);
        void Add(string category, string receipt, Context context, params string[] prefabs)
        {
            foreach (var prefab in prefabs)
            {
                if (!rows.TryGetValue(prefab, out var row))
                    row = (context, new Dictionary<string, MembershipEvidence>(StringComparer.Ordinal));
                if (row.Context != context) throw new InvalidOperationException("Conflicting semantic type context: " + prefab);
                // A definition contributes each category once, even when semantic families overlap.
                if (!row.Members.ContainsKey(category))
                    row.Members.Add(category, new MembershipEvidence(category, Source,
                        "Exact definition: " + prefab + "; " + receipt + "."));
                rows[prefab] = row;
            }
        }
        const string reviewed = "YAVSOS reviewed material/special defaults";
        const string loaded = "2026-10-14 loaded ID/type/name-key evidence";
        const string wizardry = "Wizardry 1.2.2 item/translation definitions";
        const string monstrum = "Monstrum 1.6.0 item/drop definitions";
        const string described = "Valheim 1.0.17 item description";
        const string warfare = "Warfare 1.9.5 English item description";
        void Resource(string category, params string[] prefabs) => Add(category, reviewed, Context.Resource, prefabs);
        void Loaded(string category, params string[] prefabs) => Add(category, loaded, Context.Resource, prefabs);
        // Residual definitions whose own description states the material family; uses stay source-owned.
        void Described(string category, params string[] prefabs) => Add(category, described, Context.Resource, prefabs);
        void Magic(string category, string receipt, Context context, params string[] prefabs)
        {
            Add(category, receipt, context, prefabs);
            Add("magic.materials", receipt, context, prefabs);
        }

        Resource("materials.wood", "Wood", "FineWood", "RoundLog", "ElderBark", "YggdrasilWood", "Blackwood", "Frostwood");
        Resource("materials.ore", "CopperOre", "TinOre", "IronScrap", "SilverOre", "BlackMetalScrap", "FlametalOreNew", "GoldOre", "FlametalOre");
        Loaded("materials.ore", "BronzeScrap", "CopperScrap", "IronOre");
        Resource("materials.metal", "Copper", "Tin", "Bronze", "Iron", "Silver", "BlackMetal", "FlametalNew", "Gold", "Steel_TW", "Flametal");
        Resource("materials.stone", "Stone", "Flint", "Obsidian", "BlackMarble", "Marble", "SulfurStone", "Grausten", "Crystal", "DarkCrystal_TW", "GemstoneBlue", "GemstoneGreen", "GemstoneRed");
        Loaded("materials.stone", "Ice", "StoneRock", "Amethyst_TW", "Diamond_TW", "Emerald_TW", "Onyx_TW", "Sapphire_TW", "Topaz_TW");
        Resource("materials.hides", "LeatherScraps", "DeerHide", "TrollHide", "WolfPelt", "LoxPelt", "HareScaleHide", "ScaleHide", "Carapace", "Chitin", "SerpentScale", "BjornHide", "RottenPelt_TW", "AskHide", "MooseHide", "SealHide", "BonemawSerpentScale", "Leatherstraps", "GrizzlyBearPelt_TW", "RazorbackLeather_TW");
        Resource("materials.textile", "LinenThread", "JuteRed", "JuteBlue", "JuteBlack_TW", "NornThread", "WolfHairBundle", "ElakingHairBundle");
        Loaded("materials.textile", "WoolScraps_TW", "WoolYarn_Black_TW", "WoolYarn_Blue_TW", "WoolYarn_Brown_TW", "WoolYarn_Green_TW", "WoolYarn_Red_TW", "WoolYarn_Teal_TW", "WoolYarn_White_TW", "WoolYarn_Yellow_TW",
            "FabricBundle_Blackforest_TW", "FabricBundle_Mistlands_TW", "FabricBundle_Mountain_TW", "FabricBundle_Plains_TW", "FabricBundle_Swamp_TW");
        Resource("materials.creature", "BoneFragments", "CharredBone", "BjornPaw", "BonemawSerpentTooth", "Mandible", "MoleClaws", "MooseSinew", "MorgenHeart", "MorgenSinew", "Needle", "Bilebag", "AskBladder", "LoxBone_TW", "TrollBone_TW", "UndeadBjornRibcage", "VenomousFang_TW", "WolfClaw", "WolfFang", "ProwlerFang_TW", "RazorbackTusk_TW");
        Loaded("materials.creature", "AsksvinCarrionNeck", "AsksvinCarrionPelvic", "AsksvinCarrionRibcage", "AsksvinCarrionSkull", "Feathers", "CelestialFeather", "Bloodbag", "Entrails", "FreezeGland", "GreydwarfEye", "HardAntler", "Root", "Softtissue");
        Resource("materials.components", "Chain", "IronNails", "BronzeNails", "CeramicPlate", "Hook", "BellFragment", "ScytheHandle");
        Loaded("materials.components", "BarrelRings", "CandleWick", "CharredCogwheel", "DvergrNeedle", "MechanicalSpring", "SharpeningStone", "Pot_Shard_Green", "Pot_Shard_Red");
        Resource("materials.cores", "SurtlingCore", "BlackCore", "MoltenCore", "Wisp", "YagluthDrop", "FaderEmber", "FaderDrop", "YmirRemains", "OrbFrostFire", "OrbThunderBlood", "FreyjaEssence_TW", "Voidplasm");
        Loaded("materials.cores", "DragonTear", "Ectoplasm", "Eitr", "FrostCore", "QueenDrop", "ShieldCore", "Thunderstone", "Staffcore_BlackForest_TW", "Staffcore_Mistlands_TW", "Staffcore_Mountain_TW", "Staffcore_Plains_TW", "Staffcore_Swamp_TW");
        Resource("materials.moulds", "MoldArmorGoldChest", "MoldArmorGoldHelmet", "MoldArmorGoldLegs", "MoldArmorMageChest", "MoldArmorMageHelmet", "MoldArmorMageLegs", "MoldArmorMediumHelmet", "MoldArmorMediumLegs", "MoldArmormediumChest", "MoldAtgeir", "MoldAxe", "MoldAxe2H", "MoldBow", "MoldCrossbow", "MoldFistweapon", "MoldKeys", "MoldKnife", "MoldMace", "MoldMace2H", "MoldShieldBuckler", "MoldShieldRound", "MoldShieldTower", "MoldSpear", "MoldStaffOrbofAhri", "MoldStafffrostorbs", "MoldStaffspiritcaller", "MoldStaffthunderblood", "MoldSword", "MoldSword2H");
        Loaded("materials.moulds", "MoldSmallParts");
        Resource("materials.upgrades", "AxeHead1", "AxeHead2", "DvergrKeyFragment", "DyrnwynBladeFragment", "DyrnwynHiltFragment", "DyrnwynTipFragment");
        Loaded("materials.processing", "GenericMoldUncooked", "SmallPartsGoldUncooked");
        Described("materials.wood", "BarkaBranch");
        Described("materials.stone", "CrownJewel");
        Described("materials.creature", "Charredskull", "WrithanRoots", "CuredSquirrelHamstring");
        Described("materials.textile", "Flax");
        Described("materials.components", "BlobVial");
        Add("materials.textile", warfare, Context.Resource, "GoblinScraps_TW");
        Add("materials.pigments", warfare, Context.Resource, "DyeBlack_TW");

        Add("currency", "EpicLoot 0.14.13 adventure currency definitions", Context.Resource,
            "ForestToken", "IronBountyToken", "GoldBountyToken");
        Resource("special.keys", "HildirKey_forestcrypt", "HildirKey_mountaincave", "HildirKey_plainsfortress");

        Resource("special.offerings", "FrozenKingDrop");
        Resource("special.runes", "AncientScroll_TW", "RuneAlgiz_TW", "RuneBer_TW", "RuneDagaz_TW", "RuneEihwaz_TW", "RuneFehu_TW", "RuneJara_TW", "RuneMannaz_TW", "RuneOthala_TW", "RunePerth_TW", "RuneRaidho_TW", "RuneThurisaz_TW");
        Loaded("special.runes", "LastBossGate_RuneTile");

        Magic("magic.wizardry.shards", wizardry, Context.Resource, "ShardElder_TW", "ShardBonemass_TW", "ShardModer_TW", "ShardYagluth_TW", "ShardQueen_TW");
        Magic("magic.wizardry.enchanted-wood", wizardry, Context.Resource, "EnchantedWoodBlackForest_TW", "EnchantedWoodSwamp_TW", "EnchantedWoodMountain_TW", "EnchantedWoodPlains_TW", "EnchantedWoodMistlands_TW");
        Magic("magic.wizardry.scrolls", wizardry, Context.Resource, "ArcaneScroll_BlackForest_TW", "ArcaneScroll_Swamp_TW", "ArcaneScroll_Mountain_TW", "ArcaneScroll_Plains_TW", "ArcaneScroll_Mistlands_TW");
        Magic("magic.wizardry.scrolls", wizardry, Context.Consumable, "ArcaneScroll_DamageBuff_TW", "ArcaneScroll_SpeedBuff_TW", "ArcaneScroll_JumpBuff_TW", "ArcaneScroll_SlowfallBuff_TW");

        Add("materials.hides", monstrum, Context.Resource, "FoxPelt_TW");
        // E07 upstream compatibility patch (D042): Therzie Armory 1.4.2, Warfare 1.9.4 and Wizardry 1.2.2 swap these
        // materials into recipes from Player.OnSpawned, which never runs on a dedicated server, so the authoritative
        // server never observes their crafting use. Remove when those mods apply the swap at registration time or
        // YAVSOS observes the effective recipes on the server.
        Add("materials.crafting", "D042 E07 spawn-time recipe swap not visible to dedicated servers", Context.Resource,
            "Diamond_TW", "Onyx_TW", "FoxPelt_TW");
        // This named semantic association includes source-inspected drops owned by Warfare.
        // It deliberately supplies no registration-origin or related-mod facet.
        Add("mod.monstrum", monstrum, Context.Resource, "FoxPelt_TW", "ProwlerFang_TW", "RazorbackTusk_TW", "LoxBone_TW", "GrizzlyBearPelt_TW", "RottenPelt_TW", "RazorbackLeather_TW");
        Add("mod.monstrum", monstrum, Context.Trophy, "TrophyProwler_TW", "TrophyGrizzlyBear_TW", "TrophyObsidianGolem_TW", "TrophyRazorback_TW", "TrophyCrawler_TW", "TrophyRottingElk_TW", "TrophyFox_TW", "TrophyAsmodeus_TW", "TrophySvalt_TW", "TrophyVrykolathas_TW", "TrophyShark_TW");

        return new ReadOnlyDictionary<string, Definition>(rows.ToDictionary(x => x.Key,
            x => new Definition(x.Value.Context, x.Value.Members.Values), StringComparer.Ordinal));
    }
}
