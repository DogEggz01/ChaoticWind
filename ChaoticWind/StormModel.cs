using HarmonyLib;
using UnityEngine;

namespace ChaoticWind
{
    internal enum ModStormKind
    {
        Squall,
        Hurricane
    }

    internal static class StormAccess
    {
        internal static readonly AccessTools.FieldRef<WeatherStorms, WanderingStorm[]>
            Storms = AccessTools.FieldRefAccess<WeatherStorms, WanderingStorm[]>("storms");

        internal static readonly AccessTools.FieldRef<WeatherStorms, float>
            CurrentStormRange = AccessTools.FieldRefAccess<WeatherStorms, float>("currentStormRange");

        internal static readonly AccessTools.FieldRef<WeatherStorms, float>
            RainBorder = AccessTools.FieldRefAccess<WeatherStorms, float>("rainBorder");

        internal static readonly AccessTools.FieldRef<Wind, float>
            WindTimer = AccessTools.FieldRefAccess<Wind, float>("timer");

        internal static readonly AccessTools.FieldRef<Wind, float>
            GustTimer = AccessTools.FieldRefAccess<Wind, float>("gustTimer");

        internal static readonly AccessTools.FieldRef<WanderingStorm, int>
            Priority = AccessTools.FieldRefAccess<WanderingStorm, int>("stormPriority");

        internal static readonly AccessTools.FieldRef<WanderingStorm, float>
            ParticleDistance = AccessTools.FieldRefAccess<WanderingStorm, float>("particlesDistance");

        internal static readonly AccessTools.FieldRef<WanderingStorm, float>
            Radius = AccessTools.FieldRefAccess<WanderingStorm, float>("stormRadius");

        internal static readonly AccessTools.FieldRef<WanderingStorm, ParticleSystem>
            TopParticles = AccessTools.FieldRefAccess<WanderingStorm, ParticleSystem>("topParticles");

        internal static readonly AccessTools.FieldRef<WanderingStorm, ParticleSystem>
            BottomParticles = AccessTools.FieldRefAccess<WanderingStorm, ParticleSystem>("bottomParticles");

        internal static readonly AccessTools.FieldRef<WanderingStormLightning, float>
            LightningInterval = AccessTools.FieldRefAccess<WanderingStormLightning, float>("lightningInterval");

        internal static readonly AccessTools.FieldRef<WanderingStormLightning, float>
            LightningIntensity = AccessTools.FieldRefAccess<WanderingStormLightning, float>("lightIntensity");

        internal static readonly AccessTools.FieldRef<WanderingStormLightning, float>
            CloseThunderDistance = AccessTools.FieldRefAccess<WanderingStormLightning, float>("closeThunderDistance");

        internal static readonly AccessTools.FieldRef<WanderingStormLightning, Light>
            LightningLight = AccessTools.FieldRefAccess<WanderingStormLightning, Light>("light");

        internal static readonly AccessTools.FieldRef<WanderingStormLightning, ParticleSystem>
            LightningParticles = AccessTools.FieldRefAccess<WanderingStormLightning, ParticleSystem>("particles");

        internal static readonly AccessTools.FieldRef<WanderingStormLightning, AudioSource>
            LightningAudio1 = AccessTools.FieldRefAccess<WanderingStormLightning, AudioSource>("audio1");

        internal static readonly AccessTools.FieldRef<WanderingStormLightning, AudioSource>
            LightningAudio2 = AccessTools.FieldRefAccess<WanderingStormLightning, AudioSource>("audio2");

        internal static readonly AccessTools.FieldRef<WanderingStormLightning, float>
            LightningCooldown = AccessTools.FieldRefAccess<WanderingStormLightning, float>("lightningCooldown");

        internal static readonly AccessTools.FieldRef<WanderingStormLightning, float>
            LightningLightTimer = AccessTools.FieldRefAccess<WanderingStormLightning, float>("lightTimer");
    }

    internal readonly struct StormInfluence
    {
        internal readonly ModStormController ModStorm;
        internal readonly float CenterDistance;
        internal readonly float Radius;
        internal readonly float WeatherRange;
        internal readonly float OuterEdge;
        internal readonly float Lerp;

        internal bool Inside => CenterDistance < OuterEdge;
        internal bool IsSquall => ModStorm != null && ModStorm.Kind == ModStormKind.Squall;
        internal bool IsHurricane => ModStorm != null && ModStorm.Kind == ModStormKind.Hurricane;
        internal float WindLerp => Mathf.InverseLerp(
            OuterEdge,
            Radius * 0.5f,
            CenterDistance);

        internal StormInfluence(
            ModStormController modStorm,
            float centerDistance,
            float radius,
            float weatherRange)
        {
            ModStorm = modStorm;
            CenterDistance = centerDistance;
            Radius = radius;
            WeatherRange = weatherRange;
            OuterEdge = radius + weatherRange;
            Lerp = Mathf.InverseLerp(OuterEdge, radius, centerDistance);
        }
    }

    internal static class StormInfluenceService
    {
        internal static bool GeneralChangesEnabled
        {
            get
            {
                ChaoticWindPlugin plugin = ChaoticWindPlugin.Instance;
                return plugin != null && plugin.GeneralStormChangesEnabled;
            }
        }

        internal static bool IsCustomStormEnabled(ModStormKind kind)
        {
            ChaoticWindPlugin plugin = ChaoticWindPlugin.Instance;
            return plugin != null && plugin.IsCustomStormEnabled(kind);
        }

        private static float GetWeatherRange(
            WeatherStorms weatherStorms,
            ModStormController marker)
        {
            if (marker != null && marker.UsesFixedWeatherRange)
            {
                return marker.FixedWeatherRange;
            }

            return StormAccess.CurrentStormRange(weatherStorms);
        }

        internal static StormInfluence Evaluate(
            WeatherStorms weatherStorms,
            WanderingStorm storm,
            float distance)
        {
            ModStormController marker = storm.GetComponent<ModStormController>();
            float radius = storm.GetRadius();
            float range = GetWeatherRange(weatherStorms, marker);
            return new StormInfluence(
                marker,
                distance,
                radius,
                range);
        }

        internal static bool TryGetCurrent(out StormInfluence influence)
        {
            influence = default;
            WeatherStorms weatherStorms = WeatherStorms.instance;
            WanderingStorm storm = weatherStorms?.GetCurrentStorm();

            if (weatherStorms == null || storm == null || !storm.active)
            {
                return false;
            }

            influence = Evaluate(
                weatherStorms,
                storm,
                WeatherStorms.currentStormDistance);
            return influence.Inside;
        }

        // The signature matches Mathf.InverseLerp so a transpiler can replace
        // the vanilla call without changing the evaluation stack.
        internal static float GetEffectiveWindLerp(
            float originalStart,
            float originalEnd,
            float distance)
        {
            if (!GeneralChangesEnabled)
            {
                return Mathf.InverseLerp(originalStart, originalEnd, distance);
            }

            WeatherStorms weatherStorms = WeatherStorms.instance;
            WanderingStorm storm = weatherStorms?.GetCurrentStorm();
            if (weatherStorms == null || storm == null || !storm.active)
            {
                return 0f;
            }

            StormInfluence influence = Evaluate(weatherStorms, storm, distance);
            if (!influence.Inside)
            {
                return 0f;
            }

            // The game multiplies this result by 26 immediately afterward.
            float windLerp = influence.WindLerp;
            if (influence.IsHurricane)
            {
                return windLerp * (34f / 26f);
            }

            if (influence.IsSquall)
            {
                switch (influence.ModStorm.Priority)
                {
                    case 1:
                        return windLerp * (13f / 26f);
                    case 2:
                        return windLerp;
                    case 3:
                        return windLerp * (19f / 26f);
                }
            }

            return windLerp;
        }

        internal static float NormalizeForSelection(
            WeatherStorms weatherStorms,
            WanderingStorm storm,
            float distance)
        {
            StormInfluence influence = Evaluate(weatherStorms, storm, distance);
            float range = Mathf.Max(0.0001f, influence.WeatherRange);
            return Mathf.Clamp01((distance - influence.Radius) / range);
        }
    }

    internal static class WindOverrideState
    {
        private const float StormFinalLerpSpeed = 1f;
        private const float StormGustInterval = 10f;

        private static Wind trackedWind;
        private static float normalChangeTimer;
        private static float normalGustInterval;
        private static float normalFinalLerpSpeed;
        private static bool overridesApplied;
        private static bool wasInsideSquall;

        internal static void Tick()
        {
            Wind wind = Wind.instance;
            if (wind == null)
            {
                return;
            }

            if (trackedWind != wind)
            {
                RestoreImmediately();
                trackedWind = wind;
                CaptureNormal(wind);
            }

            if (!StormInfluenceService.GeneralChangesEnabled ||
                !StormInfluenceService.TryGetCurrent(out StormInfluence influence))
            {
                RestoreForExit(wind);
                wasInsideSquall = false;
                return;
            }

            if (!overridesApplied)
            {
                CaptureNormal(wind);
                StormAccess.GustTimer(wind) = Mathf.Min(
                    StormAccess.GustTimer(wind),
                    StormGustInterval);
            }

            bool insideSquall = influence.IsSquall;
            if (insideSquall && !wasInsideSquall)
            {
                StormAccess.WindTimer(wind) = 0f;
                StormAccess.GustTimer(wind) = 0f;
            }

            wind.finalLerpSpeed = StormFinalLerpSpeed;
            wind.gustChangeTimer = StormGustInterval;

            ChaoticWindPlugin plugin = ChaoticWindPlugin.Instance;
            float liveNormalChangeTimer = plugin != null
                ? plugin.GetConfiguredWindChangeTimer()
                : normalChangeTimer;
            wind.changeTimer = insideSquall
                ? liveNormalChangeTimer * 0.5f
                : liveNormalChangeTimer;

            overridesApplied = true;
            wasInsideSquall = insideSquall;
        }

        private static void CaptureNormal(Wind wind)
        {
            normalChangeTimer = wind.changeTimer;
            normalGustInterval = wind.gustChangeTimer;
            normalFinalLerpSpeed = wind.finalLerpSpeed;
        }

        private static void RestoreForExit(Wind wind)
        {
            if (!overridesApplied)
            {
                CaptureNormal(wind);
                return;
            }

            ChaoticWindPlugin plugin = ChaoticWindPlugin.Instance;
            wind.changeTimer = plugin != null
                ? plugin.GetConfiguredWindChangeTimer()
                : normalChangeTimer;
            wind.gustChangeTimer = normalGustInterval;
            wind.finalLerpSpeed = plugin != null
                ? plugin.GetConfiguredFinalLerpSpeed(normalFinalLerpSpeed)
                : normalFinalLerpSpeed;

            StormAccess.GustTimer(wind) = Mathf.Min(
                StormAccess.GustTimer(wind),
                normalGustInterval);

            overridesApplied = false;
        }

        internal static void OnGeneralChangesToggle(bool enabled)
        {
            if (!enabled)
            {
                RestoreImmediately();
            }
        }

        internal static void EnforceFinalLerpAuthority(Wind wind)
        {
            if (overridesApplied && trackedWind == wind)
            {
                wind.finalLerpSpeed = StormFinalLerpSpeed;
            }
        }

        internal static void RestoreImmediately()
        {
            if (trackedWind != null && overridesApplied)
            {
                RestoreForExit(trackedWind);
            }

            trackedWind = null;
            overridesApplied = false;
            wasInsideSquall = false;
        }
    }
}
