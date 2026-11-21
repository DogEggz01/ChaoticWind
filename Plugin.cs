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
        private enum SimpleTradeWindArea
        {
            None,
            MidLatitude,
            UpperMidLatitude,
            Northern
        }

        private static readonly FieldInfo RegionBlenderTargetRegionField =
            AccessTools.Field(typeof(RegionBlender), "currentTargetRegion");
        private static readonly FieldInfo TrackerUpdatingField =
            AccessTools.Field(typeof(IslandDistanceTracker), "updating");
        private static readonly MethodInfo WindSetNewGustTargetMethod =
            AccessTools.Method(typeof(Wind), "SetNewGustTarget");

        // Climate's "Enable Custom Winds" entry, looked up on the first check in play.
        private static ConfigEntry<bool> climateCustomWinds;
        private static bool climateLookedUp;

        public const string PluginGuid = "com.pete.sailwind.windconfigurator";
        public const string PluginName = "Chaotic Wind";
        public const string PluginVersion = "1.4.4";
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
        private const float MidCycleSouthLatitude = 30f;
        private const float MidCycleCalmLatitude = 32f;
        private const float MidCycleNorthLatitude = 33f;
        private const float MidCycleEastLongitudeLimit = 7f;

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
        private bool finalLerpOverrideApplied;
        private int lastTradeWindRetargetSample = int.MinValue;
        private bool pendingWindRetarget;

        public static ChaoticWindPlugin Instance { get; private set; }
        public ConfigEntry<bool> EnableTradeWind { get; private set; }
        public ConfigEntry<bool> SimpleTradeWindCycle { get; private set; }

        public bool TradeWindEnabled
        {
            get { return EnableTradeWind == null || EnableTradeWind.Value; }
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

        private void Awake()
        {
            Instance = this;
            BindConfiguration();
            SubscribeToConfiguration();

            harmony = new Harmony(PluginGuid);
            harmony.PatchAll(Assembly.GetExecutingAssembly());
            ClimateCompatibility.Install(harmony);

            if (TrackerUpdatingField == null)
            {
                Logger.LogError(
                    "IslandDistanceTracker.updating was not found; the wind does not wait for a new land distance after a load.");
            }

            ApplyRuntimeSettings();
            Logger.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void OnDestroy()
        {
            UnsubscribeFromConfiguration();
            pendingWindRetarget = false;
            LandDistance.Wait = LandDistanceWait.None;

            if (trackedWind != null)
            {
                trackedWind.changeTimer = capturedWindChangeTimer;
                if (finalLerpOverrideApplied)
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
            UpdateLandDistanceWait();
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
                false,
                new ConfigDescription(
                    "Enable a 20-day trade-wind cycle between the Al'Ankh and Emerald trade winds from 30N-33N, up to 7E longitude. During the Emerald cycle, 32N-33N has no trade wind as in vanilla. Above 33N, ENE and ESE follow the same cycle, starting with ENE. Each transition takes one in-game day.",
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
            if (!TradeWindCycleEnabled ||
                !GameState.playing ||
                Wind.instance == null ||
                Sun.sun == null ||
                ClimateCustomWindsActive())
            {
                lastTradeWindRetargetSample = int.MinValue;
                return;
            }

            int sample = TradeWindCycle.GetRetargetSample(
                GetCurrentAbsoluteDay());
            if (lastTradeWindRetargetSample == int.MinValue)
            {
                lastTradeWindRetargetSample = sample;
                return;
            }

            if (sample == lastTradeWindRetargetSample)
            {
                return;
            }

            lastTradeWindRetargetSample = sample;
            if (!TryGetPlayerCoordinates(
                    out float longitude,
                    out float latitude) ||
                GetSimpleTradeWindArea(longitude, latitude) ==
                    SimpleTradeWindArea.None)
            {
                return;
            }

            RequestWindRetarget();
        }

        internal static void BeginLandDistanceWait()
        {
            if (TrackerUpdatingField != null)
            {
                LandDistance.Wait = LandDistanceWait.Player;
            }
        }

        private void UpdateLandDistanceWait()
        {
            if (LandDistance.Wait == LandDistanceWait.None)
            {
                return;
            }

            IslandDistanceTracker tracker = IslandDistanceTracker.instance;
            PlayerControllerMirror observer = Refs.observerMirror;
            if (tracker == null || observer == null)
            {
                return;
            }

            LandDistance.Wait = LandDistance.Advance(
                LandDistance.Wait,
                GameState.playing && observer.enabled,
                true.Equals(TrackerUpdatingField.GetValue(tracker)));

            // The wind targets set during the wait left out the ocean bonus.
            if (LandDistance.Wait == LandDistanceWait.None &&
                LandDistance.HasOceanBonus(GameState.distanceToLand))
            {
                RequestWindRetarget();
            }
        }

        private void RequestWindRetarget()
        {
            if (Wind.instance == null)
            {
                return;
            }

            pendingWindRetarget = true;
            Wind.instance.debugChangeWind = true;
        }

        internal void CompleteRequestedWindRetarget(Wind wind)
        {
            if (!pendingWindRetarget || wind != Wind.instance)
            {
                return;
            }

            pendingWindRetarget = false;
            try
            {
                WindSetNewGustTargetMethod?.Invoke(wind, null);
            }
            catch (Exception exception)
            {
                LogFeatureError(
                    "Could not refresh the gust target after a requested wind retarget: " +
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
                RequestWindRetarget();
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
                pendingWindRetarget = false;
                lastTradeWindRetargetSample = int.MinValue;
                trackedWind = wind;
                capturedWindChangeTimer = wind.changeTimer;
                capturedFinalLerpSpeed = wind.finalLerpSpeed;
                finalLerpOverrideApplied = false;
            }

            wind.changeTimer = windChangeTimer.Value;

            if (overrideFinalLerpSpeed.Value)
            {
                wind.finalLerpSpeed = finalLerpSpeed.Value;
                finalLerpOverrideApplied = true;
            }
            else if (finalLerpOverrideApplied)
            {
                wind.finalLerpSpeed = capturedFinalLerpSpeed;
                finalLerpOverrideApplied = false;
            }
        }

        internal void LogFeatureInfo(string message)
        {
            Logger.LogInfo(message);
        }

        internal void LogFeatureError(string message)
        {
            Logger.LogError(message);
        }

        internal bool TryGetTradeWindOverride(out Vector3 result)
        {
            result = Vector3.zero;

            if (ClimateCustomWindsActive())
            {
                return false;
            }

            if (!TradeWindEnabled)
            {
                return true;
            }

            if (!TradeWindCycleEnabled)
            {
                return false;
            }

            if (!TryGetPlayerCoordinates(
                    out float longitude,
                    out float latitude))
            {
                return false;
            }

            SimpleTradeWindArea area = GetSimpleTradeWindArea(
                longitude,
                latitude);
            if (area == SimpleTradeWindArea.None)
            {
                return false;
            }

            float absoluteDay = GetCurrentAbsoluteDay();
            switch (area)
            {
                case SimpleTradeWindArea.MidLatitude:
                    result = TradeWindCycle.GetMidLatitudeDirection(
                        absoluteDay);
                    break;

                case SimpleTradeWindArea.UpperMidLatitude:
                    result = TradeWindCycle.GetUpperMidLatitudeDirection(
                        absoluteDay);
                    break;

                default:
                    result = TradeWindCycle.GetNorthernDirection(
                        absoluteDay);
                    break;
            }
            return true;
        }

        private static SimpleTradeWindArea GetSimpleTradeWindArea(
            float longitude,
            float latitude)
        {
            if (latitude > MidCycleNorthLatitude)
            {
                return SimpleTradeWindArea.Northern;
            }

            if (latitude < MidCycleSouthLatitude ||
                longitude > MidCycleEastLongitudeLimit)
            {
                return SimpleTradeWindArea.None;
            }

            return latitude >= MidCycleCalmLatitude
                ? SimpleTradeWindArea.UpperMidLatitude
                : SimpleTradeWindArea.MidLatitude;
        }

        private static bool TryGetPlayerCoordinates(
            out float longitude,
            out float latitude)
        {
            longitude = 0f;
            latitude = 0f;
            if (FloatingOriginManager.instance == null ||
                Refs.observerMirror == null)
            {
                return false;
            }

            Vector3 coordinates =
                FloatingOriginManager.instance.GetGlobeCoords(
                    Refs.observerMirror.transform);
            longitude = coordinates.x;
            latitude = coordinates.z;
            return true;
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
            if (!LandDistance.IsKnown(distance))
            {
                return 0f;
            }

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

        internal static float GetClimateOceanLerp(
            float originalMinimum,
            float originalMaximum,
            float distance)
        {
            return LandDistance.IsKnown(distance)
                ? Mathf.InverseLerp(originalMinimum, originalMaximum, distance)
                : 0f;
        }

        internal static float GetConfiguredBonusCap()
        {
            return Instance == null
                ? DefaultCombinedBonusCap
                : Instance.combinedBonusCap.Value;
        }

        internal static bool ClimateCustomWindsActive()
        {
            if (!GameState.playing)
            {
                return false;
            }

            // Every plugin has loaded by the time the game is playing.
            if (!climateLookedUp)
            {
                climateLookedUp = true;
                if (Chainloader.PluginInfos.TryGetValue(ClimatePluginGuid, out PluginInfo climate) &&
                    climate.Instance != null)
                {
                    climate.Instance.Config.TryGetEntry(
                        "Settings",
                        "Enable Custom Winds",
                        out climateCustomWinds);
                }
            }

            return climateCustomWinds != null && climateCustomWinds.Value;
        }
    }

    [HarmonyPatch(typeof(Wind), "GetCurrentTradeWind")]
    internal static class GetCurrentTradeWindPatch
    {
        // Set in a postfix: the prefixes of Border Expander and Climate that
        // replace the game's trade wind run whatever another prefix returns.
        [HarmonyPostfix]
        [HarmonyAfter(new[] { ChaoticWindPlugin.BorderExpanderGuid })]
        private static void Postfix(ref Vector3 __result)
        {
            ChaoticWindPlugin plugin = ChaoticWindPlugin.Instance;
            if (plugin != null &&
                plugin.TryGetTradeWindOverride(out Vector3 result))
            {
                __result = result;
            }
        }
    }

    [HarmonyPatch(typeof(SaveLoadManager), "LoadGame")]
    internal static class LoadGamePatch
    {
        [HarmonyPrefix]
        private static void Prefix()
        {
            ChaoticWindPlugin.BeginLandDistanceWait();
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
            ChaoticWindPlugin.Instance?.CompleteRequestedWindRetarget(
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

    internal static class TradeWindCycle
    {
        internal const float PhaseLengthDays = 20f;
        internal const float TransitionLengthDays = 1f;
        internal const int TransitionRetargetSteps = 20;

        private static readonly Vector3 NorthEast =
            new Vector3(0.75f, 0f, 0.75f).normalized;
        private static readonly Vector3 SouthWest =
            new Vector3(-1f, 0f, -0.5f).normalized;
        private static readonly Vector3 EastNorthEast =
            new Vector3(1f, 0f, 0.5f).normalized;
        private static readonly Vector3 EastSouthEast =
            new Vector3(1f, 0f, -0.5f).normalized;

        internal static Vector3 GetMidLatitudeDirection(float absoluteDay)
        {
            return GetAlternatingDirection(
                absoluteDay,
                NorthEast,
                SouthWest);
        }

        internal static Vector3 GetNorthernDirection(float absoluteDay)
        {
            return GetAlternatingDirection(
                absoluteDay,
                EastNorthEast,
                EastSouthEast);
        }

        internal static Vector3 GetUpperMidLatitudeDirection(float absoluteDay)
        {
            absoluteDay = Mathf.Max(0f, absoluteDay);

            if (absoluteDay < PhaseLengthDays)
            {
                return NorthEast;
            }

            int phase = Mathf.FloorToInt(absoluteDay / PhaseLengthDays);
            float dayWithinPhase = absoluteDay - phase * PhaseLengthDays;
            bool targetIsNorthEast = phase % 2 == 0;
            Vector3 target = targetIsNorthEast
                ? NorthEast
                : Vector3.zero;

            if (dayWithinPhase >= TransitionLengthDays)
            {
                return target;
            }

            Vector3 previous = targetIsNorthEast
                ? Vector3.zero
                : NorthEast;
            float transition = Mathf.SmoothStep(
                0f,
                1f,
                dayWithinPhase / TransitionLengthDays);
            return Vector3.Lerp(previous, target, transition);
        }

        private static Vector3 GetAlternatingDirection(
            float absoluteDay,
            Vector3 initialDirection,
            Vector3 alternateDirection)
        {
            absoluteDay = Mathf.Max(0f, absoluteDay);

            // The initial direction is dominant from days 0 through 19.
            if (absoluteDay < PhaseLengthDays)
            {
                return initialDirection;
            }

            int phase = Mathf.FloorToInt(absoluteDay / PhaseLengthDays);
            float dayWithinPhase = absoluteDay - phase * PhaseLengthDays;
            bool targetIsInitialDirection = phase % 2 == 0;

            Vector3 target = targetIsInitialDirection
                ? initialDirection
                : alternateDirection;
            if (dayWithinPhase >= TransitionLengthDays)
            {
                return target;
            }

            Vector3 previous = targetIsInitialDirection
                ? alternateDirection
                : initialDirection;
            float transition = Mathf.SmoothStep(
                0f,
                1f,
                dayWithinPhase / TransitionLengthDays);
            return Vector3.Slerp(previous, target, transition).normalized;
        }

        internal static int GetRetargetSample(float absoluteDay)
        {
            absoluteDay = Mathf.Max(0f, absoluteDay);

            if (absoluteDay < PhaseLengthDays)
            {
                return TransitionRetargetSteps;
            }

            int phase = Mathf.FloorToInt(absoluteDay / PhaseLengthDays);
            float dayWithinPhase = absoluteDay - phase * PhaseLengthDays;
            int state = TransitionRetargetSteps;

            if (dayWithinPhase < TransitionLengthDays)
            {
                float progress = Mathf.Clamp01(
                    dayWithinPhase / TransitionLengthDays);
                state = Mathf.Min(
                    Mathf.FloorToInt(progress * TransitionRetargetSteps),
                    TransitionRetargetSteps - 1);
            }

            return phase * (TransitionRetargetSteps + 1) + state;
        }
    }

    // GameState.distanceToLand is not always the player's distance:
    // SaveLoadManager.LoadGame ends by setting 999999, IslandDistanceTracker
    // starts each search at 100000000, and a search begun before the observer
    // follows the loaded player measures from the start menu's place. Read as
    // the player's, they add or drop the open-ocean wind bonus right after a
    // load.
    internal static class LandDistance
    {
        internal const float PlaceholderMinimum = 999999f;
        internal const float OceanBonusStart = 1500f;

        // What the wind still waits for after a load before it reads the
        // distance again.
        internal static LandDistanceWait Wait;

        internal static bool IsKnown(float distance)
        {
            return Wait == LandDistanceWait.None && distance < PlaceholderMinimum;
        }

        internal static bool HasOceanBonus(float distance)
        {
            return distance > OceanBonusStart;
        }

        // One frame of the wait. playerPlaced: the game is playing and the
        // observer follows the player. trackerBusy: IslandDistanceTracker is
        // in a search. The wait ends with the first search begun after the
        // frame the player was placed in.
        internal static LandDistanceWait Advance(
            LandDistanceWait wait,
            bool playerPlaced,
            bool trackerBusy)
        {
            switch (wait)
            {
                case LandDistanceWait.Player:
                    return playerPlaced ? LandDistanceWait.TrackerIdle : wait;

                case LandDistanceWait.TrackerIdle:
                    return trackerBusy ? wait : LandDistanceWait.SearchStart;

                case LandDistanceWait.SearchStart:
                    return trackerBusy ? LandDistanceWait.SearchEnd : wait;

                case LandDistanceWait.SearchEnd:
                    return trackerBusy ? wait : LandDistanceWait.None;

                default:
                    return LandDistanceWait.None;
            }
        }
    }

    internal enum LandDistanceWait
    {
        None,
        Player,
        TrackerIdle,
        SearchStart,
        SearchEnd
    }

    internal static class ClimateCompatibility
    {
        internal static void Install(Harmony harmony)
        {
            if (!Chainloader.PluginInfos.TryGetValue(
                    ChaoticWindPlugin.ClimatePluginGuid,
                    out PluginInfo climate) ||
                climate.Instance == null)
            {
                return;
            }

            Type patchType = AccessTools.TypeByName(
                "Climate.WeatherPatches+ReplaceWindPatches");
            MethodInfo target = AccessTools.Method(patchType, "SetNewWindTarget");
            if (target == null)
            {
                ChaoticWindPlugin.Instance?.LogFeatureError(
                    "Climate wind prefix was not found; Climate bonus-cap override and land-distance guard are unavailable.");
                return;
            }

            try
            {
                harmony.Patch(
                    target,
                    transpiler: new HarmonyMethod(
                        typeof(ClimateCompatibility),
                        nameof(Transpiler))
                    {
                        priority = Priority.Last,
                    });
                ChaoticWindPlugin.Instance?.LogFeatureInfo(
                    "Installed Climate custom-wind bonus-cap and land-distance compatibility.");
            }
            catch (Exception exception)
            {
                ChaoticWindPlugin.Instance?.LogFeatureError(
                    "Climate wind compatibility could not be installed: " + exception);
            }
        }

        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            return WindIlRewriter.RewriteClimate(instructions);
        }
    }

    internal static class WindIlRewriter
    {
        private static readonly MethodInfo InverseLerp = AccessTools.Method(
            typeof(Mathf),
            nameof(Mathf.InverseLerp),
            new[] { typeof(float), typeof(float), typeof(float) });

        private static readonly FieldInfo DistanceToLand = AccessTools.Field(
            typeof(GameState),
            nameof(GameState.distanceToLand));

        private static readonly MethodInfo OceanLerpHelper = AccessTools.Method(
            typeof(ChaoticWindPlugin),
            nameof(ChaoticWindPlugin.GetConfiguredOceanLerp));

        private static readonly MethodInfo ClimateOceanLerpHelper = AccessTools.Method(
            typeof(ChaoticWindPlugin),
            nameof(ChaoticWindPlugin.GetClimateOceanLerp));

        private static readonly MethodInfo BonusCapHelper = AccessTools.Method(
            typeof(ChaoticWindPlugin),
            nameof(ChaoticWindPlugin.GetConfiguredBonusCap));

        internal static IEnumerable<CodeInstruction> RewriteVanilla(
            IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);
            List<int> oceanCalls = FindInverseLerpCalls(
                code,
                1500f,
                4000f,
                DistanceToLand);
            List<int> capLoads = FindFloatLoads(code, 20f);

            if (oceanCalls.Count != 1 || capLoads.Count != 2)
            {
                LogPatternError(
                    "vanilla Wind.SetNewWindTarget",
                    oceanCalls.Count,
                    capLoads.Count,
                    "The method was left unchanged.");
                return code;
            }

            code[oceanCalls[0]].operand = OceanLerpHelper;
            ReplaceLoadsWithCall(code, capLoads, BonusCapHelper);
            return code;
        }

        internal static IEnumerable<CodeInstruction> RewriteClimate(
            IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);
            List<int> oceanCalls = FindInverseLerpCalls(
                code,
                1500f,
                4000f,
                DistanceToLand);
            List<int> capLoads = FindFloatLoads(code, 20f);

            // Climate 1.5 caps with Mathf.Min (one literal), Climate 1.4 with a
            // comparison and an assignment (two).
            bool oceanMatches = oceanCalls.Count == 1;
            bool capMatches = capLoads.Count == 1 || capLoads.Count == 2;
            if (!oceanMatches || !capMatches)
            {
                LogPatternError(
                    "Climate custom-wind prefix",
                    oceanCalls.Count,
                    capLoads.Count,
                    "Only the matching parts were changed.");
            }

            if (oceanMatches)
            {
                code[oceanCalls[0]].operand = ClimateOceanLerpHelper;
            }

            if (capMatches)
            {
                ReplaceLoadsWithCall(code, capLoads, BonusCapHelper);
            }

            return code;
        }

        private static List<int> FindInverseLerpCalls(
            List<CodeInstruction> code,
            float minimum,
            float maximum,
            FieldInfo valueField)
        {
            List<int> matches = new List<int>();
            for (int i = 3; i < code.Count; i++)
            {
                if (Equals(code[i].operand, InverseLerp) &&
                    LoadsFloat(code[i - 3], minimum) &&
                    LoadsFloat(code[i - 2], maximum) &&
                    code[i - 1].opcode == OpCodes.Ldsfld &&
                    Equals(code[i - 1].operand, valueField))
                {
                    matches.Add(i);
                }
            }

            return matches;
        }

        private static List<int> FindFloatLoads(
            List<CodeInstruction> code,
            float value)
        {
            List<int> matches = new List<int>();
            for (int i = 0; i < code.Count; i++)
            {
                if (LoadsFloat(code[i], value))
                {
                    matches.Add(i);
                }
            }

            return matches;
        }

        private static void ReplaceLoadsWithCall(
            List<CodeInstruction> code,
            List<int> indexes,
            MethodInfo helper)
        {
            for (int i = 0; i < indexes.Count; i++)
            {
                int index = indexes[i];
                CodeInstruction original = code[index];
                CodeInstruction replacement = new CodeInstruction(OpCodes.Call, helper);
                replacement.labels.AddRange(original.labels);
                replacement.blocks.AddRange(original.blocks);
                code[index] = replacement;
            }
        }

        private static bool LoadsFloat(CodeInstruction instruction, float value)
        {
            return instruction.opcode == OpCodes.Ldc_R4 &&
                instruction.operand is float loadedValue &&
                loadedValue == value;
        }

        private static void LogPatternError(
            string target,
            int oceanCount,
            int capCount,
            string outcome)
        {
            ChaoticWindPlugin.Instance?.LogFeatureError(
                $"Wind IL pattern mismatch in {target}, ocean curves={oceanCount}, cap loads={capCount}. {outcome}");
        }
    }
}
