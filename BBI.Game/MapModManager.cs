using System;
using System.Collections.Generic;
using System.Linq;
using BBI.Game.Data;
using BBI.Game.Entities;
using BBI.Game.Simulation;

namespace BBI.Game
{
    public static class MapModManager
    {
        // Mod meta data
        public static readonly object artUiLock = new object();
        public static string ModDescription { get { return "GE mod " + ModVersion; } }
        public static string ModVersion { get { return "v1.4.4.1"; } }

        // Lobby command memorization
        public static string LayoutName { get; set; } = "";
        public static string PatchName { get; set; } = "";
        public static bool RevealRandomFactions { get; set; } = false;

        // Mod state data
        public static bool CustomLayout { get; private set; } // Whether this map has a custom layout
        public static string MapXml { get; set; } = ""; // The layout will be read into this or set to override a file based layout
        public static LevelDefinition LevelDef { get; private set; } // The currently running/loading level
        public static GameMode GameType { get; private set; } // The currently running/loading level
        public static TeamSetting GameMode { get; private set; } // The currently running/loading level
        public static uint FrameNumber { get; set; }

        // External hooks
        public static ExtractionZoneViewController SExtractionZoneViewController { get; set; }
        public static object SWinConditionPanelController { get; set; }

        // Layout data
        public static List<MapResourceData> resources = new List<MapResourceData>();
        public static List<MapWreckData> wrecks = new List<MapWreckData>();
        public static List<MapArtifactData> artifacts = new List<MapArtifactData>();
        public static List<MapEzData> ezs = new List<MapEzData>(); // Extraction Zones
        public static List<MapSpawnData> spawns = new List<MapSpawnData>();
        public static List<MapUnitData> units = new List<MapUnitData>();
        public static List<MapColliderData> colliders = new List<MapColliderData>();
        public static bool overrideBounds; // Bounds can only make a level smaller not bigger
        public static Vector2r boundsMin;
        public static Vector2r boundsMax;
        public static int heatPoints; // The ambient heat points of the map

        public static bool DisableCarrierNavs { get; set; }
        public static bool DisableAllBlockers { get; set; }

        // --- Added for artifact spawn tracking ---
        private static readonly List<MapArtifactData> activeArtifacts = new List<MapArtifactData>();

        /// <summary>
        /// Call this once per frame/tick to update wreck destruction and spawn artifacts.
        /// </summary>
        public static void Tick()
        {
            lock (artUiLock)
            {
                // Check wrecks that should spawn artifacts on destruction
                for (int i = 0; i < wrecks.Count; i++)
                {
                    MapWreckData wreck = wrecks[i];

                    if (wreck.spawnArtifactOnDestroy && IsWreckDestroyed(wreck))
                    {
                        if (!ArtifactAlreadySpawnedAt(wreck.position))
                        {
                            Entity artifactEntity = SpawnArtifactAt(wreck.position);
                            activeArtifacts.Add(new MapArtifactData
                            {
                                entity = artifactEntity,
                                position = wreck.position
                            });
                        }
                    }
                }

                // Manage artifact respawns if needed
                for (int i = activeArtifacts.Count - 1; i >= 0; i--)
                {
                    if (activeArtifacts[i].NeedsRespawning)
                    {
                        Entity newArtifact = SpawnArtifactAt(activeArtifacts[i].position);
                        activeArtifacts[i] = new MapArtifactData
                        {
                            entity = newArtifact,
                            position = activeArtifacts[i].position
                        };
                    }
                }
            }
        }

        /// <summary>
        /// Checks if the wreck is destroyed.
        /// Implemented by checking if an entity representing the wreck exists and is alive.
        /// </summary>
        private static bool IsWreckDestroyed(MapWreckData wreck)
        {
            // Example logic: wrecks are represented by entities that we must find in the scene.
            // This method should be adapted to your game's actual wreck entity management.

            // Attempt to find an entity at wreck.position matching wreck criteria.
            // If none exists or health <= 0, consider it destroyed.

            Entity wreckEntity = FindWreckEntityAtPosition(wreck.position);

            if (wreckEntity == Entity.None)
                return true; // No wreck entity means destroyed

            HealthComponent health = wreckEntity.GetComponent<HealthComponent>();
            if (health == null || health.IsDead)
                return true;

            return false;
        }

        /// <summary>
        /// Dummy placeholder to find wreck entity at a position.
        /// Replace with actual lookup from your entity management system.
        /// </summary>
        private static Entity FindWreckEntityAtPosition(Vector2r position)
        {
            // TODO: Implement actual entity lookup using your game engine APIs.

            // This is pseudo-code:
            /*
            foreach(var entity in SceneManager.GetEntities())
            {
                if(entity.Type == WreckType && entity.Position.Equals(position, tolerance))
                {
                    return entity;
                }
            }
            return Entity.None;
            */

            return Entity.None; // Placeholder fallback - assume wreck destroyed
        }

        /// <summary>
        /// Checks if an artifact is already spawned at the given position.
        /// Uses a small tolerance for position comparison to avoid floating-point errors.
        /// </summary>
        private static bool ArtifactAlreadySpawnedAt(Vector2r position)
        {
            const double positionTolerance = 0.01; // Adjust as appropriate

            return activeArtifacts.Exists(a => Vector2r.Distance(a.position, position) < positionTolerance);
        }

        /// <summary>
        /// Spawns an artifact entity at the given position.
        /// Must be implemented using your game engine's API for creating collectibles or artifact entities.
        /// </summary>
        private static Entity SpawnArtifactAt(Vector2r position)
        {
            // TODO: Replace this with your game engine's entity creation call.

            // Example pseudo-code:
            // return SceneEntityCreator.CreateCollectibleEntity("Artifact", CollectibleType.Artifact, position, default(Orientation2));

            // Since no concrete API provided, we return Entity.None as a placeholder
            Console.WriteLine($"Spawning artifact at {position}");
            return Entity.None;
        }

        // Structures

        public struct MapResourceData
        {
            public Vector2r position;
            public int type; // 0 = CU, 1 = RU
            public int amount; // Amount of resources at spawn
            public int collectors; // Max collectors
        }

        public struct MapWreckData
        {
            public Vector2r position;
            public float angle;
            public List<MapResourceData> resources;
            public bool blockLof;
            // New field to mark wrecks that spawn artifacts on destruction
            public bool spawnArtifactOnDestroy;
        }

        public struct MapArtifactData
        {
            public Entity entity;
            public Vector2r position;
            public bool NeedsRespawning
            {
                get
                {
                    return entity == Entity.None || entity.GetComponent<Position>() == null;
                }
            }
        }

        public struct MapEzData
        { // Ez = Extraction Zone
            public int team;
            public Vector2r position;
            public float radius;
        }

        public struct MapSpawnData
        {
            public int team;
            public int index; // Only has to be unique per team
            public Vector3 position;
            public float angle;
            public float cameraAngle;
            public bool fleet; // Whether you start with a starting fleet
        }

        public struct MapUnitData
        {
            public int team;
            public int index; // Only has to be unique per team
            public string type;
            public Vector2r position;
            public Orientation2 orientation;
        }

        public struct MapColliderData
        {
            public ConvexPolygon collider;
            public UnitClass mask;
            public bool blockLof;
            public bool blockAllHeights;
        }

        public struct MapMetaData
        {
            public string name; // The in-game name to use for this map
            public TeamSetting gameMode;
            public string defaultFile; // Default path of map layout file
            public int idInt; // The integer ID of the localized name
            public string idStr; // The ID of the localized name
            public string locName; // The localized name
        }
    }
}
