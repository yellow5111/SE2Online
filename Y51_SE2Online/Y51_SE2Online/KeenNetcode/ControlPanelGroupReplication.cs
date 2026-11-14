using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.VRage.Library.Serialization;
using Keen.VRage.Multiplayer.Replications;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>Each control panel owns its replicated groups; server edits still use the actual grid's groups.</summary>
internal static class ControlPanelGroupReplication
{
    private static readonly ConditionalWeakTable<ControlPanelGridModel, GroupCopies> Grids = new();
    private static readonly ConditionalWeakTable<ControlPanelGroupModel, GridGroup> Models = new();
    private static readonly ConditionalWeakTable<GridGroup, GridGroup> Sources = new();
    private static readonly AccessibleTypeInfo Original = ControlPanelGroupModel.TypeInfoHolder.Value;
    private static readonly AccessibleTypeInfo Replicated = MakeTypeInfo();

    internal static void Install(Harmony harmony)
    {
        var create = AccessTools.Method(typeof(StreamedControlPanelSessionComponent), "CreateEmptyGroupModel")
            ?? throw new MissingMethodException("CreateEmptyGroupModel");
        var members = AccessTools.Method(typeof(ObservableObjectReplicationSessionComponent), "GetReplicatedMembers")
            ?? throw new MissingMethodException("GetReplicatedMembers");
        var dispose = AccessTools.Method(typeof(ControlPanelGridModelServer), nameof(IDisposable.Dispose))
            ?? throw new MissingMethodException("ControlPanelGridModelServer.Dispose");
        var request = AccessTools.AsyncMoveNext(AccessTools.Method(typeof(StreamedControlPanelSessionComponent), "RequestSetTileGroup")
            ?? throw new MissingMethodException("RequestSetTileGroup")) ?? throw new MissingMethodException("RequestSetTileGroup state machine");
        if (AccessTools.Field(request.DeclaringType, "group")?.FieldType != typeof(GridGroup))
            throw new MissingFieldException("RequestSetTileGroup.group");
        harmony.Patch(create, postfix: new HarmonyMethod(typeof(ControlPanelGroupReplication), nameof(Created)));
        harmony.Patch(members, prefix: new HarmonyMethod(typeof(ControlPanelGroupReplication), nameof(MembersPrefix)));
        harmony.Patch(dispose, postfix: new HarmonyMethod(typeof(ControlPanelGroupReplication), nameof(Disposed)));
        harmony.Patch(request, prefix: new HarmonyMethod(typeof(ControlPanelGroupReplication), nameof(RestoreGroupArgument)));
    }

    private static AccessibleTypeInfo MakeTypeInfo()
    {
        var field = Original.Members["_group"].Accessor;
        var accessor = new ClassMemberAccessor<ControlPanelGroupModel, GridGroup>(field.Name, field.Name, field.Serializers,
            field.Member, typeof(GridGroup), () => GridGroup.TypeInfoHolder.Value,
            model => Models.TryGetValue(model, out var copy) ? copy : model._group,
            (ControlPanelGroupModel model, in GridGroup value) => model._group = value, null);
        return new AccessibleTypeInfo(Original.Type, Original.Documentation,
            Original.Members.Select(pair => (pair.Key, pair.Key == "_group" ? accessor : pair.Value.Accessor)),
            Original.MembersDeclarationOrder, Original.MembersDocumentation, Original.MembersAttributes);
    }

    private static void MembersPrefix(ref AccessibleTypeInfo typeInfo)
    {
        if (ReferenceEquals(typeInfo, Original)) typeInfo = Replicated;
    }

    private static void Created(GridGroup group, ControlPanelGridModel gridModel, ControlPanelGroupModel __result)
    {
        // Sharing a real GridGroup between viewers makes the native observable hierarchy reject the second panel.
        Models.Add(__result, Grids.GetValue(gridModel, _ => new GroupCopies()).Get(group));
    }

    private static void Disposed(ControlPanelGridModelServer __instance)
    {
        if (!Grids.TryGetValue(__instance, out var copies)) return;
        Grids.Remove(__instance);
        copies.Dispose();
    }

    private static void RestoreGroupArgument(ref GridGroup ___group)
    {
        if (___group != null && Sources.TryGetValue(___group, out var source)) ___group = source;
    }

    private sealed class GroupCopies : IDisposable
    {
        private readonly Dictionary<GridGroup, Copy> _copies = new(ReferenceEqualityComparer.Instance);

        public GridGroup Get(GridGroup source)
        {
            if (_copies.TryGetValue(source, out var known)) return known.Value;
            var copy = new Copy(this, source);
            _copies.Add(source, copy);
            Sources.Add(copy.Value, source);
            copy.RefreshChildren();
            return copy.Value;
        }

        public void Dispose()
        {
            foreach (var copy in _copies.Values)
            {
                copy.Dispose();
                Sources.Remove(copy.Value);
            }
            _copies.Clear();
        }

        private sealed class Copy : IDisposable
        {
            private readonly GroupCopies _owner;
            private readonly GridGroup _source;
            public readonly GridGroup Value = new();

            public Copy(GroupCopies owner, GridGroup source)
            {
                (_owner, _source) = (owner, source);
                RefreshProperties(null, null);
                source.PropertyChanged += RefreshProperties;
                source.SubGroups.CollectionChanged += ChildrenChanged;
            }

            private void RefreshProperties(object sender, PropertyChangedEventArgs args)
            {
                Value.Id = _source.Id;
                Value.DisplayName = _source.DisplayName;
                Value.ShowInTerminal = _source.ShowInTerminal;
                Value.Depth = _source.Depth;
            }

            private void ChildrenChanged(object sender, NotifyCollectionChangedEventArgs args) => RefreshChildren();

            public void RefreshChildren()
            {
                Value.SubGroups.Clear();
                Value.SubGroups.AddRange(_source.SubGroups.Select(_owner.Get));
            }

            public void Dispose()
            {
                _source.PropertyChanged -= RefreshProperties;
                _source.SubGroups.CollectionChanged -= ChildrenChanged;
            }
        }
    }
}
