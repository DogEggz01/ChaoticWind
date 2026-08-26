using System;
using System.Collections.Generic;
using UnityEngine;

namespace ChaoticWind
{
    internal sealed class ModStormController : MonoBehaviour
    {
        private const float ActiveRegionCheckDistance = 12000f;

        internal WanderingStorm Storm { get; private set; }
        internal ModStormKind Kind { get; private set; }
        internal int Priority { get; private set; }
        internal float MoveSpeed { get; private set; }
        internal float FixedWeatherRange { get; private set; }
        internal bool UsesFixedWeatherRange { get; private set; }
        internal float LightningSpawnExtent { get; private set; }
        internal float ThunderAudioMaxDistance { get; private set; }

        private ParticleSystem topParticles;
        private ParticleSystem bottomParticles;
        private Transform playerTransform;
        private float oneSecondTimer;

        internal void Configure(
            StormDefinition definition,
            Transform playerTransform)
        {
            Storm = GetComponent<WanderingStorm>();
            Kind = definition.Kind;
            Priority = definition.Priority;
            MoveSpeed = definition.MoveSpeed;
            UsesFixedWeatherRange = definition.UsesFixedWeatherRange;
            FixedWeatherRange = definition.FixedWeatherRange;
            LightningSpawnExtent = definition.LightningSpawnExtent;
            ThunderAudioMaxDistance = definition.ThunderAudioMaxDistance;
            this.playerTransform = playerTransform;

            StormAccess.Priority(Storm) = definition.Priority;
            StormAccess.Radius(Storm) = definition.Radius;
            StormAccess.ParticleDistance(Storm) = definition.ParticleDistance;
            topParticles = StormAccess.TopParticles(Storm);
            bottomParticles = StormAccess.BottomParticles(Storm);
            ConfigureLightning(
                definition.CloseThunderDistance,
                definition.ThunderAudioMaxDistance,
                definition.LightningRange);
            oneSecondTimer = 0f;
        }

        private void ConfigureLightning(
            float closeThunderDistance,
            float audioMaxDistance,
            float lightRange)
        {
            WanderingStormLightning[] lightningEffects =
                GetComponentsInChildren<WanderingStormLightning>(true);

            for (int i = 0; i < lightningEffects.Length; i++)
            {
                WanderingStormLightning lightning = lightningEffects[i];
                StormAccess.CloseThunderDistance(lightning) = closeThunderDistance;

                Light light = lightning.GetComponent<Light>();
                if (light != null)
                {
                    light.range = lightRange;
                }

                AudioSource[] audioSources = lightning.GetComponents<AudioSource>();
                for (int j = 0; j < audioSources.Length; j++)
                {
                    audioSources[j].maxDistance = audioMaxDistance;
                }
            }
        }

        internal void CustomUpdate()
        {
            if (!GameState.playing)
            {
                return;
            }

            if (playerTransform == null)
            {
                Camera camera = Camera.main;
                if (camera == null)
                {
                    return;
                }

                playerTransform = camera.transform;
            }

            transform.Translate(
                Wind.currentWind.normalized * Time.deltaTime * MoveSpeed,
                Space.World);

            Vector3 fromPlayer = transform.position - playerTransform.position;
            fromPlayer.y = 0f;
            transform.Translate(
                fromPlayer.normalized * Time.deltaTime *
                WeatherStorms.totemAttraction * Storm.totemMult,
                Space.World);

            if (oneSecondTimer <= 0f)
            {
                oneSecondTimer = 1f;
                float distance = Vector3.Distance(
                    transform.position,
                    playerTransform.position);

                if (distance > ActiveRegionCheckDistance &&
                    Weather.instance != null &&
                    Weather.instance.currentRegion != null)
                {
                    Storm.active = StormInfluenceService.IsCustomStormEnabled(Kind) &&
                        Weather.instance.currentRegion.stormCount >= Priority;
                }

                if (distance > ChaoticWindPlugin.GetConfiguredStormRelocationDistance())
                {
                    Vector3 shift = playerTransform.position - transform.position;
                    shift.y = 0f;
                    transform.Translate(shift * 1.75f, Space.World);
                }

                bool emit = StormInfluenceService.IsCustomStormEnabled(Kind) &&
                    Storm.active &&
                    distance <= StormAccess.ParticleDistance(Storm);
                SetEmission(topParticles, emit);
                SetEmission(bottomParticles, emit);
            }

            oneSecondTimer -= Time.deltaTime;
        }

        internal void SetModEnabled(bool enabled)
        {
            bool activeInRegion = enabled &&
                Weather.instance != null &&
                Weather.instance.currentRegion != null &&
                Weather.instance.currentRegion.stormCount >= Priority;
            Storm.active = activeInRegion;
            SetEmission(topParticles, false);
            SetEmission(bottomParticles, false);
            gameObject.SetActive(enabled);
            oneSecondTimer = 0f;
        }

        private static void SetEmission(ParticleSystem particles, bool enabled)
        {
            if (particles == null)
            {
                return;
            }

            ParticleSystem.EmissionModule emission = particles.emission;
            emission.enabled = enabled;
        }
    }

    internal readonly struct StormDefinition
    {
        internal readonly string Name;
        internal readonly ModStormKind Kind;
        internal readonly int Priority;
        internal readonly float Radius;
        internal readonly float ParticleDistance;
        internal readonly float MoveSpeed;
        internal readonly bool UsesFixedWeatherRange;
        internal readonly float FixedWeatherRange;
        internal readonly float LightningSpawnExtent;
        internal readonly float CloseThunderDistance;
        internal readonly float ThunderAudioMaxDistance;
        internal readonly float LightningRange;

        internal StormDefinition(
            string name,
            ModStormKind kind,
            int priority,
            float radius,
            float particleDistance,
            float moveSpeed,
            bool usesFixedWeatherRange,
            float fixedWeatherRange,
            float lightningSpawnExtent,
            float closeThunderDistance,
            float thunderAudioMaxDistance,
            float lightningRange)
        {
            Name = name;
            Kind = kind;
            Priority = priority;
            Radius = radius;
            ParticleDistance = particleDistance;
            MoveSpeed = moveSpeed;
            UsesFixedWeatherRange = usesFixedWeatherRange;
            FixedWeatherRange = fixedWeatherRange;
            LightningSpawnExtent = lightningSpawnExtent;
            CloseThunderDistance = closeThunderDistance;
            ThunderAudioMaxDistance = thunderAudioMaxDistance;
            LightningRange = lightningRange;
        }
    }

    internal static class ModStormFactory
    {
        private static readonly StormDefinition[] Definitions =
        {
            new StormDefinition(
                "Squall 1", ModStormKind.Squall, 1,
                1350f, 1350f, 24f, true, 450f,
                1000f, 1800f, 3600f, 1800f),
            new StormDefinition(
                "Squall 2", ModStormKind.Squall, 2,
                1350f, 1350f, 24f, true, 450f,
                1000f, 1800f, 3600f, 1800f),
            new StormDefinition(
                "Squall 3", ModStormKind.Squall, 3,
                1350f, 1350f, 24f, true, 450f,
                1000f, 1800f, 3600f, 1800f),
            new StormDefinition(
                "Hurricane", ModStormKind.Hurricane, 3,
                6000f, 9000f, 10f, false, 0f,
                2400f, 3200f, 11000f, 10000f),
        };

        private static readonly List<ModStormController> CustomStorms =
            new List<ModStormController>();

        private static WeatherStorms owner;
        private static WanderingStorm[] originalStorms;
        private static bool initialized;

        internal static ModStormController Hurricane { get; private set; }

        internal static void Initialize(WeatherStorms weatherStorms)
        {
            if (weatherStorms == null)
            {
                return;
            }

            if (initialized && owner == weatherStorms)
            {
                return;
            }

            if (initialized)
            {
                Shutdown();
            }

            WanderingStorm[] vanillaStorms = StormAccess.Storms(weatherStorms);
            WanderingStorm template = FindTemplate(vanillaStorms);
            Camera camera = Camera.main;
            if (vanillaStorms == null || template == null || camera == null)
            {
                ChaoticWindPlugin.Instance?.LogFeatureError(
                    "Custom storms could not initialize because the vanilla storm template or player camera was unavailable.");
                return;
            }

            owner = weatherStorms;
            originalStorms = vanillaStorms;
            bool templateWasActive = template.gameObject.activeSelf;
            template.gameObject.SetActive(false);

            Vector3[] offsets =
            {
                new Vector3(18000f, 0f, 0f),
                new Vector3(-18000f, 0f, 0f),
                new Vector3(0f, 0f, 18000f),
                new Vector3(0f, 0f, -24000f),
            };

            try
            {
                for (int i = 0; i < Definitions.Length; i++)
                {
                    StormDefinition definition = Definitions[i];
                    GameObject clone = UnityEngine.Object.Instantiate(
                        template.gameObject,
                        camera.transform.position + offsets[i],
                        template.transform.rotation,
                        template.transform.parent);

                    SaveableObject duplicateSaveable = clone.GetComponent<SaveableObject>();
                    if (duplicateSaveable != null)
                    {
                        UnityEngine.Object.DestroyImmediate(duplicateSaveable);
                    }

                    clone.name = "Chaotic Wind " + definition.Name;
                    ModStormController controller = clone.AddComponent<ModStormController>();
                    controller.Configure(definition, camera.transform);
                    CustomStorms.Add(controller);

                    if (definition.Kind == ModStormKind.Hurricane)
                    {
                        Hurricane = controller;
                        HurricanePersistence.ApplyLoadedPosition(controller);
                    }
                }

                WanderingStorm[] combined = new WanderingStorm[
                    vanillaStorms.Length + CustomStorms.Count];
                Array.Copy(vanillaStorms, combined, vanillaStorms.Length);
                for (int i = 0; i < CustomStorms.Count; i++)
                {
                    combined[vanillaStorms.Length + i] = CustomStorms[i].Storm;
                }

                StormAccess.Storms(weatherStorms) = combined;
                initialized = true;
                ApplyEnabledState();
                ChaoticWindPlugin.Instance?.LogFeatureInfo(
                    "Initialized three Squalls and one Hurricane.");
            }
            catch (Exception exception)
            {
                ChaoticWindPlugin.Instance?.LogFeatureError(
                    "Custom storm initialization failed: " + exception);
                Shutdown();
            }
            finally
            {
                if (template != null)
                {
                    template.gameObject.SetActive(templateWasActive);
                }
            }
        }

        internal static void ApplyEnabledState()
        {
            for (int i = 0; i < CustomStorms.Count; i++)
            {
                ModStormController storm = CustomStorms[i];
                if (storm != null)
                {
                    storm.SetModEnabled(
                        StormInfluenceService.IsCustomStormEnabled(storm.Kind));
                }
            }
        }

        internal static void Shutdown()
        {
            if (owner != null && originalStorms != null)
            {
                StormAccess.Storms(owner) = originalStorms;
            }

            for (int i = 0; i < CustomStorms.Count; i++)
            {
                ModStormController storm = CustomStorms[i];
                if (storm != null)
                {
                    UnityEngine.Object.Destroy(storm.gameObject);
                }
            }

            CustomStorms.Clear();
            Hurricane = null;
            originalStorms = null;
            owner = null;
            initialized = false;
        }

        private static WanderingStorm FindTemplate(WanderingStorm[] storms)
        {
            if (storms == null)
            {
                return null;
            }

            for (int i = 0; i < storms.Length; i++)
            {
                if (storms[i] != null)
                {
                    return storms[i];
                }
            }

            return null;
        }
    }

    internal static class HurricanePersistence
    {
        private const string ModDataKey =
            ChaoticWindPlugin.PluginGuid + ".hurricane-position.v1";

        [Serializable]
        private sealed class SaveData
        {
            public int version = 1;
            public float x;
            public float y;
            public float z;
        }

        private static bool loadCompleted;
        private static bool hasLoadedPosition;
        private static Vector3 loadedPosition;

        internal static void BeginLoad()
        {
            loadCompleted = false;
            hasLoadedPosition = false;
            loadedPosition = default;
        }

        internal static void Save()
        {
            ModStormController hurricane = ModStormFactory.Hurricane;
            if (hurricane == null)
            {
                return;
            }

            if (GameState.modData == null)
            {
                GameState.modData = new Dictionary<string, string>();
            }

            Vector3 position = hurricane.transform.position;
            SaveData data = new SaveData
            {
                x = position.x,
                y = position.y,
                z = position.z,
            };
            GameState.modData[ModDataKey] = JsonUtility.ToJson(data);
        }

        internal static void Load()
        {
            loadCompleted = true;
            hasLoadedPosition = false;

            if (GameState.modData != null &&
                GameState.modData.TryGetValue(ModDataKey, out string json) &&
                !string.IsNullOrEmpty(json))
            {
                try
                {
                    SaveData data = JsonUtility.FromJson<SaveData>(json);
                    if (data != null && data.version == 1 &&
                        IsFinite(data.x) && IsFinite(data.y) && IsFinite(data.z))
                    {
                        loadedPosition = new Vector3(data.x, data.y, data.z);
                        hasLoadedPosition = true;
                    }
                }
                catch (Exception exception)
                {
                    ChaoticWindPlugin.Instance?.LogFeatureWarning(
                        "Ignoring invalid Hurricane save data: " + exception.Message);
                }
            }

            ApplyLoadedPosition(ModStormFactory.Hurricane);
        }

        internal static void ApplyLoadedPosition(ModStormController hurricane)
        {
            if (loadCompleted && hasLoadedPosition && hurricane != null)
            {
                hurricane.transform.position = loadedPosition;
            }
        }

        internal static void Reset()
        {
            loadCompleted = false;
            hasLoadedPosition = false;
            loadedPosition = default;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }

    internal static class MediEastStormSetting
    {
        private static Region mediEast;
        private static int originalStormCount;

        internal static void Apply(bool enabled)
        {
            if (mediEast == null)
            {
                Region[] regions = UnityEngine.Object.FindObjectsOfType<Region>();
                for (int i = 0; i < regions.Length; i++)
                {
                    Region region = regions[i];
                    if (region.gameObject.name == "Region Medi East")
                    {
                        mediEast = region;
                        originalStormCount = region.stormCount;
                        break;
                    }
                }
            }

            if (mediEast != null)
            {
                mediEast.stormCount = enabled ? 3 : originalStormCount;
            }
        }
    }
}
