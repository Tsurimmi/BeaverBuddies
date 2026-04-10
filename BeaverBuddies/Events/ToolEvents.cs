using HarmonyLib;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Timberborn.BaseComponentSystem;
using Timberborn.BlockObjectTools;
using Timberborn.BlockSystem;
using Timberborn.Buildings;
using Timberborn.BuildingTools;
using Timberborn.Coordinates;
using Timberborn.DemolishingUI;
using Timberborn.DuplicationSystem;
using Timberborn.EntitySystem;
using Timberborn.Forestry;
using Timberborn.PlantingUI;
using Timberborn.RootProviders;
using Timberborn.ScienceSystem;
using Timberborn.SingletonSystem;
using Timberborn.TemplateInstantiation;
using Timberborn.TemplateSystem;
using Timberborn.ToolButtonSystem;
using Timberborn.ToolSystem;
using Timberborn.WorkSystemUI;
using UnityEngine;
using UnityEngine.UIElements.Collections;

namespace BeaverBuddies.Events
{

    [Serializable]
    class BuildingPlacedEvent : ReplayEvent
    {
        public string prefabName;
        public Vector3Int coordinates;
        public Orientation orientation;
        public bool isFlipped;
        public string duplicationSourceID;

        public override void Replay(IReplayContext context)
        {
            var buildingSpec = GetBuilding(context, prefabName);
            var blockObjectSpec = buildingSpec.GetSpec<BlockObjectSpec>();
            var placer = context.GetSingleton<BlockObjectPlacerService>().GetMatchingPlacer(blockObjectSpec);
            Placement placement = new Placement(coordinates, orientation,
                isFlipped ? FlipMode.Flipped : FlipMode.Unflipped);

            // Clean conflicting obstacles at each occupied block of the new placement.
            // Handles state divergence where the receiver has stale objects (e.g. a
            // TerrainBlock that wasn't dynamited, a building that wasn't deleted)
            // blocking the placement. Only removes objects that actually conflict on
            // the same occupation bits, so coexisting objects (Path over Platform)
            // are preserved.
            CleanConflictingObstacles(context, blockObjectSpec, placement);

            try
            {
                placer.Place(blockObjectSpec, placement, (targetEntity) => {
                    DuplicateSettingsIfNeeded(context, targetEntity);
                });
            }
            catch (Exception e)
            {
                Plugin.LogWarning($"[ReplayReject] Place failed for {prefabName} at {coordinates}: {e.Message}");
            }
        }

        private void DuplicateSettingsIfNeeded(IReplayContext context, BaseComponent targetEntity)
        {
            if (string.IsNullOrEmpty(duplicationSourceID)) return;

            var sourceEntity = GetEntityComponent(context, duplicationSourceID);
            if (sourceEntity == null) return;

            var duplicator = new Duplicator();
            duplicator.Duplicate(sourceEntity, targetEntity);
        }

        private void CleanConflictingObstacles(IReplayContext context, BlockObjectSpec spec, Placement placement)
        {
            var blockService = context.GetSingleton<IBlockService>();
            var entityService = context.GetSingleton<EntityService>();

            var toDelete = new HashSet<EntityComponent>();
            var intersecting = new List<BlockObject>();

            foreach (var block in spec.GetBlocks(placement))
            {
                if (!block.IsOccupied) continue;

                intersecting.Clear();
                blockService.GetIntersectingObjectsAt(block.Coordinates, block.Occupation, intersecting);
                foreach (var obstacle in intersecting)
                {
                    if (obstacle == null) continue;
                    var ec = obstacle.GetComponent<EntityComponent>();
                    if (ec != null) toDelete.Add(ec);
                }
            }

            if (toDelete.Count == 0) return;

            Plugin.LogWarning($"[ReplayCleanup] Removing {toDelete.Count} conflicting obstacle(s) before placing {prefabName} at {placement.Coordinates}");
            foreach (var entity in toDelete)
            {
                try { entityService.Delete(entity); }
                catch (Exception e) { Plugin.LogWarning($"[ReplayCleanup] Failed to delete obstacle: {e.Message}"); }
            }
        }

        public override string ToActionString()
        {
            return $"Placing {prefabName}, {coordinates}, {orientation}, {isFlipped}";
        }
    }

    [HarmonyPatch(typeof(BlockObjectTool), nameof(BlockObjectTool.Place))]
    class BlockObjectToolPlacePatcher
    {
        public static BaseComponent DuplicationSource { get; set; }

        static void Prefix(BlockObjectTool __instance)
        {
            DuplicationSource = __instance._duplicationSource;
        }

        static void Postfix(BlockObjectTool __instance)
        {
            DuplicationSource = null;
        }
    }


    [HarmonyPatch(typeof(BuildingPlacer), nameof(BuildingPlacer.Place))]
    class PlacePatcher
    {
        static bool Prefix(BlockObjectSpec template, Placement placement, Action<BaseComponent> placedCallback)
        {
            return ReplayEvent.DoPrefix(() =>
            {
                string prefabName = ReplayEvent.GetBuildingName(template);

                return new BuildingPlacedEvent()
                {
                    prefabName = prefabName,
                    coordinates = placement.Coordinates,
                    orientation = placement.Orientation,
                    isFlipped = placement.FlipMode.IsFlipped,
                    duplicationSourceID = ReplayEvent.GetEntityID(BlockObjectToolPlacePatcher.DuplicationSource),
                };
            });
        }
    }

    class BuildingsDeconstructedEvent : ReplayEvent
    {
        public List<string> entityIDs = new List<string>();

        public override void Replay(IReplayContext context)
        {
            var entityService = context.GetSingleton<EntityService>();
            foreach (string entityID in entityIDs)
            {
                var entity = GetEntityComponent(context, entityID);
                if (entity == null) continue;
                entityService.Delete(entity);
            }
        }

        public override string ToActionString()
        {
            return $"Deconstructing: {string.Join(", ", entityIDs)}";
        }
    }

    [HarmonyPatch(typeof(BlockObjectDeletionTool<BuildingSpec>), nameof(BlockObjectDeletionTool<BuildingSpec>.DeleteBlockObjects))]
    class BuildingDeconstructionPatcher
    {
        static bool Prefix(BlockObjectDeletionTool<BuildingSpec> __instance)
        {
            bool result = ReplayEvent.DoPrefix(() =>
            {
                // Filter out null/destroyed components (e.g. paths on platforms
                // that get destroyed when the platform is queued for deletion)
                List<string> entityIDs = __instance._temporaryBlockObjects
                        .Where(obj => obj != null && obj.GetComponent<EntityComponent>() != null)
                        .Select(ReplayEvent.GetEntityID)
                        .Where(id => id != null)
                        .ToList();

                return new BuildingsDeconstructedEvent()
                {
                    entityIDs = entityIDs,
                };
            });

            if (!result)
            {
                // If we cancel the event, clean up the tool
                __instance._temporaryBlockObjects.Clear();
            }

            return result;
        }
    }

    [Serializable]
    class PlantingAreaMarkedEvent : ReplayEvent
    {
        public List<Vector3Int> inputBlocks;
        public Ray ray;
        public string prefabName;

        public const string UNMARK = "Unmark";

        public override void Replay(IReplayContext context)
        {
            var plantingService = context.GetSingleton<PlantingSelectionService>();
            if (prefabName == UNMARK)
            {
                plantingService.UnmarkArea(inputBlocks, ray);
            }
            else
            {
                plantingService.MarkArea(inputBlocks, ray, prefabName);
            }
        }

        public override string ToActionString()
        {
            return $"Planting {inputBlocks.Count()} of {prefabName}";
        }
    }

    [HarmonyPatch(typeof(PlantingSelectionService), nameof(PlantingSelectionService.MarkArea))]
    class PlantingAreaMarkedPatcher
    {
        static bool Prefix(IEnumerable<Vector3Int> inputBlocks, Ray ray, string templateName)
        {
            return ReplayEvent.DoPrefix(() =>
            {
                return new PlantingAreaMarkedEvent()
                {
                    prefabName = templateName,
                    ray = ray,
                    inputBlocks = new List<Vector3Int>(inputBlocks)
                };
            });
        }
    }

    [HarmonyPatch(typeof(PlantingSelectionService), nameof(PlantingSelectionService.UnmarkArea))]
    class PlantingAreaUnmarkedPatcher
    {
        static bool Prefix(IEnumerable<Vector3Int> inputBlocks, Ray ray)
        {
            return ReplayEvent.DoPrefix(() =>
            {
                return new PlantingAreaMarkedEvent()
                {
                    prefabName = PlantingAreaMarkedEvent.UNMARK,
                    ray = ray,
                    inputBlocks = new List<Vector3Int>(inputBlocks)
                };
            });
        }
    }

    [Serializable]
    class ClearResourcesMarkedEvent : ReplayEvent
    {
        public List<Guid> blocks;
        public Vector3Int start;
        public Vector3Int end;
        public bool markForDemolition;

        public override void Replay(IReplayContext context)
        {
            var entityService = context.GetSingleton<EntityService>();
            var entityRegistry = context.GetSingleton<EntityRegistry>();
            int missing = 0;
            var blockObjects = blocks.Select(guid =>
            {
                var entity = entityRegistry.GetEntity(guid);
                if (entity == null)
                {
                    missing++;
                    return null;
                }
                return entity.GetComponent<BlockObject>();
            }).Where(obj => obj != null).ToList();
            if (missing > 0)
            {
                Plugin.LogWarning($"[ReplayReject] ClearResourcesMarked: {missing}/{blocks.Count} entities not found (action still applied to remaining {blockObjects.Count})");
            }
            if (blockObjects.Count == 0) return;
            if (markForDemolition)
            {
                context.GetSingleton<DemolishableSelectionTool>().ActionCallback(blockObjects, start, end, false, false);
            }
            else
            {
                context.GetSingleton<DemolishableUnselectionTool>().ActionCallback(blockObjects, start, end, false, false);
            }
        }

        public override string ToActionString()
        {
            return $"Setting {blocks.Count()} as marked: {markForDemolition}";
        }

        public static bool DoPrefix(IEnumerable<BlockObject> blockObjects, Vector3Int start, Vector3Int end, bool forDemolition)
        {
            return DoPrefix(() =>
            {
                var ids = blockObjects.Select(obj => obj.GetComponent<EntityComponent>().EntityId);
                return new ClearResourcesMarkedEvent()
                {
                    blocks = ids.ToList(),
                    start = start,
                    end = end,
                    markForDemolition = forDemolition
                };
            });
        }
    }

    [HarmonyPatch(typeof(DemolishableSelectionTool), nameof(DemolishableSelectionTool.ActionCallback))]
    class DemolishableSelectionServiceMarkPatcher
    {
        static bool Prefix(IEnumerable<BlockObject> blockObjects, Vector3Int start, Vector3Int end, bool selectionStarted, bool selectingArea)
        {
            return ClearResourcesMarkedEvent.DoPrefix(blockObjects, start, end, true);
        }
    }

    [HarmonyPatch(typeof(DemolishableUnselectionTool), nameof(DemolishableUnselectionTool.ActionCallback))]
    class DemolishableSelectionServiceUnmarkPatcher
    {
        static bool Prefix(IEnumerable<BlockObject> blockObjects, Vector3Int start, Vector3Int end, bool selectionStarted, bool selectingArea)
        {
            return ClearResourcesMarkedEvent.DoPrefix(blockObjects, start, end, false);
        }
    }

    [Serializable]
    class TreeCuttingAreaEvent : ReplayEvent
    {
        public List<Vector3Int> coordinates;
        public bool wasAdded;

        public override void Replay(IReplayContext context)
        {
            var treeService = context.GetSingleton<TreeCuttingArea>();
            if (wasAdded)
            {
                treeService.AddCoordinates(coordinates);
            }
            else
            {
                treeService.RemoveCoordinates(coordinates);
            }
        }

        public override string ToActionString()
        {
            string verb = wasAdded ? "Added" : "Removed";
            return $"{verb} tree planting coordinate {coordinates.Count()}";
        }
    }

    [HarmonyPatch(typeof(TreeCuttingArea), nameof(TreeCuttingArea.AddCoordinates))]
    class TreeCuttingAreaAddedPatcher
    {
        static bool Prefix(IEnumerable<Vector3Int> coordinates)
        {
            return ReplayEvent.DoPrefix(() =>
            {
                return new TreeCuttingAreaEvent()
                {
                    coordinates = new List<Vector3Int>(coordinates),
                    wasAdded = true,
                };
            });
        }
    }

    [HarmonyPatch(typeof(TreeCuttingArea), nameof(TreeCuttingArea.RemoveCoordinates))]
    class TreeCuttingAreaRemovedPatcher
    {
        static bool Prefix(IEnumerable<Vector3Int> coordinates)
        {
            return ReplayEvent.DoPrefix(() =>
            {
                return new TreeCuttingAreaEvent()
                {
                    coordinates = new List<Vector3Int>(coordinates),
                    wasAdded = false,
                };
            });
        }
    }

    [Serializable]
    class BuildingUnlockedEvent : ReplayEvent
    {
        public string buildingName;

        public override void Replay(IReplayContext context)
        {
            var building = GetBuilding(context, buildingName);
            if (building == null) return;
            context.GetSingleton<BuildingUnlockingService>().Unlock(building);

            var toolButtonService = context.GetSingleton<ToolButtonService>();
            var toolUnlockingService = toolButtonService._toolUnlockingService;

            foreach (ToolButton toolButton in toolButtonService.ToolButtons)
            {
                var tool = toolButton.Tool;
                BlockObjectTool blockObjectTool = tool as BlockObjectTool;
                if (blockObjectTool == null)
                {
                    continue;
                }
                BuildingSpec toolBuilding = blockObjectTool.Template.GetSpec<BuildingSpec>();
                if (toolBuilding == building)
                {
                    Plugin.Log("Unlocking tool for building: " + buildingName);
                    context.GetSingleton<UnlockedPlantableGroupsRegistry>().AddUnlockedPlantableGroups(toolBuilding);
                    // Call Unlock to remove from _activeLockers and post ToolUnlockedEvent
                    if (toolUnlockingService != null && toolUnlockingService.IsLocked(tool))
                    {
                        toolUnlockingService.Unlock(tool);
                    }
                }
            }
        }

        public override string ToActionString()
        {
            return $"Unlocking building: {buildingName}";
        }
    }

    [HarmonyPatch(typeof(BuildingUnlockingService), nameof(BuildingUnlockingService.Unlock))]
    class BuildingUnlockingServiceUnlockPatcher
    {
        static bool Prefix(BuildingSpec buildingSpec)
        {
            return ReplayEvent.DoPrefix(() =>
            {
                return new BuildingUnlockedEvent()
                {
                    buildingName = buildingSpec.Blueprint.Name,
                };
            });
        }
    }

    [Serializable]
    class WorkingHoursChangedEvent : ReplayEvent
    {
        public int hours;

        public override void Replay(IReplayContext context)
        {
            var panel = context.GetSingleton<WorkingHoursPanel>();
            panel._hours = hours;
            panel.OnHoursChanged();
        }

        public override string ToActionString()
        {
            return $"Setting working hours: {hours}";
        }
    }

    [HarmonyPatch(typeof(WorkingHoursPanel), nameof(WorkingHoursPanel.OnHoursChanged))]
    class WorkingHoursPanelOnHoursChangedPatcher
    {
        static bool Prefix(WorkingHoursPanel __instance)
        {
            bool value = ReplayEvent.DoPrefix(() =>
            {
                return new WorkingHoursChangedEvent()
                {
                    hours = __instance._hours,
                };
            });

            // Update the title if we're actually calling this event
            if (!value) __instance.UpdateTitle();
            return value;
        }
    }

    [Serializable]
    class DuplicationEvent : ReplayEvent
    {
        public string sourceEntityID;
        public string targetEntityID;

        public override void Replay(IReplayContext context)
        {
            var duplicator = new Duplicator();
            duplicator.Duplicate(
                GetEntityComponent(context, sourceEntityID),
                GetEntityComponent(context, targetEntityID)
            );
        }

        public override string ToActionString()
        {
            return $"Duplicating properties from {sourceEntityID} to {targetEntityID}";
        }
    }

    [ManualMethodOverwrite]
    /*
     * 11/26/2025
     * This isn't a real manual method overwrite, but it still needs
     * to be reviewed when the Timberborn code updates. It makes a strong
     * assumption that Duplicator.Duplicate is only called by UI events
     * and that it's the only callback that gets called when a building
     * is placed (see BuildingPlacedEvent).
     * Check
     * * IBlockObjectPlacer.Place: Make sure it's only called with
     *   a callback that calls this method.
     * * Duplicator.Duplicate: Make sure it's only called by UI actions.
     * * DuplicateSettingsTool: Make sure this continues not to do anything
     *   other than other than registering the change so it can be undone and
     *   that undos are still only supported in the map editor.
     */
    [HarmonyPatch(typeof(Duplicator), nameof(Duplicator.Duplicate))]
    class DuplicatorDuplicatePatcher
    {
        static bool Prefix(BaseComponent sourceEntity, BaseComponent targetEntity)
        {
            return ReplayEvent.DoPrefix(() =>
            {
                return new DuplicationEvent()
                {
                    sourceEntityID = ReplayEvent.GetEntityID(sourceEntity),
                    targetEntityID = ReplayEvent.GetEntityID(targetEntity),
                };
            });
        }
    }
}