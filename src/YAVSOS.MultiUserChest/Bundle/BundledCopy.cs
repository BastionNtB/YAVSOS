using System;
using System.Linq;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;
using YAVSOS.Vendor.MultiUserChest.Patches;

namespace YAVSOS.Vendor.MultiUserChest {
    /// <summary>
    /// YAVSOS addition, replacing upstream's <c>Plugin</c> class: applies this copy's patches under YAVSOS's own
    /// Harmony id, the way upstream's <c>Awake</c> and <c>Start</c> do. Call once, after the chainloader has loaded
    /// every plugin and before <c>Game.Start</c>, and only when no other MultiUserChest copy is active.
    /// </summary>
    public static class BundledCopy {
        public const string HarmonyId = "com.bastionntb.yavsos.multiuserchest";
        private static Harmony harmony;

        public static bool Active => harmony != null;

        public static void Activate(ManualLogSource logger) {
            if (harmony != null) {
                return;
            }

            Log.Init(logger);
            harmony = new Harmony(HarmonyId);

            try {
                // Upstream's harmony.PatchAll(): every annotated patch class of this copy.
                foreach (Type type in typeof(BundledCopy).Assembly.GetTypes().Where(IsAnnotatedPatchClass)) {
                    harmony.CreateClassProcessor(type).Patch();
                }

                // Upstream's ApplyModPatches(); its QuickStack compatibility is disabled upstream too.
                if (Chainloader.PluginInfos.ContainsKey("mkz.itemdrawers")) {
                    harmony.PatchAll(typeof(ItemDrawerCompat));
                }
            } catch {
                harmony.UnpatchSelf();
                harmony = null;
                throw;
            }
        }

        private static bool IsAnnotatedPatchClass(Type type) {
            return type.Namespace != null && type.Namespace.StartsWith(typeof(BundledCopy).Namespace, StringComparison.Ordinal) &&
                   type.IsDefined(typeof(HarmonyPatch), false);
        }

        public static void Deactivate() {
            harmony?.UnpatchSelf();
            harmony = null;
        }
    }
}
