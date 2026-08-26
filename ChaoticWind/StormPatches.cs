using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace ChaoticWind
{
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
                    "Climate wind prefix was not found; Climate bonus-cap override is unavailable.");
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
                    "Installed Climate custom-wind storm and bonus-cap compatibility.");
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
            return StormWindIlRewriter.RewriteClimate(instructions);
        }
    }

    internal static class StormWindIlRewriter
    {
        private static readonly MethodInfo InverseLerp = AccessTools.Method(
            typeof(Mathf),
            nameof(Mathf.InverseLerp),
            new[] { typeof(float), typeof(float), typeof(float) });

        private static readonly FieldInfo CurrentStormDistance = AccessTools.Field(
            typeof(WeatherStorms),
            nameof(WeatherStorms.currentStormDistance));

        private static readonly FieldInfo DistanceToLand = AccessTools.Field(
            typeof(GameState),
            nameof(GameState.distanceToLand));

        private static readonly MethodInfo StormLerpHelper = AccessTools.Method(
            typeof(StormInfluenceService),
            nameof(StormInfluenceService.GetEffectiveWindLerp));

        private static readonly MethodInfo OceanLerpHelper = AccessTools.Method(
            typeof(ChaoticWindPlugin),
            nameof(ChaoticWindPlugin.GetConfiguredOceanLerp));

        private static readonly MethodInfo BonusCapHelper = AccessTools.Method(
            typeof(ChaoticWindPlugin),
            nameof(ChaoticWindPlugin.GetConfiguredBonusCap));

        internal static IEnumerable<CodeInstruction> RewriteVanilla(
            IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);
            List<int> stormCalls = FindInverseLerpCalls(
                code,
                13000f,
                500f,
                CurrentStormDistance);
            List<int> oceanCalls = FindInverseLerpCalls(
                code,
                1500f,
                4000f,
                DistanceToLand);
            List<int> capLoads = FindFloatLoads(code, 20f);

            if (stormCalls.Count != 1 ||
                oceanCalls.Count != 1 ||
                capLoads.Count != 2)
            {
                LogPatternError(
                    "vanilla Wind.SetNewWindTarget",
                    stormCalls.Count,
                    oceanCalls.Count,
                    capLoads.Count);
                return code;
            }

            code[stormCalls[0]].operand = StormLerpHelper;
            code[oceanCalls[0]].operand = OceanLerpHelper;
            ReplaceLoadsWithCall(code, capLoads, BonusCapHelper);
            return code;
        }

        internal static IEnumerable<CodeInstruction> RewriteClimate(
            IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);
            List<int> stormCalls = FindInverseLerpCalls(
                code,
                13000f,
                500f,
                CurrentStormDistance);
            List<int> capLoads = FindFloatLoads(code, 20f);

            // Climate 1.5 uses Mathf.Min with one literal; Climate 1.4 used a
            // comparison and assignment with two. Both shapes are supported.
            if (stormCalls.Count != 1 ||
                (capLoads.Count != 1 && capLoads.Count != 2))
            {
                LogPatternError(
                    "Climate custom-wind prefix",
                    stormCalls.Count,
                    -1,
                    capLoads.Count);
                return code;
            }

            code[stormCalls[0]].operand = StormLerpHelper;
            ReplaceLoadsWithCall(code, capLoads, BonusCapHelper);
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
            int stormCount,
            int oceanCount,
            int capCount)
        {
            string ocean = oceanCount >= 0
                ? $", ocean curves={oceanCount}"
                : string.Empty;
            ChaoticWindPlugin.Instance?.LogFeatureError(
                $"Wind IL pattern mismatch in {target}: storm curves={stormCount}{ocean}, cap loads={capCount}. The method was left unchanged.");
        }
    }

    [HarmonyPatch(typeof(WeatherStorms), "Start")]
    internal static class WeatherStormsStartPatch
    {
        [HarmonyPostfix]
        private static void Postfix(WeatherStorms __instance)
        {
            ModStormFactory.Initialize(__instance);
        }
    }

    [HarmonyPatch(typeof(WeatherStorms), "FindClosestStorm")]
    internal static class StrongestStormPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(
            WeatherStorms __instance,
            WanderingStorm[] ___storms,
            Transform ___player,
            ref WanderingStorm ___currentStorm)
        {
            if (!StormInfluenceService.GeneralChangesEnabled)
            {
                return true;
            }

            WanderingStorm best = null;
            float bestNormalized = float.MaxValue;
            float bestDistance = float.MaxValue;

            for (int i = 0; i < ___storms.Length; i++)
            {
                WanderingStorm storm = ___storms[i];
                if (storm == null || !storm.active)
                {
                    continue;
                }

                float distance = Vector3.Distance(
                    ___player.position,
                    storm.transform.position);
                float normalized = StormInfluenceService.NormalizeForSelection(
                    __instance,
                    storm,
                    distance);

                if (normalized < bestNormalized ||
                    (Mathf.Approximately(normalized, bestNormalized) &&
                     distance < bestDistance))
                {
                    best = storm;
                    bestNormalized = normalized;
                    bestDistance = distance;
                }
            }

            if (best != null)
            {
                ___currentStorm = best;
                WeatherStorms.currentStormDistance = bestDistance;
            }
            else
            {
                // Keep the serialized fallback reference because ApplyStorm
                // assumes currentStorm is non-null even when no storm is active.
                WeatherStorms.currentStormDistance = 100000000f;
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(WeatherStorms), "GetNormalizedDistance")]
    internal static class CustomStormRangePatch
    {
        [HarmonyPrefix]
        private static bool Prefix(
            WanderingStorm ___currentStorm,
            ref float __result)
        {
            if (___currentStorm == null)
            {
                return true;
            }

            ModStormController marker =
                ___currentStorm.GetComponent<ModStormController>();
            if (marker == null ||
                !StormInfluenceService.IsCustomStormEnabled(marker.Kind) ||
                !marker.UsesFixedWeatherRange)
            {
                return true;
            }

            float radius = ___currentStorm.GetRadius();
            __result = Mathf.Clamp01(
                (WeatherStorms.currentStormDistance - radius) /
                marker.FixedWeatherRange);
            return false;
        }
    }

    [HarmonyPatch(typeof(WanderingStorm), "Update")]
    internal static class CustomStormMovementPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(WanderingStorm __instance)
        {
            ModStormController marker =
                __instance.GetComponent<ModStormController>();
            if (marker == null)
            {
                return true;
            }

            marker.CustomUpdate();
            return false;
        }
    }

    [HarmonyPatch(typeof(Wind), "SetNewGustTarget")]
    internal static class StormGustPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(
            ref Vector3 ___currentGustTarget,
            Vector3 ___currentWindTarget)
        {
            if (!StormInfluenceService.GeneralChangesEnabled ||
                !StormInfluenceService.TryGetCurrent(out _))
            {
                return true;
            }

            ___currentGustTarget = ___currentWindTarget *
                UnityEngine.Random.Range(1f, 1.33f);
            return false;
        }
    }

    [HarmonyPatch(typeof(WanderingStormLightning), "LightningStrike")]
    internal static class CustomStormLightningPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(WanderingStormLightning __instance)
        {
            ModStormController marker =
                __instance.GetComponentInParent<ModStormController>();
            if (marker == null)
            {
                return true;
            }

            if (!StormInfluenceService.IsCustomStormEnabled(marker.Kind))
            {
                return false;
            }

            float lightningInterval = StormAccess.LightningInterval(__instance);
            StormAccess.LightningCooldown(__instance) = UnityEngine.Random.Range(
                lightningInterval,
                lightningInterval * 3f);

            float spawnExtent = marker.LightningSpawnExtent;
            Vector3 strikePosition = new Vector3(
                UnityEngine.Random.Range(-spawnExtent, spawnExtent),
                500f,
                UnityEngine.Random.Range(-spawnExtent, spawnExtent));
            __instance.transform.localPosition = strikePosition;

            float distance = Vector3.Distance(
                __instance.transform.position,
                Refs.observerMirror.transform.position);
            float thunderDelay = Mathf.Max(0f, distance / 340f - 1.47f);

            Light light = StormAccess.LightningLight(__instance);
            ParticleSystem particles = StormAccess.LightningParticles(__instance);
            bool playVisual = light != null && particles != null &&
                distance <= light.range + 500f;
            bool playAudio = distance <= marker.ThunderAudioMaxDistance;
            if (!playVisual && !playAudio)
            {
                return false;
            }

            if (playAudio)
            {
                AudioClip[] thunderClips =
                    distance > StormAccess.CloseThunderDistance(__instance)
                        ? __instance.farThunders
                        : __instance.closeThunders;
                AudioSource audio1 = StormAccess.LightningAudio1(__instance);
                AudioSource audio2 = StormAccess.LightningAudio2(__instance);
                AudioSource audioSource = audio1 != null && !audio1.isPlaying
                    ? audio1
                    : audio2 != null && !audio2.isPlaying
                        ? audio2
                        : null;

                if (audioSource != null &&
                    thunderClips != null &&
                    thunderClips.Length > 0)
                {
                    audioSource.clip = thunderClips[UnityEngine.Random.Range(
                        0,
                        thunderClips.Length - 1)];
                    audioSource.PlayDelayed(thunderDelay);
                }
            }

            if (playVisual)
            {
                light.enabled = true;
                light.intensity = StormAccess.LightningIntensity(__instance);
                StormAccess.LightningLightTimer(__instance) = 0f;
                particles.Play();
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(Weather), "ApplyWeather")]
    [HarmonyAfter(ChaoticWindPlugin.ClimatePluginGuid)]
    internal static class CustomStormRainPatch
    {
        private const float SquallRainIntensity = 50f;
        private const float HurricaneRainIntensity = 15f;

        [HarmonyPostfix]
        private static void Postfix(
            ParticleSystem ___rain,
            ParticleSystem ___outerRain,
            ParticleSystem ___rainSplash)
        {
            WeatherStorms weatherStorms = WeatherStorms.instance;
            if (!StormInfluenceService.TryGetCurrent(out StormInfluence influence) ||
                !TryGetRainTarget(influence, out float rainTarget) ||
                weatherStorms == null ||
                ___rain == null ||
                ___outerRain == null ||
                ___rainSplash == null ||
                Weather.instance?.currentRegion?.rainWeather?.particles == null)
            {
                return;
            }

            float rainBorder = StormAccess.RainBorder(weatherStorms);
            float normalizedDistance = 1f - influence.Lerp;
            if (normalizedDistance > rainBorder)
            {
                return;
            }

            float stormBandLerp = Mathf.InverseLerp(
                rainBorder,
                0f,
                normalizedDistance);
            float regionalRainTarget =
                Weather.instance.currentRegion.rainWeather.particles.rainDensity;
            float rainIntensity = Mathf.Lerp(
                regionalRainTarget,
                rainTarget,
                stormBandLerp);

            ParticleSystem.EmissionModule rainEmission = ___rain.emission;
            rainEmission.rateOverTime = rainIntensity * 75f;
            ParticleSystem.EmissionModule outerRainEmission = ___outerRain.emission;
            outerRainEmission.rateOverTime = rainIntensity * 125f;
            ParticleSystem.EmissionModule splashEmission = ___rainSplash.emission;
            splashEmission.rateOverTime = rainIntensity * 250f;
            GameState.rainIntensity = rainIntensity;
        }

        private static bool TryGetRainTarget(
            StormInfluence influence,
            out float rainTarget)
        {
            rainTarget = 0f;
            if (influence.IsSquall &&
                StormInfluenceService.IsCustomStormEnabled(ModStormKind.Squall))
            {
                rainTarget = SquallRainIntensity;
                return true;
            }

            if (influence.IsHurricane &&
                StormInfluenceService.IsCustomStormEnabled(ModStormKind.Hurricane))
            {
                rainTarget = HurricaneRainIntensity;
                return true;
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(SaveLoadManager), nameof(SaveLoadManager.LoadGame))]
    internal static class HurricaneBeginLoadPatch
    {
        [HarmonyPrefix]
        private static void Prefix()
        {
            HurricanePersistence.BeginLoad();
        }
    }

    [HarmonyPatch(typeof(SaveLoadManager), nameof(SaveLoadManager.SaveModData))]
    internal static class HurricaneSavePatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            HurricanePersistence.Save();
        }
    }

    [HarmonyPatch(typeof(SaveLoadManager), nameof(SaveLoadManager.LoadModData))]
    internal static class HurricaneLoadPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            HurricanePersistence.Load();
        }
    }
}
