using System;
using JetBrains.Annotations;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Utility;

namespace Robust.Shared.GameObjects
{
    [Flags]
    public enum LookupFlags : byte
    {
        None = 0,

        /// <summary>
        /// Should we use the approximately intersecting entities or check tighter bounds.
        /// </summary>
        Approximate = 1 << 0,

        /// <summary>
        /// Also return entities from an anchoring query.
        /// </summary>
        Anchored = 1 << 1,

        /// <summary>
        /// Include entities that are currently in containers.
        /// </summary>
        Contained = 1 << 2,
        // IncludeGrids = 1 << 2,
    }

    public sealed partial class EntityLookupSystem : EntitySystem
    {
        [Dependency] private readonly IMapManager _mapManager = default!;
        [Dependency] private readonly SharedContainerSystem _container = default!;
        [Dependency] private readonly SharedTransformSystem _transform = default!;

        /// <summary>
        /// Returns all non-grid entities. Consider using your own flags if you wish for a faster query.
        /// </summary>
        public const LookupFlags DefaultFlags = LookupFlags.Contained | LookupFlags.Anchored;

        private const int GrowthRate = 256;

        private const float PointEnlargeRange = .00001f / 2;

        /// <summary>
        /// Like RenderTree we need to enlarge our lookup range for EntityLookupComponent as an entity is only ever on
        /// 1 EntityLookupComponent at a time (hence it may overlap without another lookup).
        /// </summary>
        private float _lookupEnlargementRange;

        public override void Initialize()
        {
            base.Initialize();
            var configManager = IoCManager.Resolve<IConfigurationManager>();
            configManager.OnValueChanged(CVars.LookupEnlargementRange, value => _lookupEnlargementRange = value, true);

            SubscribeLocalEvent<MoveEvent>(OnMove);
            SubscribeLocalEvent<EntParentChangedMessage>(OnParentChange);
            SubscribeLocalEvent<AnchorStateChangedEvent>(OnAnchored);
            SubscribeLocalEvent<EntInsertedIntoContainerMessage>(OnContainerInsert);
            SubscribeLocalEvent<EntRemovedFromContainerMessage>(OnContainerRemove);

            SubscribeLocalEvent<EntityLookupComponent, ComponentAdd>(OnLookupAdd);
            SubscribeLocalEvent<EntityLookupComponent, ComponentShutdown>(OnLookupShutdown);
            SubscribeLocalEvent<GridAddEvent>(OnGridAdd);

            EntityManager.EntityInitialized += OnEntityInit;
            SubscribeLocalEvent<MapChangedEvent>(OnMapCreated);
        }

        public override void Shutdown()
        {
            base.Shutdown();
            EntityManager.EntityInitialized -= OnEntityInit;
        }

        /// <summary>
        /// Updates the entity's AABB. Uses <see cref="ILookupWorldBox2Component"/>
        /// </summary>
        [UsedImplicitly]
        public void UpdateBounds(EntityUid uid, TransformComponent? xform = null, MetaDataComponent? meta = null)
        {
            if (_container.IsEntityInContainer(uid, meta))
                return;

            var xformQuery = GetEntityQuery<TransformComponent>();

            if (!xformQuery.Resolve(uid, ref xform) || xform.Anchored)
                return;

            var lookup = GetLookup(uid, xform, xformQuery);

            if (lookup == null) return;

            var coordinates = _transform.GetMoverCoordinates(xform.Coordinates, xformQuery);
            var lookupRotation = _transform.GetWorldRotation(lookup.Owner, xformQuery);
            // If we're contained then LocalRotation should be 0 anyway.
            var aabb = GetAABB(xform.Owner, coordinates.Position, _transform.GetWorldRotation(xform, xformQuery) - lookupRotation, xform, xformQuery);

            // TODO: Only container children need updating so could manually do this slightly better.
            AddToEntityTree(lookup, xform, aabb, xformQuery, lookupRotation);
        }

        private void OnAnchored(ref AnchorStateChangedEvent args)
        {
            // This event needs to be handled immediately as anchoring is handled immediately
            // and any callers may potentially get duplicate entities that just changed state.
            if (args.Anchored)
            {
                RemoveFromEntityTree(args.Entity);
            }
            else if (!args.Detaching &&
                TryComp(args.Entity, out MetaDataComponent? meta) &&
                meta.EntityLifeStage < EntityLifeStage.Terminating)
            {
                var xformQuery = GetEntityQuery<TransformComponent>();
                var xform = xformQuery.GetComponent(args.Entity);
                var lookup = GetLookup(args.Entity, xform, xformQuery);

                if (lookup == null)
                    throw new InvalidOperationException();

                var coordinates = _transform.GetMoverCoordinates(xform.Coordinates, xformQuery);
                var lookupRotation = _transform.GetWorldRotation(lookup.Owner, xformQuery);
                DebugTools.Assert(coordinates.EntityId == lookup.Owner);

                // If we're contained then LocalRotation should be 0 anyway.
                var aabb = GetAABB(args.Entity, coordinates.Position, _transform.GetWorldRotation(xform, xformQuery) - lookupRotation, xform, xformQuery);
                AddToEntityTree(lookup, xform, aabb, xformQuery, lookupRotation);
            }
            // else -> the entity is terminating. We can ignore this un-anchor event, as this entity will be removed by the tree via OnEntityDeleted.
        }

        #region DynamicTree

        private void OnLookupShutdown(EntityUid uid, EntityLookupComponent component, ComponentShutdown args)
        {
            component.Tree.Clear();
        }

        private void OnGridAdd(GridAddEvent ev)
        {
            EntityManager.EnsureComponent<EntityLookupComponent>(ev.EntityUid);
        }

        private void OnLookupAdd(EntityUid uid, EntityLookupComponent component, ComponentAdd args)
        {
            int capacity;

            if (EntityManager.TryGetComponent(uid, out TransformComponent? xform))
            {
                capacity = (int) Math.Min(256, Math.Ceiling(xform.ChildCount / (float) GrowthRate) * GrowthRate);
            }
            else
            {
                capacity = 256;
            }

            component.Tree = new DynamicTree<EntityUid>(
                (in EntityUid e) => GetTreeAABB(e, component.Owner),
                capacity: capacity,
                growthFunc: x => x == GrowthRate ? GrowthRate * 8 : x * 2
            );
        }

        private void OnMapCreated(MapChangedEvent eventArgs)
        {
            if(eventArgs.Destroyed)
                return;

            if (eventArgs.Map == MapId.Nullspace) return;

            EntityManager.EnsureComponent<EntityLookupComponent>(_mapManager.GetMapEntityId(eventArgs.Map));
        }

        private Box2 GetTreeAABB(EntityUid entity, EntityUid tree)
        {
            var xformQuery = GetEntityQuery<TransformComponent>();

            if (!xformQuery.TryGetComponent(entity, out var xform))
            {
                Logger.Error($"Entity tree contains a deleted entity? Tree: {ToPrettyString(tree)}, entity: {entity}");
                return default;
            }

            if (xform.ParentUid == tree)
                return GetAABBNoContainer(entity, xform.LocalPosition, xform.LocalRotation);

            if (!xformQuery.TryGetComponent(tree, out var treeXform))
            {
                Logger.Error($"Entity tree has no transform? Tree Uid: {tree}");
                return default;
            }

            return treeXform.InvWorldMatrix.TransformBox(GetWorldAABB(entity, xform));
        }

        #endregion

        #region Entity events
        private void OnEntityInit(EntityUid uid)
        {
            if (_container.IsEntityInContainer(uid)) return;

            var xformQuery = GetEntityQuery<TransformComponent>();

            if (!xformQuery.TryGetComponent(uid, out var xform) ||
                xform.Anchored) return;

            if (_mapManager.IsMap(uid) ||
                _mapManager.IsGrid(uid)) return;

            var lookup = GetLookup(uid, xform, xformQuery);

            // If nullspace or the likes.
            if (lookup == null) return;

            var coordinates = _transform.GetMoverCoordinates(xform.Coordinates, xformQuery);
            DebugTools.Assert(coordinates.EntityId == lookup.Owner);
            var lookupRotation = _transform.GetWorldRotation(lookup.Owner, xformQuery);

            // If we're contained then LocalRotation should be 0 anyway.
            var aabb = GetAABB(uid, coordinates.Position, _transform.GetWorldRotation(xform, xformQuery) - lookupRotation, xform, xformQuery);

            // Any child entities should be handled by their own OnEntityInit
            AddToEntityTree(lookup, xform, aabb, xformQuery, lookupRotation, false);
        }

        private void OnMove(ref MoveEvent args)
        {
            UpdatePosition(args.Sender, args.Component);
        }

        private void UpdatePosition(EntityUid uid, TransformComponent xform)
        {
            // Even if the entity is contained it may have children that aren't so we still need to update.
            if (!CanMoveUpdate(uid)) return;

            var xformQuery = GetEntityQuery<TransformComponent>();
            var lookup = GetLookup(uid, xform, xformQuery);

            if (lookup == null) return;

            var coordinates = _transform.GetMoverCoordinates(xform.Coordinates, xformQuery);
            var lookupRotation = _transform.GetWorldRotation(lookup.Owner, xformQuery);
            var aabb = GetAABB(uid, coordinates.Position, _transform.GetWorldRotation(xform) - lookupRotation, xform, xformQuery);
            AddToEntityTree(lookup, xform, aabb, xformQuery, lookupRotation);
        }

        private bool CanMoveUpdate(EntityUid uid)
        {
            return !_mapManager.IsMap(uid) &&
                     !_mapManager.IsGrid(uid) &&
                     !_container.IsEntityInContainer(uid);
        }

        private void OnParentChange(ref EntParentChangedMessage args)
        {
            var meta = MetaData(args.Entity);

            // If our parent is changing due to a container-insert, we let the container insert event handle that. Note
            // that the in-container flag gets set BEFORE insert parent change, and gets unset before the container
            // removal parent-change. So if it is set here, this must mean we are getting inserted.
            //
            // However, this means that this method will still get run in full on container removal. Additionally,
            // because not all container removals are guaranteed to result in a parent change, container removal events
            // also need to add the entity to a tree. So this generally results in:
            // add-to-tree -> remove-from-tree -> add-to-tree.
            // Though usually, `oldLookup == newLookup` for the last step. Its still shit though.
            //
            // TODO IMPROVE CONTAINER REMOVAL HANDLING

            if (_container.IsEntityInContainer(args.Entity, meta))
                return;

            if (meta.EntityLifeStage < EntityLifeStage.Initialized ||
                _mapManager.IsGrid(args.Entity) ||
                _mapManager.IsMap(args.Entity)) return;

            var xformQuery = GetEntityQuery<TransformComponent>();
            var xform = args.Transform;
            EntityLookupComponent? oldLookup = null;

            if (args.OldMapId != MapId.Nullspace && args.OldParent != null)
            {
                oldLookup = GetLookup(args.OldParent.Value, xformQuery);
            }

            var newLookup = GetLookup(args.Entity, xform, xformQuery);

            // If parent is the same then no need to do anything as position should stay the same.
            if (oldLookup == newLookup) return;

            RemoveFromEntityTree(oldLookup, xform, xformQuery);

            if (newLookup != null)
                AddToEntityTree(newLookup, xform, xformQuery, _transform.GetWorldRotation(newLookup.Owner, xformQuery));
        }

        private void OnContainerRemove(EntRemovedFromContainerMessage ev)
        {
            var xformQuery = GetEntityQuery<TransformComponent>();
            var xform = xformQuery.GetComponent(ev.Entity);
            var lookup = GetLookup(ev.Entity, xform, xformQuery);

            if (lookup == null) return;

            AddToEntityTree(lookup, xform, xformQuery, _transform.GetWorldRotation(lookup.Owner, xformQuery));
        }

        private void OnContainerInsert(EntInsertedIntoContainerMessage ev)
        {
            var xformQuery = GetEntityQuery<TransformComponent>();

            if (ev.OldParent == EntityUid.Invalid || !xformQuery.TryGetComponent(ev.OldParent, out var oldXform))
                return;

            var lookup = GetLookup(ev.OldParent, oldXform, xformQuery);

            RemoveFromEntityTree(lookup, xformQuery.GetComponent(ev.Entity), xformQuery);
        }

        private void AddToEntityTree(
            EntityLookupComponent lookup,
            TransformComponent xform,
            EntityQuery<TransformComponent> xformQuery,
            Angle lookupRotation,
            bool recursive = true)
        {
            var coordinates = _transform.GetMoverCoordinates(xform.Coordinates, xformQuery);
            // If we're contained then LocalRotation should be 0 anyway.
            var aabb = GetAABB(xform.Owner, coordinates.Position, _transform.GetWorldRotation(xform, xformQuery) - lookupRotation, xform, xformQuery);
            AddToEntityTree(lookup, xform, aabb, xformQuery, lookupRotation, recursive);
        }

        private void AddToEntityTree(
            EntityLookupComponent? lookup,
            TransformComponent xform,
            Box2 aabb,
            EntityQuery<TransformComponent> xformQuery,
            Angle lookupRotation,
            bool recursive = true)
        {
            // If entity is in nullspace then no point keeping track of data structure.
            if (lookup == null) return;

            if (!xform.Anchored)
                lookup.Tree.AddOrUpdate(xform.Owner, aabb);

            var childEnumerator = xform.ChildEnumerator;

            if (xform.ChildCount == 0 || !recursive) return;

            // If they're in a container then don't add to entitylookup due to the additional cost.
            // It's cheaper to just query these components at runtime given PVS no longer uses EntityLookupSystem.
            if (EntityManager.TryGetComponent<ContainerManagerComponent>(xform.Owner, out var conManager))
            {
                while (childEnumerator.MoveNext(out var child))
                {
                    if (conManager.ContainsEntity(child.Value)) continue;

                    var childXform = xformQuery.GetComponent(child.Value);
                    var coordinates = _transform.GetMoverCoordinates(childXform.Coordinates, xformQuery);
                    // TODO: If we have 0 position and not contained can optimise these further, but future problem.
                    var childAABB = GetAABBNoContainer(child.Value, coordinates.Position, childXform.WorldRotation - lookupRotation);
                    AddToEntityTree(lookup, childXform, childAABB, xformQuery, lookupRotation);
                }
            }
            else
            {
                while (childEnumerator.MoveNext(out var child))
                {
                    var childXform = xformQuery.GetComponent(child.Value);
                    var coordinates = _transform.GetMoverCoordinates(childXform.Coordinates, xformQuery);
                    // TODO: If we have 0 position and not contained can optimise these further, but future problem.
                    var childAABB = GetAABBNoContainer(child.Value, coordinates.Position, childXform.WorldRotation - lookupRotation);
                    AddToEntityTree(lookup, childXform, childAABB, xformQuery, lookupRotation);
                }
            }
        }

        private void RemoveFromEntityTree(EntityUid uid, bool recursive = true)
        {
            var xformQuery = GetEntityQuery<TransformComponent>();
            var xform = xformQuery.GetComponent(uid);
            var lookup = GetLookup(uid, xform, xformQuery);
            RemoveFromEntityTree(lookup, xform, xformQuery, recursive);
        }

        /// <summary>
        /// Recursively iterates through this entity's children and removes them from the entitylookupcomponent.
        /// </summary>
        private void RemoveFromEntityTree(EntityLookupComponent? lookup, TransformComponent xform, EntityQuery<TransformComponent> xformQuery, bool recursive = true)
        {
            // TODO: Move this out of the loop
            if (lookup == null) return;

            lookup.Tree.Remove(xform.Owner);

            if (!recursive) return;

            var childEnumerator = xform.ChildEnumerator;

            while (childEnumerator.MoveNext(out var child))
            {
                RemoveFromEntityTree(lookup, xformQuery.GetComponent(child.Value), xformQuery);
            }
        }

        #endregion

        private EntityLookupComponent? GetLookup(EntityUid entity, EntityQuery<TransformComponent> xformQuery)
        {
            var xform = xformQuery.GetComponent(entity);
            return GetLookup(entity, xform, xformQuery);
        }

        private EntityLookupComponent? GetLookup(EntityUid uid, TransformComponent xform, EntityQuery<TransformComponent> xformQuery)
        {
            if (xform.MapID == MapId.Nullspace)
                return null;

            var parent = xform.ParentUid;
            var lookupQuery = GetEntityQuery<EntityLookupComponent>();

            // If we're querying a map / grid just return it directly.
            if (lookupQuery.TryGetComponent(uid, out var lookup))
            {
                return lookup;
            }

            while (parent.IsValid())
            {
                if (lookupQuery.TryGetComponent(parent, out var comp)) return comp;
                parent = xformQuery.GetComponent(parent).ParentUid;
            }

            return null;
        }

        #region Bounds

        /// <summary>
        /// Get the AABB of an entity with the supplied position and angle. Tries to consider if the entity is in a container.
        /// </summary>
        internal Box2 GetAABB(EntityUid uid, Vector2 position, Angle angle, TransformComponent xform, EntityQuery<TransformComponent> xformQuery)
        {
            // If we're in a container then we just use the container's bounds.
            if (_container.TryGetOuterContainer(uid, xform, out var container, xformQuery))
            {
                return GetAABBNoContainer(container.Owner, position, angle);
            }

            return GetAABBNoContainer(uid, position, angle);
        }

        /// <summary>
        /// Get the AABB of an entity with the supplied position and angle without considering containers.
        /// </summary>
        private Box2 GetAABBNoContainer(EntityUid uid, Vector2 position, Angle angle)
        {
            if (TryComp<ILookupWorldBox2Component>(uid, out var worldLookup))
            {
                var transform = new Transform(position, angle);
                return worldLookup.GetAABB(transform);
            }
            else
            {
                return new Box2(position, position);
            }
        }

        public Box2 GetWorldAABB(EntityUid uid, TransformComponent? xform = null)
        {
            var xformQuery = GetEntityQuery<TransformComponent>();
            xform ??= xformQuery.GetComponent(uid);
            var (worldPos, worldRot) = xform.GetWorldPositionRotation();

            return GetAABB(uid, worldPos, worldRot, xform, xformQuery);
        }

        #endregion
    }
}
