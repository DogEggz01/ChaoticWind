using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ChaoticWind
{
    internal sealed class ConfigurationManagerMetadata
    {
        public bool? ShowRangeAsPercent;
        public string DispName;
        public int? Order;
    }

    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(BorderExpanderGuid, BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class ChaoticWindPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.pete.sailwind.windconfigurator";
        public const string PluginName = "Chaotic Wind";
        public const string PluginVersion = "1.2.0";
        public const string BorderExpanderGuid = "com.nandbrew.borderexpander";
        public const string ClimatePluginGuid = "com.raddude.climate";

        private const float DefaultAlAnkhDirectionChaos = 0.14f;
        private const float DefaultEmeraldDirectionChaos = 0.18f;
        private const float DefaultFireFishDirectionChaos = 0.25f;
        private const float DefaultAestrinDirectionChaos = 0.21f;
        private const float DefaultChronosDirectionChaos = 0.21f;
        private const float DefaultWindChangeTimer = 40f;
        private const float DefaultFinalLerpSpeed = 0.5f;
        private const float DefaultCombinedBonusCap = 20f;

        private ConfigEntry<float> alAnkhDirectionChaos;
        private ConfigEntry<float> emeraldDirectionChaos;
        private ConfigEntry<float> fireFishDirectionChaos;
        private ConfigEntry<float> aestrinDirectionChaos;
        private ConfigEntry<float> chronosDirectionChaos;
        private ConfigEntry<float> windChangeTimer;
        private ConfigEntry<bool> overrideFinalLerpSpeed;
        private ConfigEntry<float> finalLerpSpeed;
        private ConfigEntry<bool> additionalOceanScaling;
        private ConfigEntry<float> combinedBonusCap;

        private Harmony harmony;
        private Wind trackedWind;
        private float capturedFinalLerpSpeed;
        private bool hasCapturedFinalLerpSpeed;
        private bool finalLerpOverrideApplied;
        private float capturedTradeWindInfluence;
        private float capturedMinimumMagnitude;
        private bool hasCapturedTradeWindDefaults;

        public static ChaoticWindPlugin Instance { get; private set; }
        public ConfigEntry<bool> DisableTradeWind { get; private set; }

        public bool TradeWindDisabled
        {
            get { return DisableTradeWind != null && DisableTradeWind.Value; }
        }

        public bool TradeWindOverrideActive
        {
            get { return TradeWindDisabled && !ClimateCustomWindsActive(); }
        }

        private void Awake()
        {
            Instance = this;
            BindConfiguration();
            SubscribeToConfiguration();

            harmony = new Harmony(PluginGuid);
            harmony.PatchAll(Assembly.GetExecutingAssembly());

            ApplyRuntimeSettings();
            Logger.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void OnDestroy()
        {
            UnsubscribeFromConfiguration();

            if (trackedWind != null && finalLerpOverrideApplied && hasCapturedFinalLerpSpeed)
            {
                trackedWind.finalLerpSpeed = capturedFinalLerpSpeed;
            }

            harmony?.UnpatchSelf();

            if (Instance == this)
            {
                Instance = null;
            }
        }

        private void BindConfiguration()
        {
            DisableTradeWind = Config.Bind(
                "Trade Wind",
                "Disable Trade Wind",
                true,
                "When enabled, Wind.GetCurrentTradeWind returns Vector3.zero.");

            alAnkhDirectionChaos = BindDirectionChaos(
                "AlAnkh",
                "Al'Ankh",
                DefaultAlAnkhDirectionChaos,
                50);

            emeraldDirectionChaos = BindDirectionChaos(
                "Emerald",
                "Emerald",
                DefaultEmeraldDirectionChaos,
                40);

            fireFishDirectionChaos = BindDirectionChaos(
                "Fire Fish Lagoon",
                "Fire Fish Lagoon",
                DefaultFireFishDirectionChaos,
                30);

            aestrinDirectionChaos = BindDirectionChaos(
                "Aestrin",
                "Aestrin",
                DefaultAestrinDirectionChaos,
                20);

            chronosDirectionChaos = BindDirectionChaos(
                "Chronos",
                "Chronos",
                DefaultChronosDirectionChaos,
                10);

            windChangeTimer = BindSlider(
                "Wind Timing",
                "Wind Change Timer",
                DefaultWindChangeTimer,
                2f,
                900f,
                "There is a random range for time from half the setting value to double the setting value. Setting value higher than 600sec(10min) is recommended if you go crazy on Direction Chaos setting.",
                10);

            overrideFinalLerpSpeed = Config.Bind(
                "Wind Smoothing",
                "Override Final Lerp Speed",
                false,
                "Enable the Final Lerp Speed slider override. When disabled, the original game value remains in use.");

            finalLerpSpeed = BindSlider(
                "Wind Smoothing",
                "Final Lerp Speed",
                DefaultFinalLerpSpeed,
                0.1f,
                60f,
                "This value changes how fast wind speed changes. At 60 it will instantly change to the new wind speed at 60 FPS. USE AT YOUR OWN RISK.",
                10);

            additionalOceanScaling = Config.Bind(
                "Open Ocean Wind",
                "Enable Additional Ocean Scaling",
                false,
                "Continue ocean scaling beyond 4,000 units, reaching 2.3 (~1.52x Base Wind as bonus) at 72,000 units. Ignored while Climate Custom Winds is enabled.");

            combinedBonusCap = BindSlider(
                "Open Ocean Wind",
                "Storm + Ocean Bonus Cap",
                DefaultCombinedBonusCap,
                0f,
                100f,
                "Maximum combined storm and ocean wind bonus. Original game value is 20. Ignored while Climate Custom Winds is enabled.",
                10);
        }

        private ConfigEntry<float> BindDirectionChaos(string key, string displayName, float defaultValue, int order)
        {
            return BindSlider(
                "Direction Chaos",
                key,
                defaultValue,
                0.01f,
                1f,
                "Direction chaos for " + displayName + ".",
                order,
                displayName);
        }

        private ConfigEntry<float> BindSlider(
            string section,
            string key,
            float defaultValue,
            float minimum,
            float maximum,
            string description,
            int order,
            string displayName = null)
        {
            ConfigurationManagerMetadata metadata = new ConfigurationManagerMetadata
            {
                ShowRangeAsPercent = false,
                DispName = displayName,
                Order = order
            };

            return Config.Bind(
                section,
                key,
                defaultValue,
                new ConfigDescription(
                    description,
                    new AcceptableValueRange<float>(minimum, maximum),
                    metadata));
        }

        private void SubscribeToConfiguration()
        {
            alAnkhDirectionChaos.SettingChanged += OnRegionSettingChanged;
            emeraldDirectionChaos.SettingChanged += OnRegionSettingChanged;
            fireFishDirectionChaos.SettingChanged += OnRegionSettingChanged;
            aestrinDirectionChaos.SettingChanged += OnRegionSettingChanged;
            chronosDirectionChaos.SettingChanged += OnRegionSettingChanged;

            DisableTradeWind.SettingChanged += OnWindSettingChanged;
            windChangeTimer.SettingChanged += OnWindSettingChanged;
            overrideFinalLerpSpeed.SettingChanged += OnWindSettingChanged;
            finalLerpSpeed.SettingChanged += OnWindSettingChanged;
        }

        private void UnsubscribeFromConfiguration()
        {
            alAnkhDirectionChaos.SettingChanged -= OnRegionSettingChanged;
            emeraldDirectionChaos.SettingChanged -= OnRegionSettingChanged;
            fireFishDirectionChaos.SettingChanged -= OnRegionSettingChanged;
            aestrinDirectionChaos.SettingChanged -= OnRegionSettingChanged;
            chronosDirectionChaos.SettingChanged -= OnRegionSettingChanged;

            DisableTradeWind.SettingChanged -= OnWindSettingChanged;
            windChangeTimer.SettingChanged -= OnWindSettingChanged;
            overrideFinalLerpSpeed.SettingChanged -= OnWindSettingChanged;
            finalLerpSpeed.SettingChanged -= OnWindSettingChanged;
        }

        private void OnRegionSettingChanged(object sender, EventArgs e)
        {
            ApplyRuntimeSettings();
        }

        private void OnWindSettingChanged(object sender, EventArgs e)
        {
            ApplyRuntimeSettings();
        }

        private void ApplyRuntimeSettings()
        {
            ApplyRegionSettings();
            TrackAndApplyWind(Wind.instance);
            ApplyActiveRegionChaos();
        }

        public void ApplyRegionSettings()
        {
            Region[] regions = FindObjectsOfType<Region>();
            for (int i = 0; i < regions.Length; i++)
            {
                Region region = regions[i];
                if (TryGetChaosForRegion(region, out float chaos))
                {
                    region.windDirChaos = chaos;
                }
            }
        }

        internal void ApplyActiveRegionChaos()
        {
            if (Weather.instance == null || Weather.instance.currentRegion == null)
            {
                return;
            }

            Region sourceRegion = GetRegionBlenderTargetRegion() ?? Weather.instance.currentRegion;
            if (TryGetChaosForRegion(sourceRegion, out float chaos))
            {
                Weather.instance.currentRegion.windDirChaos = chaos;
            }
        }

        private static Region GetRegionBlenderTargetRegion()
        {
            if (RegionBlender.instance == null)
            {
                return null;
            }

            FieldInfo field = AccessTools.Field(typeof(RegionBlender), "currentTargetRegion");
            return field?.GetValue(RegionBlender.instance) as Region;
        }

        private bool TryGetChaosForRegion(Region region, out float chaos)
        {
            chaos = 0f;
            if (region == null)
            {
                return false;
            }

            string name = region.gameObject.name;
            switch (name)
            {
                case "Region Al'ankh":
                case "Region Equatorial":
                case "Region Mid Latitude":
                    chaos = alAnkhDirectionChaos.Value;
                    return true;

                case "Region Emerald (new smaller)":
                    chaos = emeraldDirectionChaos.Value;
                    return true;

                case "Region Emerald Lagoon":
                    chaos = fireFishDirectionChaos.Value;
                    return true;

                case "Region Medi":
                    chaos = aestrinDirectionChaos.Value;
                    return true;

                case "Region Medi East":
                case "Region Northern":
                    chaos = chronosDirectionChaos.Value;
                    return true;

                default:
                    return false;
            }
        }

        public void TrackAndApplyWind(Wind wind)
        {
            if (wind == null)
            {
                return;
            }

            if (trackedWind != wind)
            {
                trackedWind = wind;
                capturedFinalLerpSpeed = wind.finalLerpSpeed;
                hasCapturedFinalLerpSpeed = true;
                finalLerpOverrideApplied = false;
                capturedTradeWindInfluence = wind.tradeWindInfluence;
                capturedMinimumMagnitude = wind.minimumMagnitude;
                hasCapturedTradeWindDefaults = true;
            }

            wind.changeTimer = windChangeTimer.Value;

            if (overrideFinalLerpSpeed.Value)
            {
                wind.finalLerpSpeed = finalLerpSpeed.Value;
                finalLerpOverrideApplied = true;
            }
            else if (finalLerpOverrideApplied && hasCapturedFinalLerpSpeed)
            {
                wind.finalLerpSpeed = capturedFinalLerpSpeed;
                finalLerpOverrideApplied = false;
            }
        }

        public void EnforceTradeWindOverride(Wind wind, ref Vector3 result)
        {
            if (!TradeWindOverrideActive)
            {
                return;
            }

            result = Vector3.zero;

            if (hasCapturedTradeWindDefaults && wind == trackedWind)
            {
                wind.tradeWindInfluence = capturedTradeWindInfluence;
                wind.minimumMagnitude = capturedMinimumMagnitude;
            }
        }

        internal static float GetConfiguredOceanLerp(
            float originalMinimum,
            float originalMaximum,
            float distance)
        {
            ChaoticWindPlugin plugin = Instance;
            if (plugin == null || !plugin.additionalOceanScaling.Value)
            {
                return Mathf.InverseLerp(originalMinimum, originalMaximum, distance);
            }

            return distance <= 4000f
                ? Mathf.InverseLerp(1500f, 4000f, distance)
                : Mathf.Lerp(
                    1f,
                    2.3f,
                    Mathf.InverseLerp(4000f, 72000f, distance));
        }

        internal static float GetConfiguredBonusCap()
        {
            return Instance == null
                ? DefaultCombinedBonusCap
                : Instance.combinedBonusCap.Value;
        }

        internal static bool ClimateCustomWindsActive()
        {
            if (!GameState.playing ||
                !Chainloader.PluginInfos.TryGetValue(ClimatePluginGuid, out PluginInfo climate) ||
                climate.Instance == null)
            {
                return false;
            }

            return climate.Instance.Config.TryGetEntry(
                       "Settings",
                       "Enable Custom Winds",
                       out ConfigEntry<bool> enabled) &&
                   enabled.Value;
        }
    }

    [HarmonyPatch(typeof(Wind), "GetCurrentTradeWind")]
    internal static class GetCurrentTradeWindPatch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        [HarmonyBefore(new[] { ChaoticWindPlugin.BorderExpanderGuid })]
        private static bool Prefix(ref Vector3 __result)
        {
            ChaoticWindPlugin plugin = ChaoticWindPlugin.Instance;
            if (plugin == null || !plugin.TradeWindOverrideActive)
            {
                return true;
            }

            __result = Vector3.zero;
            return false;
        }

        [HarmonyPostfix]
        [HarmonyAfter(new[] { ChaoticWindPlugin.BorderExpanderGuid })]
        private static void Postfix(Wind __instance, ref Vector3 __result)
        {
            ChaoticWindPlugin.Instance?.EnforceTradeWindOverride(__instance, ref __result);
        }
    }

    [HarmonyPatch(typeof(Wind), "Awake")]
    internal static class WindAwakePatch
    {
        [HarmonyPostfix]
        private static void Postfix(Wind __instance)
        {
            ChaoticWindPlugin.Instance?.TrackAndApplyWind(__instance);
        }
    }

    [HarmonyPatch(typeof(Wind), "SetNewWindTarget")]
    internal static class WindSetNewTargetPatch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        [HarmonyBefore(new[] { ChaoticWindPlugin.ClimatePluginGuid })]
        private static void Prefix()
        {
            ChaoticWindPlugin.Instance?.ApplyActiveRegionChaos();
        }

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);

            MethodInfo inverseLerp = AccessTools.Method(
                typeof(Mathf),
                nameof(Mathf.InverseLerp),
                new[] { typeof(float), typeof(float), typeof(float) });
            FieldInfo distanceToLand = AccessTools.Field(
                typeof(GameState),
                nameof(GameState.distanceToLand));
            MethodInfo oceanHelper = AccessTools.Method(
                typeof(ChaoticWindPlugin),
                nameof(ChaoticWindPlugin.GetConfiguredOceanLerp));
            MethodInfo capHelper = AccessTools.Method(
                typeof(ChaoticWindPlugin),
                nameof(ChaoticWindPlugin.GetConfiguredBonusCap));

            int oceanCallIndex = -1;
            List<int> capIndexes = new List<int>();

            for (int i = 0; i < code.Count; i++)
            {
                if (i >= 3 &&
                    Equals(code[i].operand, inverseLerp) &&
                    LoadsFloat(code[i - 3], 1500f) &&
                    LoadsFloat(code[i - 2], 4000f) &&
                    code[i - 1].opcode == OpCodes.Ldsfld &&
                    Equals(code[i - 1].operand, distanceToLand))
                {
                    oceanCallIndex = i;
                }

                if (LoadsFloat(code[i], 20f))
                {
                    capIndexes.Add(i);
                }
            }

            if (oceanCallIndex < 0 || capIndexes.Count != 2)
            {
                Debug.LogError(
                    "[Chaotic Wind] Vanilla ocean wind patch pattern was not found.");
                return code;
            }

            code[oceanCallIndex].operand = oceanHelper;

            for (int i = 0; i < capIndexes.Count; i++)
            {
                int index = capIndexes[i];
                CodeInstruction original = code[index];
                CodeInstruction replacement = new CodeInstruction(OpCodes.Call, capHelper);
                replacement.labels.AddRange(original.labels);
                replacement.blocks.AddRange(original.blocks);
                code[index] = replacement;
            }

            return code;
        }

        private static bool LoadsFloat(CodeInstruction instruction, float value)
        {
            return instruction.opcode == OpCodes.Ldc_R4 &&
                   instruction.operand is float loadedValue &&
                   loadedValue == value;
        }
    }

    [HarmonyPatch(typeof(RegionBlender), "Start")]
    internal static class RegionBlenderStartPatch
    {
        [HarmonyPostfix]
        [HarmonyAfter(new[] { ChaoticWindPlugin.BorderExpanderGuid })]
        private static void Postfix()
        {
            ChaoticWindPlugin.Instance?.ApplyRegionSettings();
            ChaoticWindPlugin.Instance?.ApplyActiveRegionChaos();
        }
    }
}
