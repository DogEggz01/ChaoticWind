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
    // Configuration Manager discovers this optional metadata tag by its exact
    // type name. Keep the name aligned with its public integration contract.
    internal sealed class ConfigurationManagerAttributes
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
        private static readonly MethodInfo WindSetNewGustTargetMethod =
            AccessTools.Method(typeof(Wind), "SetNewGustTarget");

        public const string PluginGuid = "com.pete.sailwind.windconfigurator";
        public const string PluginName = "Chaotic Wind";
        public const string PluginVersion = "1.4.0";
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
        private float capturedWindChangeTimer;
        private float capturedFinalLerpSpeed;
        private bool hasCapturedFinalLerpSpeed;
        private bool finalLerpOverrideApplied;
        private float capturedTradeWindInfluence;
        private float capturedMinimumMagnitude;
        private bool hasCapturedTradeWindDefaults;
        private int lastTradeWindRetargetSample = int.MinValue;
        private bool pendingTradeWindRetarget;

        public static ChaoticWindPlugin Instance { get; private set; }
        public ConfigEntry<bool> EnableTradeWind { get; private set; }
        public ConfigEntry<bool> SimpleTradeWindCycle { get; private set; }

        public bool TradeWindEnabled
        {
            get { return EnableTradeWind == null || EnableTradeWind.Value; }
        }

        public bool TradeWindDisabled
        {
            get { return !TradeWindEnabled; }
        }

        public bool TradeWindCycleEnabled
        {
            get
            {
                return TradeWindEnabled &&
                       (SimpleTradeWindCycle == null ||
                        SimpleTradeWindCycle.Value);
            }
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
            ClimateCompatibility.Install(harmony);

            ApplyRuntimeSettings();
            Logger.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void OnDestroy()
        {
            UnsubscribeFromConfiguration();
            pendingTradeWindRetarget = false;

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

        private void Update()
        {
            UpdateTradeWindRetargeting();
        }

        private void BindConfiguration()
        {
            EnableTradeWind = Config.Bind(
                "Trade Wind",
                "Enable Trade Wind",
                true,
                new ConfigDescription(
                    "Enable trade winds and allow the optional trade-wind cycle below. Turn this off to disable all trade winds. Ignored while Climate Custom Winds is enabled.",
                    null,
                    new ConfigurationManagerAttributes
                    {
                        Order = 20
                    }));

            SimpleTradeWindCycle = Config.Bind(
                "Trade Wind",
                "Simple Trade wind cycle",
                true,
                new ConfigDescription(
                    "Enable 20 days trade wind cycle between Al'Ankh trade wind and Emerald Trade wind. One of them will dominant the latitude between 30N-33N at each cycle",
                    null,
                    new ConfigurationManagerAttributes
                    {
                        Order = 10
                    }));

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
                "Adjusts in increments of 1 second. The actual change interval is randomly selected from half to double this value. Values above 600 seconds are recommended with extreme Direction Chaos settings.",
                10);

            overrideFinalLerpSpeed = Config.Bind(
                "Wind Smoothing",
                "Override Final Lerp Speed",
                false,
                "Enable the Final Lerp Speed slider override for normal wind behavior. A separate storm mod may temporarily override it while inside a storm.");

            finalLerpSpeed = BindSlider(
                "Wind Smoothing",
                "Final Lerp Speed",
                DefaultFinalLerpSpeed,
                0.1f,
                60f,
                0.01f,
                "Adjusts in increments of 0.01. Higher values make wind speed change faster. At 60 this setting changes wind speed almost instantly at 60 FPS. USE AT YOUR OWN RISK.",
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
                "Adjusts in increments of 1. Maximum combined storm and ocean wind bonus. Original game value is 20. Also overrides Climate Custom Winds and is available to Better Storm.",
                10);
        }

        private void UpdateTradeWindRetargeting()
        {
            Wind wind = Wind.instance;
            if (!GameState.playing || wind == null || Sun.sun == null)
            {
                lastTradeWindRetargetSample = int.MinValue;
                return;
            }

            if (ClimateCustomWindsActive() || !TradeWindCycleEnabled)
            {
                lastTradeWindRetargetSample = int.MinValue;
                return;
            }

            if (!TryGetPlayerLatitude(out float latitude) ||
                latitude < 30f ||
                latitude > 33f)
            {
                lastTradeWindRetargetSample = int.MinValue;
                return;
            }

            int sample = TradeWindCycle.GetRetargetSample(
                GetCurrentAbsoluteDay());
            if (sample == lastTradeWindRetargetSample)
            {
                return;
            }

            lastTradeWindRetargetSample = sample;
            RequestTradeWindRetarget();
        }

        private void RequestTradeWindRetarget()
        {
            if (Wind.instance == null)
            {
                return;
            }

            pendingTradeWindRetarget = true;
            Wind.instance.debugChangeWind = true;
        }

        internal void CompleteRequestedTradeWindRetarget(Wind wind)
        {
            if (!pendingTradeWindRetarget || wind != Wind.instance)
            {
                return;
            }

            pendingTradeWindRetarget = false;
            try
            {
                WindSetNewGustTargetMethod?.Invoke(wind, null);
            }
            catch (Exception exception)
            {
                LogFeatureError(
                    "Could not refresh the gust target after a trade-wind transition update: " +
                    exception);
            }
        }

        private ConfigEntry<float> BindDirectionChaos(
            string key,
            string displayName,
            float defaultValue,
            int order)
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
            ConfigurationManagerAttributes metadata = new ConfigurationManagerAttributes
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

        private void SubscribeToConfiguration()
        {
            alAnkhDirectionChaos.SettingChanged += OnRegionSettingChanged;
            emeraldDirectionChaos.SettingChanged += OnRegionSettingChanged;
            fireFishDirectionChaos.SettingChanged += OnRegionSettingChanged;
            aestrinDirectionChaos.SettingChanged += OnRegionSettingChanged;
            chronosDirectionChaos.SettingChanged += OnRegionSettingChanged;

            EnableTradeWind.SettingChanged += OnWindSettingChanged;
            SimpleTradeWindCycle.SettingChanged += OnWindSettingChanged;
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

            EnableTradeWind.SettingChanged -= OnWindSettingChanged;
            SimpleTradeWindCycle.SettingChanged -= OnWindSettingChanged;
            windChangeTimer.SettingChanged -= OnWindSettingChanged;
            overrideFinalLerpSpeed.SettingChanged -= OnWindSettingChanged;
            finalLerpSpeed.SettingChanged -= OnWindSettingChanged;
        }

        private void OnRegionSettingChanged(object sender, EventArgs e)
        {
            ApplyRegionSettings();
            ApplyActiveRegionChaos();
        }

        private void OnWindSettingChanged(object sender, EventArgs e)
        {
            if ((sender == EnableTradeWind ||
                 sender == SimpleTradeWindCycle) &&
                Wind.instance != null)
            {
                lastTradeWindRetargetSample = int.MinValue;
                RequestTradeWindRetarget();
            }
            TrackAndApplyWind(Wind.instance);
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

            Region sourceRegion = GetCurrentGameplayRegion();
            if (TryGetChaosForRegion(sourceRegion, out float chaos))
            {
                Weather.instance.currentRegion.windDirChaos = chaos;
            }
        }

        internal static Region GetCurrentGameplayRegion()
        {
            Region targetRegion = GetRegionBlenderTargetRegion();
            return targetRegion != null
                ? targetRegion
                : Weather.instance?.currentRegion;
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
                pendingTradeWindRetarget = false;
                lastTradeWindRetargetSample = int.MinValue;
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
            if (TryGetTradeWindOverride(wind, out Vector3 configuredResult))
            {
                result = configuredResult;
            }
        }

        internal bool TryGetTradeWindOverride(
            Wind wind,
            out Vector3 result)
        {
            result = Vector3.zero;

            if (ClimateCustomWindsActive())
            {
                return false;
            }

            if (TradeWindDisabled)
            {
                RestoreTradeWindFields(wind);
                return true;
            }

            if (!TradeWindCycleEnabled)
            {
                return false;
            }

            if (!TryGetPlayerLatitude(out float latitude))
            {
                return false;
            }

            if (latitude < 30f || latitude > 33f)
            {
                return false;
            }

            result = TradeWindCycle.GetDirection(GetCurrentAbsoluteDay());
            return true;
        }

        private static bool TryGetPlayerLatitude(out float latitude)
        {
            latitude = 0f;
            if (FloatingOriginManager.instance == null ||
                Refs.observerMirror == null)
            {
                return false;
            }

            latitude = FloatingOriginManager.instance.GetGlobeCoords(
                Refs.observerMirror.transform).z;
            return true;
        }

        private void RestoreTradeWindFields(Wind wind)
        {
            if (!hasCapturedTradeWindDefaults || wind != trackedWind)
            {
                return;
            }

            wind.tradeWindInfluence = capturedTradeWindInfluence;
            wind.minimumMagnitude = capturedMinimumMagnitude;
        }

        private static float GetCurrentAbsoluteDay()
        {
            float timeOfDay = Sun.sun != null
                ? Mathf.Repeat(Sun.sun.globalTime, 24f) / 24f
                : 0f;
            return Mathf.Max(0, GameState.day) + timeOfDay;
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
        private static bool Prefix(Wind __instance, ref Vector3 __result)
        {
            ChaoticWindPlugin plugin = ChaoticWindPlugin.Instance;
            if (plugin == null ||
                !plugin.TryGetTradeWindOverride(__instance, out Vector3 result))
            {
                return true;
            }

            __result = result;
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

        [HarmonyPostfix]
        private static void Postfix(Wind __instance)
        {
            ChaoticWindPlugin.Instance?.CompleteRequestedTradeWindRetarget(
                __instance);
        }

        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            return WindIlRewriter.RewriteVanilla(instructions);
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
