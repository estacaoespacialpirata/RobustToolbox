using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Text;
using System.Text.RegularExpressions;
using Robust.Client.Debugging;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Asynchronous;
using Robust.Shared.Console;
using Robust.Shared.ContentPack;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Localization;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Network;
using Robust.Shared.Reflection;
using Robust.Shared.Serialization;
using Robust.Shared.Utility;
using Robust.Shared.ViewVariables;
using static Robust.Client.UserInterface.Controls.BoxContainer;

namespace Robust.Client.Console.Commands
{
    internal sealed class DumpEntitiesCommand : IConsoleCommand
    {
        public string Command => "dumpentities";
        public string Help => "Dump entity list";
        public string Description => "Dumps entity list of UIDs and prototype.";

        public void Execute(IConsoleShell shell, string argStr, string[] args)
        {
            var entityManager = IoCManager.Resolve<IEntityManager>();

            foreach (var e in entityManager.GetEntities().OrderBy(e => e))
            {
                shell.WriteLine($"entity {e}, {entityManager.GetComponent<MetaDataComponent>(e).EntityPrototype?.ID}, {entityManager.GetComponent<TransformComponent>(e).Coordinates}.");
            }
        }
    }

    internal sealed class GetComponentRegistrationCommand : IConsoleCommand
    {
        public string Command => "getcomponentregistration";
        public string Help => "Usage: getcomponentregistration <componentName>";
        public string Description => "Gets component registration information";

        public void Execute(IConsoleShell shell, string argStr, string[] args)
        {
            if (args.Length < 1)
            {
                shell.WriteLine(Help);
                return;
            }

            var componentFactory = IoCManager.Resolve<IComponentFactory>();

            try
            {
                var registration = componentFactory.GetRegistration(args[0]);

                var message = new StringBuilder($"'{registration.Name}': (type: {registration.Type}, ");
                if (registration.NetID == null)
                {
                    message.Append("no Net ID");
                }
                else
                {
                    message.Append($"net ID: {registration.NetID}");
                }

                message.Append($", References:");

                shell.WriteLine(message.ToString());

                foreach (var type in registration.References)
                {
                    shell.WriteLine($"  {type}");
                }
            }
            catch (UnknownComponentException)
            {
                shell.WriteError($"No registration found for '{args[0]}'");
            }
        }
    }

    internal sealed class ToggleMonitorCommand : IConsoleCommand
    {
        public string Command => "monitor";

        public string Description => Loc.GetString("cmd-monitor-desc");

        public string Help
        {
            get
            {
                var monitors = string.Join(", ", Enum.GetNames<DebugMonitor>());
                return Loc.GetString("cmd-monitor-help", ("monitors", monitors));
            }
        }

        public void Execute(IConsoleShell shell, string argStr, string[] args)
        {
            var monitors = IoCManager.Resolve<IUserInterfaceManager>().DebugMonitors;

            if (args.Length != 1)
            {
                shell.WriteLine(Loc.GetString("cmd-monitor-arg-count"));
                return;
            }

            var monitorArg = args[0];
            if (monitorArg.Equals("-all", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var monitor in Enum.GetValues<DebugMonitor>())
                {
                    monitors.SetMonitor(monitor, false);
                }

                return;
            }

            if (monitorArg.Equals("+all", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var monitor in Enum.GetValues<DebugMonitor>())
                {
                    monitors.SetMonitor(monitor, true);
                }

                return;
            }

            if (!Enum.TryParse(monitorArg, true, out DebugMonitor parsedMonitor))
            {
                shell.WriteError(Loc.GetString("cmd-monitor-invalid-name"));
                return;
            }

            monitors.ToggleMonitor(parsedMonitor);
        }

        public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
        {
            if (args.Length == 1)
            {
                var allOptions = new CompletionOption[]
                {
                    new("-all", Loc.GetString("cmd-monitor-minus-all-hint")),
                    new("+all", Loc.GetString("cmd-monitor-plus-all-hint"))
                };

                var options = allOptions.Concat(Enum.GetNames<DebugMonitor>().Select(c => new CompletionOption(c)));
                return CompletionResult.FromHintOptions(options, Loc.GetString("cmd-monitor-arg-monitor"));
            }

            return CompletionResult.Empty;
        }
    }

    internal sealed class ExceptionCommand : IConsoleCommand
    {
        public string Command => "fuck";
        public string Help => "Throws an exception";
        public string Description => "Throws an exception";

        public void Execute(IConsoleShell shell, string argStr, string[] args)
        {
            throw new InvalidOperationException("Fuck");
        }
    }

    internal sealed class ShowPositionsCommand : IConsoleCommand
    {
        public string Command => "showpos";
        public string Help => "";
        public string Description => "Enables debug drawing over all entity positions in the game.";

        public void Execute(IConsoleShell shell, string argStr, string[] args)
        {
            var mgr = IoCManager.Resolve<IEntitySystemManager>().GetEntitySystem<DebugDrawingSystem>();
            mgr.DebugPositions = !mgr.DebugPositions;
        }
    }

    internal sealed class ShowRayCommand : IConsoleCommand
    {
        public string Command => "showrays";
        public string Help => "Usage: showrays <raylifetime>";
        public string Description => "Toggles debug drawing of physics rays. An integer for <raylifetime> must be provided";

        public void Execute(IConsoleShell shell, string argStr, string[] args)
        {
            if (args.Length != 1)
            {
                shell.WriteLine(Help);
                return;
            }

            if (!float.TryParse(args[0], out var duration))
            {
                shell.WriteError($"{args[0]} is not a valid float.");
                return;
            }

            var mgr = EntitySystem.Get<DebugRayDrawingSystem>();
            mgr.DebugDrawRays = !mgr.DebugDrawRays;
            shell.WriteError("Toggled showing rays to:" + mgr.DebugDrawRays);
            mgr.DebugRayLifetime = TimeSpan.FromSeconds(duration);
        }
    }

    internal sealed class DisconnectCommand : IConsoleCommand
    {
        public string Command => "disconnect";
        public string Help => "";
        public string Description => "Immediately disconnect from the server and go back to the main menu.";

        public void Execute(IConsoleShell shell, string argStr, string[] args)
        {
            IoCManager.Resolve<IClientNetManager>().ClientDisconnect("Disconnect command used.");
        }
    }

    internal sealed class EntityInfoCommand : IConsoleCommand
    {
        public string Command => "entfo";

        public string Help =>
            "entfo <entityuid>\nThe entity UID can be prefixed with 'c' to convert it to a client entity UID.";

        public string Description => "Displays verbose diagnostics for an entity.";

        public void Execute(IConsoleShell shell, string argStr, string[] args)
        {
            if (args.Length != 1)
            {
                shell.WriteLine(Help);
                return;
            }

            if ((!new Regex(@"^c?[0-9]+$").IsMatch(args[0])))
            {
                shell.WriteError("Malformed UID");
                return;
            }

            var uid = EntityUid.Parse(args[0]);
            var entmgr = IoCManager.Resolve<IEntityManager>();
            if (!entmgr.EntityExists(uid))
            {
                shell.WriteError("That entity does not exist. Sorry lad.");
                return;
            }
            var meta = entmgr.GetComponent<MetaDataComponent>(uid);
            shell.WriteLine($"{uid}: {meta.EntityPrototype?.ID}/{meta.EntityName}");
            shell.WriteLine($"init/del/lmt: {meta.EntityInitialized}/{meta.EntityDeleted}/{meta.EntityLastModifiedTick}");
            foreach (var component in entmgr.GetComponents(uid))
            {
                shell.WriteLine(component.ToString() ?? "");
                if (component is IComponentDebug debug)
                {
                    foreach (var line in debug.GetDebugString().Split('\n'))
                    {
                        if (string.IsNullOrWhiteSpace(line))
                        {
                            continue;
                        }

                        shell.WriteLine("\t" + line);
                    }
                }
            }
        }
    }

    internal sealed class SnapGridGetCell : IConsoleCommand
    {
        public string Command => "sggcell";
        public string Help => "sggcell <gridID> <vector2i>\nThat vector2i param is in the form x<int>,y<int>.";
        public string Description => "Lists entities on a snap grid cell.";

        public void Execute(IConsoleShell shell, string argStr, string[] args)
        {
            if (args.Length != 2)
            {
                shell.WriteLine(Help);
                return;
            }

            string indices = args[1];

            if (!EntityUid.TryParse(args[0], out var gridUid))
            {
                shell.WriteError($"{args[0]} is not a valid entity UID.");
                return;
            }

            if (!new Regex(@"^-?[0-9]+,-?[0-9]+$").IsMatch(indices))
            {
                shell.WriteError("mapIndicies must be of form x<int>,y<int>");
                return;
            }

            var mapMan = IoCManager.Resolve<IMapManager>();
            if (mapMan.TryGetGrid(gridUid, out var grid))
            {
                foreach (var entity in grid.GetAnchoredEntities(new Vector2i(
                    int.Parse(indices.Split(',')[0], CultureInfo.InvariantCulture),
                    int.Parse(indices.Split(',')[1], CultureInfo.InvariantCulture))))
                {
                    shell.WriteLine(entity.ToString());
                }
            }
            else
            {
                shell.WriteError("grid does not exist");
            }
        }
    }

    internal sealed class SetPlayerName : IConsoleCommand
    {
        public string Command => "overrideplayername";
        public string Description => "Changes the name used when attempting to connect to the server.";
        public string Help => Command + " <name>";

        public void Execute(IConsoleShell shell, string argStr, string[] args)
        {
            if (args.Length < 1)
            {
                shell.WriteLine(Help);
                return;
            }
            var client = IoCManager.Resolve<IBaseClient>();
            client.PlayerNameOverride = args[0];

            shell.WriteLine($"Overriding player name to \"{args[0]}\".");
        }
    }

    internal sealed class LoadResource : IConsoleCommand
    {
        public string Command => "ldrsc";
        public string Description => "Pre-caches a resource.";
        public string Help => "ldrsc <path> <type>";

        public void Execute(IConsoleShell shell, string argStr, string[] args)
        {
            if (args.Length < 2)
            {
                shell.WriteLine(Help);
                return;
            }
            var resourceCache = IoCManager.Resolve<IResourceCache>();
            var reflection = IoCManager.Resolve<IReflectionManager>();
            Type type;

            try
            {
                type = reflection.LooseGetType(args[1]);
            }
            catch(ArgumentException)
            {
                shell.WriteError("Unable to find type");
                return;
            }

            var getResourceMethod =
                resourceCache
                    .GetType()
                    .GetMethod("GetResource", new[] { typeof(string), typeof(bool) });
            DebugTools.Assert(getResourceMethod != null);
            var generic = getResourceMethod!.MakeGenericMethod(type);
            generic.Invoke(resourceCache, new object[] { args[0], true });
        }
    }

    internal sealed class ReloadResource : IConsoleCommand
    {
        public string Command => "rldrsc";
        public string Description => "Reloads a resource.";
        public string Help => "rldrsc <path> <type>";

        public void Execute(IConsoleShell shell, string argStr, string[] args)
        {
            if (args.Length < 2)
            {
                shell.WriteLine(Help);
                return;
            }
            var resourceCache = IoCManager.Resolve<IResourceCache>();
            var reflection = IoCManager.Resolve<IReflectionManager>();

            Type type;
            try
            {
                type = reflection.LooseGetType(args[1]);
            }
            catch(ArgumentException)
            {
                shell.WriteError("Unable to find type");
                return;
            }

            var getResourceMethod = resourceCache.GetType().GetMethod("ReloadResource", new[] { typeof(string) });
            DebugTools.Assert(getResourceMethod != null);
            var generic = getResourceMethod!.MakeGenericMethod(type);
            generic.Invoke(resourceCache, new object[] { args[0] });
        }
    }

    internal sealed class GridTileCount : IConsoleCommand
    {
        public string Command => "gridtc";
        public string Description => "Gets the tile count of a grid";
        public string Help => "Usage: gridtc <gridId>";

        public void Execute(IConsoleShell shell, string argStr, string[] args)
        {
            if (args.Length != 1)
            {
                shell.WriteLine(Help);
                return;
            }

            if (!EntityUid.TryParse(args[0], out var gridUid))
            {
                shell.WriteLine($"{args[0]} is not a valid entity UID.");
                return;
            }

            var mapManager = IoCManager.Resolve<IMapManager>();
            if (mapManager.TryGetGrid(gridUid, out var grid))
            {
                shell.WriteLine(grid.GetAllTiles().Count().ToString());
            }
            else
            {
                shell.WriteError($"No grid exists with id {gridUid}");
            }
        }
    }

    internal sealed class GuiDumpCommand : IConsoleCommand
    {
        public string Command => "guidump";
        public string Description => "Dump GUI tree to /guidump.txt in user data.";
        public string Help => "guidump";

        public void Execute(IConsoleShell shell, string argStr, string[] args)
        {
            var uiMgr = IoCManager.Resolve<IUserInterfaceManager>();
            var res = IoCManager.Resolve<IResourceManager>();

            using var writer = res.UserData.OpenWriteText(new ResourcePath("/guidump.txt"));

            foreach (var root in uiMgr.AllRoots)
            {
                writer.WriteLine($"ROOT: {root}");
                _writeNode(root, 0, writer);
                writer.WriteLine("---------------");
            }

            shell.WriteLine("Saved guidump");
        }

        private static void _writeNode(Control control, int indents, TextWriter writer)
        {
            var indentation = new string(' ', indents * 2);
            writer.WriteLine("{0}{1}", indentation, control);
            foreach (var (key, value) in PropertyValuesFor(control))
            {
                writer.WriteLine("{2} * {0}: {1}", key, value, indentation);
            }

            foreach (var child in control.Children)
            {
                _writeNode(child, indents + 1, writer);
            }
        }

        internal static List<(string, string)> PropertyValuesFor(Control control)
        {
            var members = new List<(string, string)>();
            var type = control.GetType();

            foreach (var fieldInfo in type.GetAllFields())
            {
                if (!ViewVariablesUtility.TryGetViewVariablesAccess(fieldInfo, out _))
                {
                    continue;
                }

                members.Add((fieldInfo.Name, fieldInfo.GetValue(control)?.ToString() ?? "null"));
            }

            foreach (var propertyInfo in type.GetAllProperties())
            {
                if (!ViewVariablesUtility.TryGetViewVariablesAccess(propertyInfo, out _))
                {
                    continue;
                }

                members.Add((propertyInfo.Name, propertyInfo.GetValue(control)?.ToString() ?? "null"));
            }

            foreach (var (attachedProperty, value) in control.AllAttachedProperties)
            {
                members.Add(($"{attachedProperty.OwningType.Name}.{attachedProperty.Name}",
                    value?.ToString() ?? "null"));
            }

            members.Sort((a, b) => string.Compare(a.Item1, b.Item1, StringComparison.Ordinal));
            return members;
        }
    }

    internal sealed class UITestCommand : IConsoleCommand
    {
        public string Command => "uitest";
        public string Description => "Open a dummy UI testing window";
        public string Help => "uitest";

        public void Execute(IConsoleShell shell, string argStr, string[] args)
        {
            var window = new DefaultWindow { MinSize = (500, 400)};
            var tabContainer = new TabContainer();
            window.Contents.AddChild(tabContainer);
            var scroll = new ScrollContainer();
            tabContainer.AddChild(scroll);
            //scroll.SetAnchorAndMarginPreset(Control.LayoutPreset.Wide);
            var vBox = new BoxContainer
            {
                Orientation = LayoutOrientation.Vertical
            };
            scroll.AddChild(vBox);

            var progressBar = new ProgressBar { MaxValue = 10, Value = 5 };
            vBox.AddChild(progressBar);

            var optionButton = new OptionButton();
            optionButton.AddItem("Honk");
            optionButton.AddItem("Foo");
            optionButton.AddItem("Bar");
            optionButton.AddItem("Baz");
            optionButton.OnItemSelected += eventArgs => optionButton.SelectId(eventArgs.Id);
            vBox.AddChild(optionButton);

            var tree = new Tree { VerticalExpand = true };
            var root = tree.CreateItem();
            root.Text = "Honk!";
            var child = tree.CreateItem();
            child.Text = "Foo";
            for (var i = 0; i < 20; i++)
            {
                child = tree.CreateItem();
                child.Text = $"Bar {i}";
            }

            vBox.AddChild(tree);

            var rich = new RichTextLabel();
            var message = new FormattedMessage();
            message.AddText("Foo\n");
            message.PushColor(Color.Red);
            message.AddText("Bar");
            message.Pop();
            rich.SetMessage(message);
            vBox.AddChild(rich);

            var itemList = new ItemList();
            tabContainer.AddChild(itemList);
            for (var i = 0; i < 10; i++)
            {
                itemList.AddItem(i.ToString());
            }

            var grid = new GridContainer { Columns = 3 };
            tabContainer.AddChild(grid);
            for (var y = 0; y < 3; y++)
            {
                for (var x = 0; x < 3; x++)
                {
                    grid.AddChild(new Button
                    {
                        MinSize = (50, 50),
                        Text = $"{x}, {y}"
                    });
                }
            }

            var group = new ButtonGroup();
            var vBoxRadioButtons = new BoxContainer
            {
                Orientation = LayoutOrientation.Vertical
            };
            for (var i = 0; i < 10; i++)
            {
                vBoxRadioButtons.AddChild(new Button
                {
                    Text = i.ToString(),
                    Group = group
                });

                // ftftftftftftft
            }

            tabContainer.AddChild(vBoxRadioButtons);

            TabContainer.SetTabTitle(vBoxRadioButtons, "Radio buttons!!");

            tabContainer.AddChild(new BoxContainer
            {
                Orientation = LayoutOrientation.Vertical,
                Name = "Slider",
                Children =
                {
                    new Slider()
                }
            });

            tabContainer.AddChild(new SplitContainer
            {
                Orientation = SplitContainer.SplitOrientation.Horizontal,
                Children =
                {
                    new PanelContainer
                    {
                        PanelOverride = new StyleBoxFlat {BackgroundColor = Color.Red},
                        Children =
                        {
                            new Label{  Text = "FOOBARBAZ"},
                        }
                    },
                    new PanelContainer
                    {
                        PanelOverride = new StyleBoxFlat {BackgroundColor = Color.Blue},
                        Children =
                        {
                            new Label{  Text = "FOOBARBAZ"},
                        }
                    },
                }
            });

            window.OpenCentered();
        }
    }

    internal sealed class SetClipboardCommand : IConsoleCommand
    {
        public string Command => "setclipboard";
        public string Description => "Sets the system clipboard";
        public string Help => "setclipboard <text>";

        public void Execute(IConsoleShell shell, string argStr, string[] args)
        {
            var mgr = IoCManager.Resolve<IClipboardManager>();
            mgr.SetText(args[0]);
        }
    }

    internal sealed class GetClipboardCommand : IConsoleCommand
    {
        public string Command => "getclipboard";
        public string Description => "Gets the system clipboard";
        public string Help => "getclipboard";

        public async void Execute(IConsoleShell shell, string argStr, string[] args)
        {
            var mgr = IoCManager.Resolve<IClipboardManager>();
            shell.WriteLine(await mgr.GetText());
        }
    }

    internal sealed class ToggleLight : IConsoleCommand
    {
        public string Command => "togglelight";
        public string Description => "Toggles light rendering.";
        public string Help => "togglelight";

        public void Execute(IConsoleShell shell, string argStr, string[] args)
        {
            var mgr = IoCManager.Resolve<ILightManager>();
            if (!mgr.LockConsoleAccess)
                mgr.Enabled = !mgr.Enabled;
        }
    }

    internal sealed class ToggleFOV : IConsoleCommand
    {
        public string Command => "togglefov";
        public string Description => "Toggles fov for client.";
        public string Help => "togglefov";

        public void Execute(IConsoleShell shell, string argStr, string[] args)
        {
          var mgr = IoCManager.Resolve<IEyeManager>();
          if (mgr.CurrentEye != null)
              mgr.CurrentEye.DrawFov = !mgr.CurrentEye.DrawFov;
        }
    }

    internal sealed class ToggleHardFOV : IConsoleCommand
    {
        public string Command => "togglehardfov";
        public string Description => "Toggles hard fov for client (for debugging space-station-14#2353).";
        public string Help => "togglehardfov";

        public void Execute(IConsoleShell shell, string argStr, string[] args)
        {
            var mgr = IoCManager.Resolve<ILightManager>();
            if (!mgr.LockConsoleAccess)
                mgr.DrawHardFov = !mgr.DrawHardFov;
        }
    }

    internal sealed class ToggleShadows : IConsoleCommand
    {
        public string Command => "toggleshadows";
        public string Description => "Toggles shadow rendering.";
        public string Help => "toggleshadows";

        public void Execute(IConsoleShell shell, string argStr, string[] args)
        {
            var mgr = IoCManager.Resolve<ILightManager>();
            if (!mgr.LockConsoleAccess)
                mgr.DrawShadows = !mgr.DrawShadows;
        }
    }
    internal sealed class ToggleLightBuf : IConsoleCommand
    {
        public string Command => "togglelightbuf";
        public string Description => "Toggles lighting rendering. This includes shadows but not FOV.";
        public string Help => "togglelightbuf";

        public void Execute(IConsoleShell shell, string argStr, string[] args)
        {
            var mgr = IoCManager.Resolve<ILightManager>();
            if (!mgr.LockConsoleAccess)
                mgr.DrawLighting = !mgr.DrawLighting;
        }
    }

    internal sealed class ChunkInfoCommand : IConsoleCommand
    {
        public string Command => "chunkinfo";
        public string Description => "Gets info about a chunk under your mouse cursor.";
        public string Help => Command;

        public void Execute(IConsoleShell shell, string argStr, string[] args)
        {
            var mapMan = IoCManager.Resolve<IMapManager>();
            var inputMan = IoCManager.Resolve<IInputManager>();
            var eyeMan = IoCManager.Resolve<IEyeManager>();

            var mousePos = eyeMan.ScreenToMap(inputMan.MouseScreenPosition);

            if (!mapMan.TryFindGridAt(mousePos, out var grid))
            {
                shell.WriteLine("No grid under your mouse cursor.");
                return;
            }

            var internalGrid = (IMapGridInternal)grid;

            var chunkIndex = grid.LocalToChunkIndices(grid.MapToGrid(mousePos));
            var chunk = internalGrid.GetChunk(chunkIndex);

            shell.WriteLine($"worldBounds: {internalGrid.CalcWorldAABB(chunk)} localBounds: {chunk.CachedBounds}");
        }
    }

    internal sealed class ReloadShadersCommand : IConsoleCommand
    {

        public string Command => "rldshader";

        public string Description => "Reloads all shaders";

        public string Help => "rldshader";

        public static Dictionary<string, FileSystemWatcher>? _watchers;

        public static ConcurrentDictionary<string, bool>? _reloadShadersQueued = new();

        public void Execute(IConsoleShell shell, string argStr, string[] args)
        {
            IResourceCacheInternal resC;
            if (args.Length == 1)
            {
                if (args[0] == "+watch")
                {
                    if (_watchers != null)
                    {
                        shell.WriteLine("Already watching.");
                        return;
                    }
                    resC = IoCManager.Resolve<IResourceCacheInternal>();

                    _watchers = new Dictionary<string, FileSystemWatcher>();

                    var stringComparer = PathHelpers.IsFileSystemCaseSensitive()
                        ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

                    var reversePathResolution = new ConcurrentDictionary<string, HashSet<ResourcePath>>(stringComparer);

                    var taskManager = IoCManager.Resolve<ITaskManager>();

                    var shaderCount = 0;
                    var created = 0;
                    var dirs = new ConcurrentDictionary<string, SortedSet<string>>(stringComparer);
                    foreach (var (path, src) in resC.GetAllResources<ShaderSourceResource>())
                    {
                        if (!resC.TryGetDiskFilePath(path, out var fullPath))
                        {
                            throw new NotImplementedException();
                        }

                        reversePathResolution.GetOrAdd(fullPath, _ => new HashSet<ResourcePath>()).Add(path);

                        var dir = Path.GetDirectoryName(fullPath)!;
                        var fileName = Path.GetFileName(fullPath);
                        dirs.GetOrAdd(dir, _ => new SortedSet<string>(stringComparer))
                            .Add(fileName);

                        foreach (var inc in src.ParsedShader.Includes)
                        {
                            if (!resC.TryGetDiskFilePath(inc, out var incFullPath))
                            {
                                throw new NotImplementedException();
                            }

                            reversePathResolution.GetOrAdd(incFullPath, _ => new HashSet<ResourcePath>()).Add(path);

                            var incDir = Path.GetDirectoryName(incFullPath)!;
                            var incFileName = Path.GetFileName(incFullPath);
                            dirs.GetOrAdd(incDir, _ => new SortedSet<string>(stringComparer))
                                .Add(incFileName);
                        }

                        ++shaderCount;
                    }

                    foreach (var (dir, files) in dirs)
                    {
                        if (_watchers.TryGetValue(dir, out var watcher))
                        {
                            throw new NotImplementedException();
                        }

                        watcher = new FileSystemWatcher(dir);
                        watcher.Changed += (_, ev) =>
                        {
                            if (_reloadShadersQueued!.TryAdd(ev.FullPath, true))
                            {
                                taskManager.RunOnMainThread(() =>
                                {
                                    var resPaths = reversePathResolution[ev.FullPath];
                                    foreach (var resPath in resPaths)
                                    {
                                        try
                                        {
                                            IoCManager.Resolve<IResourceCache>()
                                                .ReloadResource<ShaderSourceResource>(resPath);
                                            shell.WriteLine($"Reloaded shader: {resPath}");
                                        }
                                        catch (Exception)
                                        {
                                            shell.WriteLine($"Failed to reload shader: {resPath}");
                                        }

                                        _reloadShadersQueued.TryRemove(ev.FullPath, out var _);
                                    }
                                });
                            }
                        };

                        foreach (var file in files)
                        {
                            watcher.Filters.Add(file);
                        }

                        watcher.EnableRaisingEvents = true;

                        _watchers.Add(dir, watcher);
                        ++created;
                    }

                    shell.WriteLine($"Created {created} shader directory watchers for {shaderCount} shaders.");

                    return;
                }

                if (args[0] == "-watch")
                {
                    if (_watchers == null)
                    {
                        shell.WriteLine("No shader directory watchers active.");
                        return;
                    }

                    var disposed = 0;
                    foreach (var (_, watcher) in _watchers)
                    {
                        ++disposed;
                        watcher.Dispose();
                    }

                    _watchers = null;

                    shell.WriteLine($"Disposed of {disposed} shader directory watchers.");

                    return;
                }
            }

            if (args.Length > 1)
            {
                shell.WriteLine("Not implemented.");
                return;
            }

            shell.WriteLine("Reloading content shader resources...");

            resC = IoCManager.Resolve<IResourceCacheInternal>();

            foreach (var (path, _) in resC.GetAllResources<ShaderSourceResource>())
            {
                try
                {
                    resC.ReloadResource<ShaderSourceResource>(path);
                }
                catch (Exception)
                {
                    shell.WriteLine($"Failed to reload shader: {path}");
                }
            }

            shell.WriteLine("Done.");
        }

    }

    internal sealed class ClydeDebugLayerCommand : IConsoleCommand
    {
        public string Command => "cldbglyr";
        public string Description => "Toggle fov and light debug layers";
        public string Help => "cldbglyr <layer>: Toggle <layer>\ncldbglyr: Turn all Layers off";

        public void Execute(IConsoleShell shell, string argStr, string[] args)
        {
            var clyde = IoCManager.Resolve<IClydeInternal>();

            if (args.Length < 1)
            {
                clyde.DebugLayers = ClydeDebugLayers.None;
                return;
            }

            clyde.DebugLayers = args[0] switch
            {
                "fov" => ClydeDebugLayers.Fov,
                "light" => ClydeDebugLayers.Light,
                _ => ClydeDebugLayers.None
            };
        }
    }

    internal sealed class GetKeyInfoCommand : IConsoleCommand
    {
        public string Command => "keyinfo";
        public string Description => "Keys key info for a key";
        public string Help => "keyinfo <Key>";

        public void Execute(IConsoleShell shell, string argStr, string[] args)
        {
            if (args.Length != 1)
            {
                shell.WriteLine(Help);
                return;
            }

            var clyde = IoCManager.Resolve<IClydeInternal>();

            if (Enum.TryParse(typeof(Keyboard.Key), args[0], true, out var parsed))
            {
                var key = (Keyboard.Key) parsed!;

                var name = clyde.GetKeyName(key);

                shell.WriteLine($"name: '{name}' ");
            }
        }
    }
}
