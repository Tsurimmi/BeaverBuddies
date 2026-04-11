using BeaverBuddies.IO;
using System;
using Timberborn.BaseComponentSystem;
using Timberborn.BlockSystem;
using Timberborn.BlueprintSystem;
using Timberborn.Buildings;
using Timberborn.EntitySystem;
using Timberborn.TemplateSystem;
using UnityEngine;
using static BeaverBuddies.SingletonManager;

namespace BeaverBuddies.Events
{
    public interface IReplayContext
    {
        T GetSingleton<T>();
    }

    public abstract class ReplayEvent : IComparable<ReplayEvent>
    {
        public int ticksSinceLoad;
        public int? randomS0Before;
        // Optional: world coordinates of the BlockObject this event targets,
        // auto-populated by DoEntityPrefix when the target has a BlockObject.
        // Used as a fallback when the entityID lookup fails due to ID
        // divergence between host and client.
        public Vector3Int? entityCoordinates;

        public string type => GetType().Name;

        public int CompareTo(ReplayEvent other)
        {
            if (other == null)
                return 1;
            //return timeInFixedSecs.CompareTo(other.timeInFixedSecs);
            return ticksSinceLoad.CompareTo(other.ticksSinceLoad);
        }

        public abstract void Replay(IReplayContext context);

        public override string ToString()
        {
            return type;
        }

        public virtual string ToActionString()
        {
            return $"Doing: {type}";
        }

        public static EntityComponent GetEntityComponent(IReplayContext context, string entityID, Vector3Int? fallbackCoordinates = null)
        {
            if (!Guid.TryParse(entityID, out Guid guid))
            {
                Plugin.LogWarning($"Could not parse guid: {entityID}");
                return null;
            }
            var entity = context.GetSingleton<EntityRegistry>().GetEntity(guid);
            if (entity != null) return entity;

            // Fallback: if the ID doesn't match (ID divergence between host
            // and client), look up a BlockObject at the provided coordinates.
            if (fallbackCoordinates.HasValue)
            {
                var blockService = context.GetSingleton<IBlockService>();
                var objectsAt = blockService.GetObjectsAt(fallbackCoordinates.Value);
                foreach (var obj in objectsAt)
                {
                    if (obj == null) continue;
                    var ec = obj.GetComponent<EntityComponent>();
                    if (ec != null)
                    {
                        Plugin.Log($"[ReplayRecover] Entity {entityID} recovered via coordinates {fallbackCoordinates.Value}");
                        return ec;
                    }
                }
            }

            Plugin.LogWarning($"Could not find entity: {entityID}");
            return null;
        }

        public static T GetComponent<T>(IReplayContext context, string entityID, Vector3Int? fallbackCoordinates = null)
        {
            var entity = GetEntityComponent(context, entityID, fallbackCoordinates);
            if (entity == null) return default;
            var component = entity.GetComponent<T>();
            if (component == null)
            {
                Plugin.LogWarning($"Could not find component {typeof(T)} on entity {entityID}");
            }
            return component;
        }

        public static string GetEntityID(BaseComponent component)
        {
            return component?.GetComponent<EntityComponent>()?.EntityId.ToString();
        }

        protected BuildingSpec GetBuilding(IReplayContext context, string buildingName)
        {
            var result = context.GetSingleton<BuildingService>().GetBuildingTemplate(buildingName);
            if (result == null)
            {
                Plugin.LogWarning($"Could not find building prefab: {buildingName}");
            }
            return result;
        }

        public static string GetBuildingName(ComponentSpec component)
        {
            return component.GetSpec<TemplateSpec>()?.TemplateName;
        }

        public static ReplayService GetReplayServiceIfReady()
        {
            // If we haven't loaded yet, we're not ready
            if (!ReplayService.IsLoaded) return null;

            var replayService = GetSingleton<ReplayService>();
            if (replayService == null || replayService.IsDesynced) return null;
            return replayService;
        }
        

        /// <summary>
        /// Helper method to make overriding recorded actions in game easier.
        /// </summary>
        /// <param name="getEvent">
        /// A function that returns the event to record, or null
        /// if we should skip recording and do the default method behavior.
        /// </param>
        /// <returns>True if the method should use default behavior</returns>
        public static bool DoPrefix(Func<ReplayEvent> getEvent)
        {
            // If we're already replaying events, just let the original method run.
            // This handles nested calls (e.g., Replay() calls Unlock() which triggers this prefix again)
            if (ReplayService.IsReplayingEvents) return true;

            // If the replay service is not available, just use default behavior
            ReplayService replayService = GetReplayServiceIfReady();
            if (replayService == null) return true;

            // TODO: I don't think there's any reason to
            // create the event here when replaying events, since
            // it'll just get thrown away. Probably not a big deal, but
            // it can be confusing in debugging. Too afraid it'll break
            // something to change it right now though.

            // Get the event and if it's null, just use default behavior
            ReplayEvent message = getEvent();
            if (message == null) return true;

            // Optional: Log the message
            Plugin.Log(message.ToActionString());

            // Record the event
            replayService.RecordEvent(message);

            // Return based on the EventIO's desired behavior
            return EventIO.ShouldPlayPatchedEvents;
        }

        public static bool DoEntityPrefix(BaseComponent component, Func<string, ReplayEvent> doRecord)
        {
            return DoPrefix(() =>
            {
                string entityID = GetEntityID(component);
                // If this is happening to a non-entity (e.g. prefab),
                // just let the base method handle it
                if (entityID == null) return null;
                ReplayEvent evt = doRecord(entityID);
                // Auto-capture block coordinates so Replay() can fall back to
                // a spatial lookup if the entityID has diverged on the peer.
                if (evt != null && component != null)
                {
                    var blockObj = component.GetComponent<BlockObject>();
                    if (blockObj != null)
                    {
                        evt.entityCoordinates = blockObj.Coordinates;
                    }
                }
                return evt;
            });
        }
    }

}
