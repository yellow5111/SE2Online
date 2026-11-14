using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

if (args.Length is < 1 or > 2) throw new ArgumentException("Usage: ControlPanelChecks <Game2 directory> [patched Y51_SE2Online.dll]");
var game = Path.GetFullPath(args[0]);
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    var path = Path.Combine(game, name.Name + ".dll");
    if (!File.Exists(path) && args.Length > 1) path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1]))!, name.Name + ".dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};
var runtime = Directory.GetFiles(game, "*.dll").Select(Path.GetFileNameWithoutExtension)
    .Where(n => n != null && (n.StartsWith("VRage.") || n.StartsWith("Game2."))
        && !new[] { "Editor", "Content", "Generator", "ShaderBuilder", "CodeAnalysis", "Native" }.Any(n.Contains))
    .Select(n => Assembly.Load(n!)).ToArray();
var library = Assembly.Load("VRage.Library");
var managerType = library.GetType("Keen.VRage.Library.Reflection.MetadataManager", true)!;
var manager = library.GetType("Keen.VRage.Library.Utils.Singleton`1", true)!.MakeGenericType(managerType).GetProperty("Instance")!.GetValue(null);
managerType.GetMethod("PushContext", [typeof(IEnumerable<Assembly>)])!.Invoke(manager, [runtime]);
var wireModel = Assembly.Load("Game2.Simulation").GetType("Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel.ControlPanelGroupModel", true)!;
var wireSession = Assembly.Load("VRage.Multiplayer").GetType("Keen.VRage.Multiplayer.Replications.ObservableObjectReplicationServerSessionComponent", true)!;
var wireInstance = Activator.CreateInstance(wireSession)!;
var wireInfo = wireModel.GetMethod("GetTypeInfo")!.Invoke(Activator.CreateInstance(wireModel), null)!;
var getMembers = wireSession.BaseType!.GetMethod("GetReplicatedMembers", BindingFlags.Instance | BindingFlags.NonPublic)!;
string[] WireShape() => ((IEnumerable)getMembers.Invoke(wireInstance, [wireInfo])!).Cast<object>().Select(member =>
{
    var accessor = Value(member, "Accessor");
    return $"{Value(accessor, "Name")}|{Value(accessor, "Member")}|{Value(accessor, "Serializers")}|{Value(member, "CanWrite")}";
}).ToArray();
var originalWireShape = WireShape();
Assembly? plugin = null;
Type? harmonyType = null;
if (args.Length > 1)
{
    plugin = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[1]));
    harmonyType = Assembly.Load("0Harmony").GetType("HarmonyLib.Harmony", true)!;
    var harmony = Activator.CreateInstance(harmonyType, "SE2Online.ControlPanelChecks");
    foreach (var name in new[] { "SyncEngine.NameSync", "KeenNetcode.ControlPanelGroupReplication" })
        plugin.GetType("Y51_SE2Online." + name, true)!.GetMethod("Install", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [harmony]);
}

const BindingFlags hidden = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
var simulation = Assembly.Load("Game2.Simulation");
var multiplayer = Assembly.Load("VRage.Multiplayer");
Type Sim(string name) => simulation.GetType("Keen.Game2.Simulation." + name, true)!;
var groupType = Sim("WorldObjects.CubeBlocks.GridGroup");
var groupsType = Sim("WorldObjects.CubeBlocks.CubeGridGroupsComponent");
var listType = Sim("StreamedUI.Terminal.ControlPanel.ControlPanelListModel");
var modelType = Sim("StreamedUI.Terminal.ControlPanel.ControlPanelGroupModelServer");
var gridModelType = Sim("StreamedUI.Terminal.ControlPanel.ControlPanelGridModelServer");
var cubeGridType = Sim("WorldObjects.CubeGrids.CubeGridComponent");
var panelType = Sim("StreamedUI.Terminal.ControlPanel.StreamedControlPanelSessionComponent");
var baseModel = modelType.BaseType!;
var replicationType = multiplayer.GetType("Keen.VRage.Multiplayer.Replications.ObservableObjectReplicationServerSessionComponent", true)!;
var hierarchyType = replicationType.GetNestedType("ObservedHierarchy", BindingFlags.NonPublic)!;
var replication = Activator.CreateInstance(replicationType)!;
var source = Activator.CreateInstance(groupType)!;
groupType.GetProperty("DisplayName")!.SetValue(source, "shared group");
var child = Activator.CreateInstance(groupType)!;
groupType.GetProperty("DisplayName")!.SetValue(child, "child group");
groupType.GetProperty("ParentGroup")!.SetValue(child, source);
IList Children(object group) => (IList)groupType.GetField("SubGroups")!.GetValue(group)!;
Children(source).Add(child);
var groups = Activator.CreateInstance(groupsType)!;
var dcs = Assembly.Load("VRage.DCS");
var componentType = dcs.GetType("Keen.VRage.DCS.Components.Component", true)!;
var entityProperty = componentType.GetProperty("Entity")!;
var entity = RuntimeHelpers.GetUninitializedObject(entityProperty.PropertyType);
var grid = RuntimeHelpers.GetUninitializedObject(cubeGridType);
var components = Array.CreateInstance(componentType, 2); components.SetValue(groups, 0); components.SetValue(grid, 1);
var compositionProperty = entity.GetType().GetProperty("CompositionData")!;
var composition = (IDictionary)Activator.CreateInstance(compositionProperty.PropertyType, hidden, null, [2], null)!;
var defaultTag = dcs.GetType("Keen.VRage.DCS.Components.DefaultTag", true)!.GetMethod("Get", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, [typeof(Type)])!;
foreach (var (component, index) in new[] { (groups, 0), (grid, 1) })
{
    entityProperty.SetValue(component, entity);
    var tag = defaultTag.Invoke(null, [component.GetType()])!;
    composition.Add((int)tag.GetType().GetField("ID")!.GetValue(tag)!, index);
}
var arrayFactory = typeof(System.Collections.Immutable.ImmutableArray).GetMethods().Single(m => m.Name == "CreateRange" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsGenericType && m.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == typeof(IEnumerable<>)).MakeGenericMethod(componentType);
entity.GetType().GetField("Components")!.SetValue(entity, arrayFactory.Invoke(null, [components]));
compositionProperty.SetValue(entity, composition);
object Scope()
{
    var scope = RuntimeHelpers.GetUninitializedObject(gridModelType);
    gridModelType.GetProperty("Grid")!.SetValue(scope, grid);
    return scope;
}
object Model(object scope, object? group = null) => panelType.GetMethod("CreateEmptyGroupModel", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [group ?? source, scope])!;
object Models(object scope)
{
    var parent = Model(scope);
    ((IList)baseModel.GetProperty("SubGroups")!.GetValue(parent)!).Add(Model(scope, child));
    return parent;
}
object Root(object model)
{
    var list = Activator.CreateInstance(listType)!;
    listType.GetProperty("Groups")!.PropertyType.GetMethod("Add")!.Invoke(listType.GetProperty("Groups")!.GetValue(list), [model]);
    return list;
}
object Hierarchy(object root)
{
    var hierarchy = Activator.CreateInstance(hierarchyType, hidden, null, [replication], null)!;
    Check((bool)hierarchyType.GetMethod("TryReplicateRoot")!.Invoke(hierarchy, [root])!, "native root registration");
    return hierarchy;
}
List<object> TrackedGroups(object hierarchy) => ((IDictionary)hierarchyType.GetField("_observed", hidden)!.GetValue(hierarchy)!).Keys.Cast<object>().Where(groupType.IsInstanceOfType).ToList();
object Group(object model) => baseModel.GetProperty("Group")!.GetValue(model)!;
string Name(object model) => (string)baseModel.GetProperty("DisplayName")!.GetValue(model)!;
bool Visible(object model) => (bool)baseModel.GetProperty("ShowInTerminal")!.GetValue(model)!;
var scope1 = Scope(); var scope2 = Scope();
var first = Models(scope1); var second = Models(scope2);
var h1 = Hierarchy(Root(first)); var h2 = Hierarchy(Root(second));
var a = TrackedGroups(h1); var b = TrackedGroups(h2);
Check(a.Count == 2 && b.Count == 2, "both native panel hierarchies must track parent and child groups (stock second registration loses them)");
Check(!a.Any(x => b.Any(y => ReferenceEquals(x, y))), "per-viewer replicated group identities must be isolated");
Check(ReferenceEquals(Group(first), source) && ReferenceEquals(Group(second), source), "server operations must retain original group identity");
var id = groupType.GetField("Id")!;
object Replica(List<object> tracked, object original) => tracked.Single(x => Equals(id.GetValue(original), id.GetValue(x)));
var parent1 = Replica(a, source); var parent2 = Replica(b, source);
Check(originalWireShape.SequenceEqual(WireShape()), "replication member names, order, metadata and writability must preserve wire format");
if (plugin != null)
{
    var patch = plugin.GetType("Y51_SE2Online.KeenNetcode.ControlPanelGroupReplication", true)!;
    var restore = patch.GetMethod("RestoreGroupArgument", BindingFlags.Static | BindingFlags.NonPublic)!;
    foreach (var proxy in a.Concat(b))
    {
        object?[] values = [proxy]; restore.Invoke(null, values);
        Check(ReferenceEquals(values[0], Equals(id.GetValue(proxy), id.GetValue(source)) ? source : child), "toolbar group requests must restore original source identity");
    }
    var unknown = Activator.CreateInstance(groupType)!;
    object?[] unknownValues = [unknown]; restore.Invoke(null, unknownValues);
    Check(ReferenceEquals(unknownValues[0], unknown), "unknown groups must remain subject to native request validation");
    var request = panelType.GetMethod("RequestSetTileGroup", hidden)!;
    var stateMachine = request.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
    Check(stateMachine.GetField("group", hidden)?.FieldType == groupType, "toolbar request state machine must expose the expected group field");
    var moveNext = stateMachine.GetMethod("MoveNext", hidden)!;
    var info = harmonyType!.GetMethod("GetPatchInfo", [typeof(MethodBase)])!.Invoke(null, [moveNext])!;
    Check(((IEnumerable)Value(info, "Prefixes")).Cast<object>().Any(p => Equals(Value(p, "PatchMethod"), restore)), "toolbar source restoration must be installed on actual native request MoveNext");
}
Check(ReferenceEquals(Children(parent1)[0], Replica(a, child)) && ReferenceEquals(Children(parent2)[0], Replica(b, child)), "nested group and child model must share the same replica within one viewer");
Check((int)groupType.GetProperty("Depth")!.GetValue(Replica(a, child))! == 2, "nested group depth must survive projection");
groupType.GetProperty("DisplayName")!.SetValue(source, "renamed");
Check(Name(first) == "renamed" && Name(second) == "renamed", "source rename must reach both viewers");
Check((string)groupType.GetProperty("DisplayName")!.GetValue(parent1)! == "renamed" && (string)groupType.GetProperty("DisplayName")!.GetValue(parent2)! == "renamed", "source rename must reach both replicas");
groupType.GetProperty("ShowInTerminal")!.SetValue(source, false);
Check(!Visible(first) && !Visible(second), "source visibility must reach both viewers");
Check(!(bool)groupType.GetProperty("ShowInTerminal")!.GetValue(parent1)! && !(bool)groupType.GetProperty("ShowInTerminal")!.GetValue(parent2)!, "source visibility must reach both replicas");
var added = Activator.CreateInstance(groupType)!;
groupType.GetProperty("ParentGroup")!.SetValue(added, source);
Children(source).Add(added);
Check(Children(parent1).Count == 2 && Children(parent2).Count == 2, "new nested groups must reach both viewers");
Children(source).Remove(added);
Check(Children(parent1).Count == 1 && Children(parent2).Count == 1, "removed nested groups must leave both viewers");
groupType.GetProperty("Depth")!.SetValue(child, 3);
Check((int)groupType.GetProperty("Depth")!.GetValue(Replica(a, child))! == 3 && (int)groupType.GetProperty("Depth")!.GetValue(Replica(b, child))! == 3, "depth changes must reach both viewers");
baseModel.GetProperty("DisplayName")!.SetValue(first, "edited by first viewer");
Check((string)groupType.GetProperty("DisplayName")!.GetValue(source)! == "edited by first viewer" && Name(second) == "edited by first viewer", "viewer rename must change original group and reach other viewer");
baseModel.GetProperty("ShowInTerminal")!.SetValue(second, true);
Check((bool)groupType.GetProperty("ShowInTerminal")!.GetValue(source)! && Visible(first), "viewer visibility edit must change original group and reach other viewer");
hierarchyType.GetMethod("DisposeHierarchy")!.Invoke(h1, null);
((IDisposable)first).Dispose();
var handlers = entity.GetType().GetField("ExternalHandlers", hidden)!;
handlers.SetValue(entity, Activator.CreateInstance(handlers.FieldType));
((IDisposable)scope1).Dispose();
groupType.GetProperty("DisplayName")!.SetValue(source, "after first close");
Check(Name(second) == "after first close", "remaining viewer must receive edits after first closes");
Check((string)groupType.GetProperty("DisplayName")!.GetValue(parent1)! == "edited by first viewer", "closed scope must unsubscribe source mirrors");
Check((string)groupType.GetProperty("DisplayName")!.GetValue(parent2)! == "after first close", "remaining replica must receive edits after first closes");
var scope3 = Scope(); var reopened = Models(scope3); var h3 = Hierarchy(Root(reopened));
Check(TrackedGroups(h3).Count == 2 && Name(reopened) == "after first close" && Visible(reopened), "reopened panel must retain current group state");
hierarchyType.GetMethod("DisposeHierarchy")!.Invoke(h2, null);
hierarchyType.GetMethod("DisposeHierarchy")!.Invoke(h3, null);
((IDisposable)second).Dispose(); ((IDisposable)reopened).Dispose();
((IDisposable)scope2).Dispose(); ((IDisposable)scope3).Dispose();
Console.WriteLine("PASS native two-viewer control-panel hierarchy registration, wire shape, toolbar source aliases, live group mirrors, rename, visibility, close and reopen. No game/world/transport started.");

static void Check(bool ok, string message)
{
    if (!ok) throw new InvalidOperationException(message);
}

static object Value(object instance, string name) => instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(instance)
    ?? instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(instance)!;
