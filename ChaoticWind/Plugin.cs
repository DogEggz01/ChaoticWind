using System;
using System.Collections.Generic;
using System.Reflection;
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

    internal sealed class SteppedAcceptableValueRange : AcceptableValueRange<float>
    {
        private readonly float step;

        internal SteppedAcceptableValueRange(float minimum, float maximum, float step)
            : base(minimum, maximum)
        {
            this.step = step;
        }

        public override object Clamp(object value)
        {
            float clamped = (float)base.Clamp(value);
            float snapped = MinValue +
                Mathf.Floor((clamped - MinValue) / step + 0.5f) * step;
            return Mathf.Clamp(snapped, MinValue, MaxValue);
        }

        public override bool IsValid(object value)
        {
            if (!(value is float floatValue))
            {
                return false;
            }

            float snapped = (float)Clamp(floatValue);
            return Mathf.Approximately(floatValue, snapped);
        }
    }

    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(BorderExpanderGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(ClimatePluginGuid, BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class ChaoticWindPlugin : BaseUnityPlugin
    {
        private static readonly FieldInfo RegionBlenderTargetRegionField =
            AccessTools.Field(typeof(RegionBlender), "currentTargetRegion");

        public const string PluginGuid = "com.pete.sailwind.windconfigurator";
        public const string PluginName = "Chaotic Wind";
        public const string PluginVersion = "1.3.3";
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
        private const int DefaultStormRelocationDistance = 44000;

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
        private ConfigEntry<bool> squallsEnabled;
        private ConfigEntry<bool> hurricaneEnabled;
        private ConfigEntry<bool> generalStormChangesEnabled;
        private ConfigEntry<int> stormRelocationDistance;

        private Harmony harmony;
        private Wind trackedWind;
        private float capturedWindChangeTimer;
        private float capturedFinalLerpSpeed;
        private bool hasCapturedFinalLerpSpeed;
        private bool finalLerpOverrideApplied;
        private float capturedTradeWindInfluence;
        private float capturedMinimumMagnitude;
        private bool hasCapturedTradeWindDefaults;

        public static ChaoticWindPlugin Instance { get; private set; }
        public ConfigEntry<bool> EnableTradeWind { get; private set; }

        public bool TradeWindEnabled
        {
            get { return EnableTradeWind == null || EnableTradeWind.Value; }
        }

        public bool TradeWindDisabled
        {
            get { return !TradeWindEnabled; }
        }

        public bool TradeWindOverrideActive
        {
            get { return TradeWindDisabled && !ClimateCustomWindsActive(); }
        }

        internal bool SquallsEnabled
        {
            get { return squallsEnabled != null && squallsEnabled.Value; }
        }

        internal bool HurricaneEnabled
        {
            get { return hurricaneEnabled != null && hurricaneEnabled.Value; }
        }

        internal bool AnyCustomStormsEnabled
        {
            get { return SquallsEnabled || HurricaneEnabled; }
        }

        internal bool GeneralStormChangesEnabled
        {
            get
            {
                return generalStormChangesEnabled != null &&
                    generalStormChangesEnabled.Value;
            }
        }

        internal bool IsCustomStormEnabled(ModStormKind kind)
        {
            return kind == ModStormKind.Squall
                ? SquallsEnabled
                : HurricaneEnabled;
        }

        private void Awake()
        {
            Instance = this;
            BindConfiguration();
            SubscribeToConfiguration();

            harmony = new Harmony(PluginGuid);
            harmony.PatchAll(Assembly.GetExecutingAssembly());
            ClimateCompatibility.Install(harmony);

            ApplyRuntimeSettings();
            Logger.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void OnDestroy()
        {
            UnsubscribeFromConfiguration();

            WindOverrideState.RestoreImmediately();
            MediEastStormSetting.Apply(false);
            ModStormFactory.Shutdown();
            HurricanePersistence.Reset();

            if (trackedWind != null)
            {
                trackedWind.changeTimer = capturedWindChangeTimer;
                if (finalLerpOverrideApplied && hasCapturedFinalLerpSpeed)
                {
                    trackedWind.finalLerpSpeed = capturedFinalLerpSpeed;
                }
            }

            harmony?.UnpatchSelf();

            if (Instance == this)
            {
                Instance = null;
            }
        }

        private void BindConfiguration()
        {
            EnableTradeWind = Config.Bind(
                "Trade Wind",
                "Enable Trade Wind",
                true,
                "Keep vanilla trade winds enabled. Turn this off to disable trade winds. Ignored while Climate Custom Winds is enabled.");

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
                1f,
                "Adjusts in increments of 1 second. There is a random range for time from half the setting value to double the setting value. Setting value higher than 600sec(10min) is recommended if you go crazy on Direction Chaos setting.",
                10);

            overrideFinalLerpSpeed = Config.Bind(
                "Wind Smoothing",
                "Override Final Lerp Speed",
                false,
                "Enable the Final Lerp Speed slider override outside storms. When disabled, the original game value remains in use outside storms. General Storm Changes always uses its authoritative value while inside a storm.");

            finalLerpSpeed = BindSlider(
                "Wind Smoothing",
                "Final Lerp Speed",
                DefaultFinalLerpSpeed,
                0.1f,
                60f,
                0.01f,
                "Adjusts in increments of 0.01. This value changes how fast wind speed changes outside storms. General Storm Changes has authority while inside a storm and uses 1.0 instead. At 60 this setting instantly changes wind speed at 60 FPS. USE AT YOUR OWN RISK.",
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
                1f,
                "Adjusts in increments of 1. Maximum combined storm and ocean wind bonus. Original game value is 20. Also overrides Climate Custom Winds.",
                10);

            generalStormChangesEnabled = Config.Bind(
                "Storms",
                "Enable General Storm Changes",
                true,
                "Enable normalized storm selection, radius-based storm bonus calculation, and custom wind and gust behavior while inside any storm. Storm gusts use a fixed 10-second interval and wind lerp speed 1.0. This setting does not enable custom storms or change Medi East storm count.");

            squallsEnabled = Config.Bind(
                "Storms",
                "Enable Squalls",
                true,
                "Enable all three Squalls and all Squall-specific mechanics, including movement, relocation, particles, thunder, and transitional rain that reaches intensity 50. Enabling this sets Medi East storm count to 3.");

            hurricaneEnabled = Config.Bind(
                "Storms",
                "Enable Hurricane",
                true,
                "Enable the Hurricane and all Hurricane-specific mechanics, including movement, relocation, particles, thunder, persistence, and transitional rain that reaches intensity 15. Enabling this sets Medi East storm count to 3.");

            stormRelocationDistance = BindIntegerSlider(
                "Storms",
                "Storm Relocation Distance",
                DefaultStormRelocationDistance,
                18000,
                90000,
                "Distance at which custom storms are shifted past the player. Vanilla default is 44000. Adjusts in increments of 1.",
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
                0.01f,
                "Direction chaos for " + displayName + ". Adjusts in increments of 0.01.",
                order,
                displayName);
        }

        private ConfigEntry<float> BindSlider(
            string section,
            string key,
            float defaultValue,
            float minimum,
            float maximum,
            float step,
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
                    new SteppedAcceptableValueRange(minimum, maximum, step),
                    metadata));
        }

        private ConfigEntry<int> BindIntegerSlider(
            string section,
            string key,
            int defaultValue,
            int minimum,
            int maximum,
            string description,
            int order)
        {
            ConfigurationManagerMetadata metadata = new ConfigurationManagerMetadata
            {
                ShowRangeAsPercent = false,
                Order = order
            };

            return Config.Bind(
                section,
                key,
                defaultValue,
                new ConfigDescription(
                    description,
                    new AcceptableValueRange<int>(minimum, maximum),
                    metadata));
        }

        private void SubscribeToConfiguration()
        {
            alAnkhDirectionChaos.SettingChanged += OnRegionSettingChanged;
            emeraldDirectionChaos.SettingChanged += OnRegionSettingChanged;
            fireFishDirectionChaos.SettingChanged += OnRegionSettingChanged;
            aestrinDirectionChaos.SettingChanged += OnRegionSettingChanged;
            chronosDirectionChaos.SettingChanged += OnRegionSettingChanged;

            EnableTradeWind.SettingChanged += OnWindSettingChanged;
            windChangeTimer.SettingChanged += OnWindSettingChanged;
            overrideFinalLerpSpeed.SettingChanged += OnWindSettingChanged;
            finalLerpSpeed.SettingChanged += OnWindSettingChanged;
            squallsEnabled.SettingChanged += OnCustomStormSettingChanged;
            hurricaneEnabled.SettingChanged += OnCustomStormSettingChanged;
            generalStormChangesEnabled.SettingChanged +=
                OnGeneralStormChangesSettingChanged;
        }

        private void UnsubscribeFromConfiguration()
        {
            alAnkhDirectionChaos.SettingChanged -= OnRegionSettingChanged;
            emeraldDirectionChaos.SettingChanged -= OnRegionSettingChanged;
            fireFishDirectionChaos.SettingChanged -= OnRegionSettingChanged;
            aestrinDirectionChaos.SettingChanged -= OnRegionSettingChanged;
            chronosDirectionChaos.SettingChanged -= OnRegionSettingChanged;

            EnableTradeWind.SettingChanged -= OnWindSettingChanged;
            windChangeTimer.SettingChanged -= OnWindSettingChanged;
            overrideFinalLerpSpeed.SettingChanged -= OnWindSettingChanged;
            finalLerpSpeed.SettingChanged -= OnWindSettingChanged;
            squallsEnabled.SettingChanged -= OnCustomStormSettingChanged;
            hurricaneEnabled.SettingChanged -= OnCustomStormSettingChanged;
            generalStormChangesEnabled.SettingChanged -=
                OnGeneralStormChangesSettingChanged;
        }

        private void OnRegionSettingChanged(object sender, EventArgs e)
        {
            ApplyRegionSettings();
            ApplyActiveRegionChaos();
        }

        private void OnWindSettingChanged(object sender, EventArgs e)
        {
            TrackAndApplyWind(Wind.instance);
        }

        private void OnCustomStormSettingChanged(object sender, EventArgs e)
        {
            ModStormFactory.ApplyEnabledState();
            MediEastStormSetting.Apply(AnyCustomStormsEnabled);
            RefreshCurrentStorm();
        }

        private void OnGeneralStormChangesSettingChanged(object sender, EventArgs e)
        {
            WindOverrideState.OnGeneralChangesToggle(GeneralStormChangesEnabled);
            RefreshCurrentStorm();
        }

        private static void RefreshCurrentStorm()
        {
            if (GameState.playing && WeatherStorms.instance != null)
            {
                WeatherStorms.instance.FindClosestStorm();
            }
        }

        private void Update()
        {
            WindOverrideState.Tick();
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

            return RegionBlenderTargetRegionField?.GetValue(
                RegionBlender.instance) as Region;
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
                capturedWindChangeTimer = wind.changeTimer;
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

            WindOverrideState.EnforceFinalLerpAuthority(wind);
        }

        internal float GetConfiguredWindChangeTimer()
        {
            return windChangeTimer.Value;
        }

        internal float GetConfiguredFinalLerpSpeed(float fallback)
        {
            if (overrideFinalLerpSpeed.Value)
            {
                return finalLerpSpeed.Value;
            }

            return hasCapturedFinalLerpSpeed ? capturedFinalLerpSpeed : fallback;
        }

        internal static float GetConfiguredStormRelocationDistance()
        {
            return Instance == null
                ? DefaultStormRelocationDistance
                : Instance.stormRelocationDistance.Value;
        }

        internal void LogFeatureInfo(string message)
        {
            Logger.LogInfo(message);
        }

        internal void LogFeatureWarning(string message)
        {
            Logger.LogWarning(message);
        }

        internal void LogFeatureError(string message)
        {
            Logger.LogError(message);
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
        [HarmonyPriority(Priority.Last)]
        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            return StormWindIlRewriter.RewriteVanilla(instructions);
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
            MediEastStormSetting.Apply(
                ChaoticWindPlugin.Instance?.AnyCustomStormsEnabled == true);
        }
    }
}
