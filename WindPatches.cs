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
                    "Installed Climate custom-wind bonus-cap compatibility.");
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
                    capLoads.Count);
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
            List<int> capLoads = FindFloatLoads(code, 20f);

            // Climate 1.5 uses Mathf.Min with one literal; Climate 1.4 used a
            // comparison and assignment with two. Both shapes are supported.
            if (capLoads.Count != 1 && capLoads.Count != 2)
            {
                LogPatternError(
                    "Climate custom-wind prefix",
                    -1,
                    capLoads.Count);
                return code;
            }

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
            int oceanCount,
            int capCount)
        {
            string ocean = oceanCount >= 0
                ? $", ocean curves={oceanCount}"
                : string.Empty;
            ChaoticWindPlugin.Instance?.LogFeatureError(
                $"Wind IL pattern mismatch in {target}{ocean}, cap loads={capCount}. The method was left unchanged.");
        }
    }
}
