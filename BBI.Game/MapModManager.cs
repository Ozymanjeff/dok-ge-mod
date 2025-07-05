using System;
using System.Collections;
using System.Collections.Generic;
using System.Xml;
using BBI.Core.ComponentModel;
using BBI.Core.Data;
using BBI.Core.Network;
using BBI.Core.Utility;
using BBI.Core.Utility.FixedPoint;
using BBI.Game.Data;
using BBI.Game.Replay;
using BBI.Game.Simulation;
using UnityEngine;
using BBI.Unity.Game.World;
using BBI.Unity.Game.Data;
using BBI.Game.Data.Queries;
using BBI.Game.Events;
using BBI.Unity.Game.UI;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

// Token: 0x02000410 RID: 1040
public static class MapModManager
{
    private static bool enableEnemySensors = false;
    private static bool enableEnemySensorsRead = false; // Whether enemy sensor state has been read yet
    public static bool EnableEnemySensors
    {
        get
        {
            if (!MapModManager.enableEnemySensorsRead)
            {
                MapModManager.enableEnemySensorsRead = true;
                try
                {
                    string managedPath = Path.Combine(Application.dataPath, (Application.platform == RuntimePlatform.OSXPlayer) ? "Resources/Data/Managed" : "Managed");
                    MapModManager.enableEnemySensors = File.ReadAllText(Path.Combine(managedPath, "settings/enemy-sensors.txt")).Trim().ToLower() == "on";
                }
                catch { }
            }
            return MapModManager.enableEnemySensors;
        }
    }

    private static string logName = "ge_mod.log";

    static MapModManager()
    {
        // Only read the zoom value once so you can't change it between /zoom commands
        try
        {
            string managedPath = Path.Combine(Application.dataPath, (Application.platform == RuntimePlatform.OSXPlayer) ? "Resources/Data/Managed" : "Managed");
            zoom = float.Parse(File.ReadAllText(Path.Combine(managedPath, "settings/zoom.txt")));
        }
        catch { }

        ResetLayout();
    }

    private static List<string> sobanUnits = new List<string>{
        "C_Sob_Escort_MP", "C_Sob_Baserunner_MP", "C_Sob_Railgun_MP", "C_Sob_SupportCruiser_MP", "C_Sob_Battlecruiser_MP", "C_Sob_AssaultCruiser_MP",
        "N_ECMField_MP", "C_Sob_NukeSight_MP", "C_Sob_PopcapScanner", "C_Sob_NukeEmitter_MP", "C_Sob_Carrier_MP"
    };

    private static List<string> khaanUnits = new List<string>{
        "K_Sandskimmer_MP", "K_Harvester_MP", "K_Baserunner_MP", "K_ExplodingSkimmer_MP", "K_AssaultShip_MP", "K_MissileShip_MP", "K_AssaultRailgun_MP",
        "K_HeavyRailgun_MP", "K_Interceptor_MP", "K_Bomber_MP", "K_SupportCruiser_MP", "K_ArtilleryCruiser_MP", "K_HonorGuard_MP", "K_Carrier_MP"
    };

    public static DictionaryExtensions.ValueIterator<CommanderID, Commander> GetCommanders()
    {
        return Sim.Instance.CommanderManager.Commanders;
    }

    // Loads the map layout, including wrecks, units, resources, etc.
    public static void LoadMapLayout()
    {
        // Reset map state variables
        MapXml = "";
        LayoutName = "";

        Subsystem.AttributeLoader.PatchOverrideData = "";
        PatchName = "";
        RevealRandomFactions = false;

        if (CustomLayout)
        {
            // Move existing DoK engine objects off map and disable resources
            foreach (Entity entity in Sim.Instance.EntitySystem)
            {
                if (entity.HasComponent(11))
                {
                    entity.GetComponent<Position>(10).Position2D = new Vector2r(Fixed64.FromInt(1000000), Fixed64.FromInt(1000000));
                    entity.GetComponent<Resource>(11).Disabled = true;
                }
                else if (entity.HasComponent(36) || entity.HasComponent(14))
                {
                    entity.GetComponent<Position>(10).Position2D = new Vector2r(Fixed64.FromInt(1000000), Fixed64.FromInt(1000000));
                }
            }

            // Move black box out of view
            GameObject blackBox = GameObject.Find("BlackPolygon");
            if (blackBox != null)
            {
                blackBox.transform.position = new Vector3(1000000.0f, 1000000.0f, 1000000.0f);
            }

            // Spawn resources
            foreach (MapResourceData resource in resources)
            {
                var detectableAttributesData = new DetectableAttributesData
                {
                    m_SetHasBeenSeenBeforeOnSpawn = true,
                };

                string resourceType = resource.type == 0 ? "Resource_CU" : "Resource_RU";

                SceneEntityCreator.CreateResourcePoint(
                    resourceType,
                    resource.position,
                    default(Orientation2),
                    new string[0],
                    new ResourceAttributesData((ResourceType)resource.type, resource.amount, resource.collectors),
                    detectableAttributesData,
                    false,
                    default(ResourcePositionalVariations),
                    false
                );
            }

            // Delete starting units for commanders without starting fleet
            foreach (MapSpawnData spawn in spawns)
            {
                if (spawn.fleet) continue;

                foreach (Commander commander in Sim.Instance.CommanderManager.Commanders)
                {
                    CommanderDirectorAttributes director = Sim.Instance.CommanderManager.GetCommanderDirectorFromID(commander.ID);
                    if (director.PlayerType == PlayerType.AI) continue;

                    int expectedSpawnIndex = (GameMode == TeamSetting.Team) ? (1 - spawn.team) * 3 : spawn.team;
                    if (expectedSpawnIndex + spawn.index == director.SpawnIndex)
                    {
                        foreach (Entity entity in Sim.Instance.EntitySystem.Query().Has(2)) // All units
                        {
                            if (entity.GetComponent<OwningCommander>(5).ID == commander.ID)
                            {
                                entity.GetComponent<Unit>(2).RetireDespawn();
                            }
                        }
                    }
                }
            }

            // Spawn units
            foreach (MapUnitData unit in units)
            {
                foreach (Commander commander in Sim.Instance.CommanderManager.Commanders)
                {
                    CommanderDirectorAttributes director = Sim.Instance.CommanderManager.GetCommanderDirectorFromID(commander.ID);
                    int expectedSpawnIndex = (GameMode == TeamSetting.Team) ? (1 - unit.team) * 3 : unit.team;

                    if (expectedSpawnIndex + unit.index == director.SpawnIndex)
                    {
                        if (commander.CommanderAttributes.Name == "SPECTATOR")
                            continue;

                        if (sobanUnits.Contains(unit.type) && commander.CommanderAttributes.Faction.ID != FactionID.Soban)
                            continue;

                        if (khaanUnits.Contains(unit.type) && commander.CommanderAttributes.Faction.ID != FactionID.Khaaneph)
                            continue;

                        SceneEntityCreator.CreateEntity(unit.type, commander.ID, unit.position, unit.orientation);
                        break;
                    }
                }
            }

            // Spawn wrecks
            foreach (MapWreckData wreck in wrecks)
            {
                var detectableAttributes = new DetectableAttributesData
                {
                    m_SetHasBeenSeenBeforeOnSpawn = true,
                };

                var positions = new ResourcePositionalVariations
                {
                    ModelOrientationEulersDegrees = new Vector3r(
                        Fixed64.FromConstFloat(0.0f),
                        Fixed64.FromConstFloat(wreck.angle),
                        Fixed64.FromConstFloat(0.0f))
                };

                var shape = new ShapeAttributesData
                {
                    m_Radius = 100.0f,
                    m_BlocksLOF = wreck.blockLof,
                    m_BlocksAllHeights = wreck.blockLof,
                };

                var res = new ResourceAttributesData
                {
                    m_ResourceType = ResourceType.Resource3, // Wreck resource type
                };

                SimWreckSectionResourceSpawnableAttributesData[] childResources = new SimWreckSectionResourceSpawnableAttributesData[wreck.resources.Count];
                for (int i = 0; i < wreck.resources.Count; i++)
                {
                    var resource = wreck.resources[i];

                    string entityType = "Resource_CU";
                    if (resource.type == 1) entityType = "Resource_RU";
                    else if (resource.type == 2) entityType = "Artifact_Resource";

                    childResources[i] = new SimWreckSectionResourceSpawnableAttributesData
                    {
                        m_DetectableAttributes = new DetectableAttributesData(),
                        m_OverrideDetectableAttributes = true,
                        m_Tags = new string[0],
                        m_EntityTypeToSpawn = entityType,
                        m_ResourceAttributes = new ResourceAttributesData(
                            (ResourceType)resource.type,
                            resource.amount,
                            resource.collectors),
                        m_OverrideResourceAttributes = true,
                        m_SpawnPositionOffsetFromSectionCenterX = Fixed64.IntValue((resource.position - wreck.position).X),
                        m_SpawnPositionOffsetFromSectionCenterY = Fixed64.IntValue((resource.position - wreck.position).Y),
                        m_UseNonRandomSpawnPositionOffset = true,
                    };
                }

                var wreckData = new SimWreckAttributesData
                {
                    m_WreckSections = new SimWreckSectionAttributesData[]
                    {
                        new SimWreckSectionAttributesData
                        {
                            m_ExplosionChance = 100,
                            m_Health = 1,
                            m_ResourceSpawnables = childResources,
                        }
                    }
                };

                SceneEntityCreator.CreateWreck(
                    "Resource_Wreck_MP",
                    wreck.position,
                    Orientation2.LocalForward,
                    new string[0],
                    wreckData,
                    "",
                    shape,
                    res,
                    detectableAttributes,
                    false,
                    positions,
                    false
                );

                // Register wreck for artifact spawning if flagged
                if (wreck.spawnArtifactOnDestroy)
                {
                    WreckArtifactManager.RegisterWreck(wreck.position);
                }
            }
        }
    }

    // Other methods like Tick, SetMap, ResetLayout, and relevant fields would follow here...

}


	public static void ResetLayout() {
		// Reset custom layout data
		heatPoints = 0;
		resources.Clear();
		wrecks.Clear();
		artifacts.Clear();
		ezs.Clear();
		spawns.Clear();
		units.Clear();
		colliders.Clear();
		overrideBounds = false;

		DisableCarrierNavs = false;
		DisableAllBlockers = false;
	}

	public static bool LoadLayout(string mapXml, string sceneName, TeamSetting gameMode, int players) {
		Console.WriteLine("[GE mod] Loading layout...");
		System.IO.File.WriteAllText(logName, "Loading layout...");
		// Loading map layout from XML
		try {
			XmlTextReader xmlDokmapReader = new XmlTextReader(new System.IO.StringReader(mapXml));
			while (xmlDokmapReader.Read()) {
				if (xmlDokmapReader.NodeType == XmlNodeType.Element) {
					switch (xmlDokmapReader.Name) {
						default:
							System.IO.File.AppendAllText(logName, string.Format("[GE mod] WARNING: Unknown tag '{0}'" + Environment.NewLine, xmlDokmapReader.Name));
							Debug.LogWarning(string.Format("[GE mod] WARNING: Unknown tag '{0}'", xmlDokmapReader.Name));
							break;

						case "meta": case "dokmap":
							break;

						case "layout":
							if ((TeamSetting)Enum.Parse(typeof(TeamSetting), xmlDokmapReader.GetAttribute("mode")) == gameMode &&
							     (Regex.Replace(xmlDokmapReader.GetAttribute("map"), @"\s+", "").Contains(sceneName) ||
								 Regex.Replace(xmlDokmapReader.GetAttribute("map"), @"\s+", "").Contains("*")) &&
								 xmlDokmapReader.GetAttribute("players").Contains(players.ToString()[0])) {

									 XmlReader xmlLayoutReader = xmlDokmapReader.ReadSubtree();
									 while (xmlLayoutReader.Read()) {
										if (xmlLayoutReader.NodeType == XmlNodeType.Element) {
											switch (xmlLayoutReader.Name) {
												// Unimplemented but valid elements
												case "layout":
												case "resources": case "artifacts": case "ezs": case "spawns": case "units": case "colliders": case "wrecks":
													break;

												case "resource":
													// collectors is an optional attribute
													int collectors = 2;
													try {
														collectors = int.Parse(xmlLayoutReader.GetAttribute("collectors"));
													} catch {}

													resources.Add(new MapResourceData {
														position = new Vector2r(Fixed64.FromConstFloat(float.Parse(xmlLayoutReader.GetAttribute("x"))), Fixed64.FromConstFloat(float.Parse(xmlLayoutReader.GetAttribute("y")))),
														type = (xmlLayoutReader.GetAttribute("type") == "ru") ? 1 : 0,
														amount = int.Parse(xmlLayoutReader.GetAttribute("amount")),
														collectors = collectors,
													});
													break;

													case "wreck":
														    bool blockLof = false;
														    bool spawnArtifact = false;
														    try
														    {
														        spawnArtifact = Boolean.Parse(xmlLayoutReader.GetAttribute("spawnArtifactOnDestroy"));
														    }
														    catch
														    {
														        spawnArtifact = false; // default if attribute is missing or invalid
														    }
														    try {
														        blockLof = Boolean.Parse(xmlLayoutReader.GetAttribute("blocklof"));
														    } catch {}
														
														    MapWreckData wreck = new MapWreckData {
														        position = new Vector2r(Fixed64.FromConstFloat(float.Parse(xmlLayoutReader.GetAttribute("x"))), Fixed64.FromConstFloat(float.Parse(xmlLayoutReader.GetAttribute("y")))),
														        angle = float.Parse(xmlLayoutReader.GetAttribute("angle")),
														        resources = new List<MapResourceData>(),
														        blockLof = blockLof,
														        spawnArtifactOnDestroy = spawnArtifact,  // <-- assign the bool here!
														    };

													// Read child resources
													XmlReader xmlLayoutReaderWreck = xmlLayoutReader.ReadSubtree();
													while (xmlLayoutReaderWreck.Read()) {
														// collectors is an optional attribute
														if (xmlLayoutReaderWreck.NodeType == XmlNodeType.Element && xmlLayoutReaderWreck.Name == "resource") {
															collectors = 2;
															try {
																collectors = int.Parse(xmlLayoutReaderWreck.GetAttribute("collectors"));
															} catch {}

															wreck.resources.Add(new MapResourceData {
																position = new Vector2r(Fixed64.FromConstFloat(float.Parse(xmlLayoutReaderWreck.GetAttribute("x"))), Fixed64.FromConstFloat(float.Parse(xmlLayoutReaderWreck.GetAttribute("y")))),
																type = (xmlLayoutReader.GetAttribute("type") == "ru") ? 1 : 0,
																amount = int.Parse(xmlLayoutReaderWreck.GetAttribute("amount")),
																collectors = collectors,
															});
														}
													}

													wrecks.Add(wreck);
													break;

												case "artifact":
													artifacts.Add(new MapArtifactData {
														entity = Entity.None,
														position = new Vector2r(Fixed64.FromConstFloat(float.Parse(xmlLayoutReader.GetAttribute("x"))), Fixed64.FromConstFloat(float.Parse(xmlLayoutReader.GetAttribute("y")))),
													});
													break;

												case "ez":
													ezs.Add(new MapEzData {
														team = int.Parse(xmlLayoutReader.GetAttribute("team")),
														position = new Vector2r(Fixed64.FromConstFloat(float.Parse(xmlLayoutReader.GetAttribute("x"))), Fixed64.FromConstFloat(float.Parse(xmlLayoutReader.GetAttribute("y")))),
														radius = float.Parse(xmlLayoutReader.GetAttribute("radius")),
													});
													break;

												case "spawn":
													// Try to get optional index
													int spawnIndex = 0;
													try {
														spawnIndex = int.Parse(xmlLayoutReader.GetAttribute("index"));
													} catch {}

													// Try to get optional camera angle
													float cameraAngle = 0;
													try {
														cameraAngle = float.Parse(xmlLayoutReader.GetAttribute("camera"));
													} catch {}

													// Try to get optional fleet toggle
													bool fleet = true;
													try {
														fleet = Boolean.Parse(xmlLayoutReader.GetAttribute("fleet"));
													} catch {}

													spawns.Add(new MapSpawnData {
														team = int.Parse(xmlLayoutReader.GetAttribute("team")),
														index = spawnIndex,
														position = new Vector3(float.Parse(xmlLayoutReader.GetAttribute("x")), 0.0f, float.Parse(xmlLayoutReader.GetAttribute("y"))),
														angle = float.Parse(xmlLayoutReader.GetAttribute("angle")),
														cameraAngle = cameraAngle,
														fleet = fleet,
													});
													break;

												case "unit":
													// Try to get optional index
													int unitIndex = 0;
													try {
														unitIndex = int.Parse(xmlLayoutReader.GetAttribute("index"));
													} catch {}

													units.Add(new MapUnitData {
														team = int.Parse(xmlLayoutReader.GetAttribute("team")),
														index = unitIndex,
														type = xmlLayoutReader.GetAttribute("type"),
														position = new Vector2r(Fixed64.FromConstFloat(float.Parse(xmlLayoutReader.GetAttribute("x"))), Fixed64.FromConstFloat(float.Parse(xmlLayoutReader.GetAttribute("y")))),
														orientation = Orientation2.FromDirection(Vector2r.Rotate(new Vector2r(Fixed64.FromConstFloat(0.0f), Fixed64.FromConstFloat(1.0f)), Fixed64.FromConstFloat(float.Parse(xmlLayoutReader.GetAttribute("angle")) / 180.0f * -3.14159f))),
													});
													break;

												case "heat":
													heatPoints = int.Parse(xmlLayoutReader.ReadInnerXml());
													break;

												case "bounds":
													overrideBounds = true;
													boundsMax = new Vector2r(Fixed64.FromConstFloat(float.Parse(xmlLayoutReader.GetAttribute("right"))), Fixed64.FromConstFloat(float.Parse(xmlLayoutReader.GetAttribute("top"))));
													boundsMin = new Vector2r(Fixed64.FromConstFloat(float.Parse(xmlLayoutReader.GetAttribute("left"))), Fixed64.FromConstFloat(float.Parse(xmlLayoutReader.GetAttribute("bottom"))));
													break;

												case "blocker":
													// Parse vertices
													List<Vector2r> vertices = new List<Vector2r>();
													foreach (string vert in xmlLayoutReader.GetAttribute("verts").Split(';')) {
														float x = float.Parse(vert.Split(',')[0]);
														float z = float.Parse(vert.Split(',')[1]);
														vertices.Add(new Vector2r(Fixed64.FromConstFloat(x), Fixed64.FromConstFloat(z)));
													}

													ConvexPolygon collider = ConvexPolygon.FromPointCloud(vertices);
													UnitClass mask = (UnitClass)Enum.Parse(typeof(UnitClass), xmlLayoutReader.GetAttribute("class"));
													collider.SetLayerFlag((uint)mask);
													bool blockAllHeights = true;
													try {
														blockAllHeights = Boolean.Parse(xmlLayoutReader.GetAttribute("blockallheights"));
													} catch {
														blockAllHeights = Boolean.Parse(xmlLayoutReader.GetAttribute("blocklof"));
													}
													colliders.Add(new MapColliderData {
														collider = collider,
														mask = mask,
														blockLof = Boolean.Parse(xmlLayoutReader.GetAttribute("blocklof")),
														blockAllHeights = blockAllHeights,
													});
													break;

												case "blockers":
													try {
														DisableCarrierNavs = !Boolean.Parse(xmlLayoutReader.GetAttribute("carrier"));
													} catch {}
													try {
														DisableAllBlockers = !Boolean.Parse(xmlLayoutReader.GetAttribute("existing"));
													} catch {}
													break;

												default:
													System.IO.File.AppendAllText(logName, string.Format("[GE mod] WARNING: Unknown tag '{0}'" + Environment.NewLine, xmlLayoutReader.Name));
													Debug.LogWarning(string.Format("[GE mod] WARNING: Unknown tag '{0}'", xmlLayoutReader.Name));
													break;
											}
										}
									}

									return true;
								}
							break;
							}
						}
					}

					return false;
		} catch (Exception e) {
			System.IO.File.AppendAllText(logName, string.Format("[GE mod] ERROR: parsing layout: {0}" + Environment.NewLine, e));
			Debug.LogWarning(string.Format("[GE mod] ERROR: parsing layout: {0}", e));
			System.Diagnostics.Process.Start(logName);
			ResetLayout();
			MapXml = "";
			LayoutName = "";
			return false;
		}
	}

	private static float zoom = 0; // 0 means default

	// Used to get the max camera distance in non sensors
	public static float GetMaxCameraDistance(float def) {
		return def + zoom;
	}

	public static string GetLayoutData() {
		if (!CustomLayout) return "";
		string managedPath = Path.Combine(Application.dataPath, (Application.platform == RuntimePlatform.OSXPlayer) ? "Resources/Data/Managed" : "Managed");
		// Reading map from file if not downloaded from web
		if (MapXml == "") {
			if (GameType == BBI.Game.Data.GameMode.AISkirmish) { // Only return the ones on disc if the game type is skirmish
				return File.ReadAllText(Path.Combine(managedPath, Path.Combine("maps", maps[LevelDef.SceneName].defaultFile)));
			} else { // Return layouts hard coded into the DLL's for multiplayer to prevent desyncs from modified layouts
				return MapModLayouts.GetDefaultLayout(LevelDef.SceneName);
			}
		}
		return MapXml;
	}

	// All team maps have a dash after them to differentiate from FFA maps
	public static Dictionary<string, string> mapNameOverrides = new Dictionary<string, string>() {
		{"MP_05-Teeth-", "Kalash Teeth [2,2]"},
		{"MP_01-Crater-", "Torin Crater [2,2]"},
		{"MP_14_1v1_Smaller-", "The Boneyard [2,2]"},
		{"MP_17_1v1_2-", "The Shallows [2,2]"},
		{"MP_16_1v1-Firebase-", "Firebase Krill [2,2]"},
		{"MP_16-Firebase-", "Firebase Krill [4,4]"},
		{"MP_14-", "The Boneyard [4,4]"},
		{"MP_07-Output-", "Canyon Outpost [4,4]"},
		{"MP_11-DuneSea-", "Dune Sea [4,4]"},
		{"MP_17_2v2-", "The Shallows [4,4]"},
		{"MP_10-KharToba-", "Khar-Toba [6,6]"},
		{"MP_21-", "Taiidan Passage [4,4]"},
		{"MP_23-", "Gaalsien Territories [2,2]"},
		{"MP_22-", "Kalash Valley [6,6]"},
		{"MP_22_2v2-", "Kalash Valley [4,4]"},
		{"MP_21_1v1-", "Taiidan Passage [2,2]"},
		{"DEBUG_EmptyMap-", "TEST Empty Map"}, // Non-accessible map

		{"MP_05-Teeth-FFA", "FFA - Kalash Teeth [2,2]"},
		{"MP_01-Crater", "FFA - Torin Crater [2,2]"},
		{"MP_07-Outpost-FFA", "FFA - Canyon Outpost [4,4]"},
		{"MP_11-DuneSea-FFA", "FFA - Dune Sea [4,4]"},
		{"MP_10-KharToba-FFA", "FFA - Khar-Toba [6,6]"},
		{"MP_24_FFA", "MP_24_FFA"}, // Non-accessible map
	};

	public static Dictionary<string, MapMetaData> maps = new Dictionary<string, MapMetaData>() {
		{"M01", new MapMetaData {name = "M01", gameMode = TeamSetting.Team, defaultFile = "M01.dokmap", locName = "Epsilon Base [2,4]"}},
		{"M02", new MapMetaData {name = "M02", gameMode = TeamSetting.Team, defaultFile = "M02.dokmap", locName = "Salvage Facility [2,4]"}},
		{"M03", new MapMetaData {name = "M03", gameMode = TeamSetting.Team, defaultFile = "M03.dokmap", locName = "Cape Wrath [2,4]"}},
		{"M04", new MapMetaData {name = "M04", gameMode = TeamSetting.Team, defaultFile = "M04.dokmap", locName = "Kalash Site [2,6]"}},
		{"M05", new MapMetaData {name = "M05", gameMode = TeamSetting.Team, defaultFile = "M05.dokmap", locName = "Kalash Wreck [2,2]"}},
		{"M06", new MapMetaData {name = "M06", gameMode = TeamSetting.Team, defaultFile = "M06.dokmap", locName = "Dune Ocean [2,6]"}},
		{"M07", new MapMetaData {name = "M07", gameMode = TeamSetting.Team, defaultFile = "M07.dokmap", locName = "Gaalsien Base [2,4]"}},
		{"M08", new MapMetaData {name = "M08", gameMode = TeamSetting.Team, defaultFile = "M08.dokmap", locName = "Tombs of the Ancients [2,4]"}},
		{"M09", new MapMetaData {name = "M09", gameMode = TeamSetting.Team, defaultFile = "M09.dokmap", locName = "Whispering Gallery [2,6]"}},
		{"M10", new MapMetaData {name = "M10", gameMode = TeamSetting.Team, defaultFile = "M10.dokmap", locName = "Kashar Approach [2,6]"}},
		{"M11", new MapMetaData {name = "M11", gameMode = TeamSetting.Team, defaultFile = "M11.dokmap", locName = "Kashar Plateau [2,6]"}},
		{"M12", new MapMetaData {name = "M12", gameMode = TeamSetting.Team, defaultFile = "M12.dokmap", locName = "Taiidan Crater [2,6]"}},
		{"M13", new MapMetaData {name = "M13", gameMode = TeamSetting.Team, defaultFile = "M13.dokmap", locName = "Prime Anomaly [2,6]"}},
		{"M13-FFA", new MapMetaData {name = "M13", gameMode = TeamSetting.FFA, defaultFile = "M13-FFA.dokmap", locName = "FFA - Prime Anomaly [2,6]"}},
	}; // List of all added single player maps in the order they will show in the level selector

	public static string[] BannedEntities = new string[] { // Remove these SceneEntity's if the layout is a custom one
		"Resource_CU",
		"Resource_CU_Refined",
		"Resource_RU",
		"Resource_RU_Refined",
		"Resource_Wreck",
		"Resource_Wreck_01",
		"Resource_Wreck_MP",
	};

	using System.Collections.Generic;
// Add appropriate using directives for Entity, Vector2r, SceneEntityCreator, etc.

public static class MapManager
{
    // Mod meta data
    public static readonly object artUiLock = new object();
    public static string ModDescription => "GE mod " + ModVersion;
    public static string ModVersion => "v1.4.4.1";

    // Lobby command memorization
    public static string LayoutName { get; set; } = "";
    public static string PatchName { get; set; } = "";
    public static bool RevealRandomFactions { get; set; } = false;

    // Mod state data
    public static bool CustomLayout { get; private set; }
    public static string MapXml { get; set; } = "";
    public static LevelDefinition LevelDef { get; private set; }
    public static GameMode GameType { get; private set; }
    public static TeamSetting TeamMode { get; private set; } // Renamed from GameMode to avoid duplicate
    public static uint FrameNumber { get; set; }

    // External hooks
    public static ExtractionZoneViewController SExtractionZoneViewController { get; set; }
    public static object SWinConditionPanelController { get; set; }

    // Layout data
    public static List<MapResourceData> resources = new List<MapResourceData>();
    public static List<MapWreckData> wrecks = new List<MapWreckData>();
    public static List<MapArtifactData> artifacts = new List<MapArtifactData>();
    public static List<MapEzData> ezs = new List<MapEzData>();
    public static List<MapSpawnData> spawns = new List<MapSpawnData>();
    public static List<MapUnitData> units = new List<MapUnitData>();
    public static List<MapColliderData> colliders = new List<MapColliderData>();
    public static bool overrideBounds;
    public static Vector2r boundsMin;
    public static Vector2r boundsMax;
    public static int heatPoints;

    public static bool DisableCarrierNavs { get; set; }
    public static bool DisableAllBlockers { get; set; }

    // --- Artifact spawn tracking ---
    private static List<MapArtifactData> activeArtifacts = new List<MapArtifactData>();

    public static void Tick()
    {
        for (int i = 0; i < wrecks.Count; i++)
        {
            MapWreckData wreck = wrecks[i];

            if (IsWreckDestroyed(wreck) && wreck.spawnArtifactOnDestroy)
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

    private static bool IsWreckDestroyed(MapWreckData wreck)
    {
        // TODO: Replace with actual destruction logic
        return true;
    }

    private static bool ArtifactAlreadySpawnedAt(Vector2r position)
    {
        return activeArtifacts.Exists(a => a.position.Equals(position));
    }

    private static Entity SpawnArtifactAt(Vector2r position)
    {
        // TODO: Use actual API call for entity creation
        return SceneEntityCreator.CreateCollectibleEntity("Artifact", CollectibleType.Artifact, position, default(Orientation2));
    }

    // --- Data Structures ---
    public struct MapResourceData
    {
        public Vector2r position;
        public int type; // 0 = CU, 1 = RU
        public int amount;
        public int collectors;
    }

    public struct MapWreckData
    {
        public Vector2r position;
        public float angle;
        public List<MapResourceData> resources;
        public bool blockLof;
        public bool spawnArtifactOnDestroy;
    }

    public struct MapArtifactData
    {
        public Entity entity;
        public Vector2r position;
        public bool NeedsRespawning => entity == Entity.None || entity.GetComponent<Position>(10) == null;
    }

    public struct MapEzData
    {
        public int team;
        public Vector2r position;
        public float radius;
    }

    public struct MapSpawnData
    {
        public int team;
        public int index;
        public Vector3 position;
        public float angle;
        public float cameraAngle;
        public bool fleet;
    }

    public struct MapUnitData
    {
        public int team;
        public int index;
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
        public string name;
        public TeamSetting gameMode;
        public string defaultFile;
        public int idInt;
        public string idStr;
        public string locName;
    }
}
