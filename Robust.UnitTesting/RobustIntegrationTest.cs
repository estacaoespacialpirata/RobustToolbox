using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using Robust.Client;
using Robust.Server;
using Robust.Server.Console;
using Robust.Server.ServerStatus;
using Robust.Shared;
using Robust.Shared.Analyzers;
using Robust.Shared.Asynchronous;
using Robust.Shared.Configuration;
using Robust.Shared.ContentPack;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Network;
using Robust.Shared.Serialization;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using ServerProgram = Robust.Server.Program;

namespace Robust.UnitTesting
{
    /// <summary>
    ///     Base class allowing you to implement integration tests.
    /// </summary>
    /// <remarks>
    ///     Integration tests allow you to act upon a running server as a whole,
    ///     contrary to unit testing which tests, well, units.
    /// </remarks>
    public abstract partial class RobustIntegrationTest
    {
        internal static readonly ConcurrentQueue<ClientIntegrationInstance> ClientsReady = new();
        internal static readonly ConcurrentQueue<ServerIntegrationInstance> ServersReady = new();

        internal static readonly ConcurrentQueue<string> ClientsCreated = new();
        internal static readonly ConcurrentQueue<string> ClientsPooled = new();
        internal static readonly ConcurrentQueue<string> ClientsNotPooled = new();

        internal static readonly ConcurrentQueue<string> ServersCreated = new();
        internal static readonly ConcurrentQueue<string> ServersPooled = new();
        internal static readonly ConcurrentQueue<string> ServersNotPooled = new();

        private readonly List<IntegrationInstance> _notPooledInstances = new();

        private readonly ConcurrentDictionary<ClientIntegrationInstance, byte> _clientsRunning = new();
        private readonly ConcurrentDictionary<ServerIntegrationInstance, byte> _serversRunning = new();

        private string TestId => TestContext.CurrentContext.Test.FullName;

        private string GetTestsRanString(IntegrationInstance instance, string running)
        {
            var type = instance is ServerIntegrationInstance ? "Server " : "Client ";

            return $"{type} tests ran ({instance.TestsRan.Count}):\n" +
                   $"{string.Join('\n', instance.TestsRan)}\n" +
                   $"Currently running: {running}";
        }

        /// <summary>
        ///     Start an instance of the server and return an object that can be used to control it.
        /// </summary>
        protected virtual ServerIntegrationInstance StartServer(ServerIntegrationOptions? options = null)
        {
            ServerIntegrationInstance instance;

            if (ShouldPool(options))
            {
                if (ServersReady.TryDequeue(out var server))
                {
                    server.PreviousOptions = server.ServerOptions;
                    server.ServerOptions = options;

                    OnServerReturn(server).Wait();

                    _serversRunning[server] = 0;
                    instance = server;
                }
                else
                {
                    instance = new ServerIntegrationInstance(options);
                    _serversRunning[instance] = 0;

                    ServersCreated.Enqueue(TestId);
                }

                ServersPooled.Enqueue(TestId);
            }
            else
            {
                instance = new ServerIntegrationInstance(options);
                _notPooledInstances.Add(instance);

                ServersCreated.Enqueue(TestId);
                ServersNotPooled.Enqueue(TestId);
            }

            var currentTest = TestContext.CurrentContext.Test.FullName;
            TestContext.Out.WriteLine(GetTestsRanString(instance, currentTest));
            instance.TestsRan.Add(currentTest);

            return instance;
        }

        /// <summary>
        ///     Start a headless instance of the client and return an object that can be used to control it.
        /// </summary>
        protected virtual ClientIntegrationInstance StartClient(ClientIntegrationOptions? options = null)
        {
            ClientIntegrationInstance instance;

            if (ShouldPool(options))
            {
                if (ClientsReady.TryDequeue(out var client))
                {
                    client.PreviousOptions = client.ClientOptions;
                    client.ClientOptions = options;

                    OnClientReturn(client).Wait();

                    _clientsRunning[client] = 0;
                    instance = client;
                }
                else
                {
                    instance = new ClientIntegrationInstance(options);
                    _clientsRunning[instance] = 0;

                    ClientsCreated.Enqueue(TestId);
                }

                ClientsPooled.Enqueue(TestId);
            }
            else
            {
                instance = new ClientIntegrationInstance(options);
                _notPooledInstances.Add(instance);

                ClientsCreated.Enqueue(TestId);
                ClientsNotPooled.Enqueue(TestId);
            }

            var currentTest = TestContext.CurrentContext.Test.FullName;
            TestContext.Out.WriteLine(GetTestsRanString(instance, currentTest));
            instance.TestsRan.Add(currentTest);

            return instance;
        }

        private bool ShouldPool(IntegrationOptions? options)
        {
            return options?.Pool ?? false;
        }

        protected virtual async Task OnInstanceReturn(IntegrationInstance instance)
        {
            await instance.WaitPost(() =>
            {
                var config = IoCManager.Resolve<IConfigurationManagerInternal>();
                var overrides = new[]
                {
                    (RTCVars.FailureLogLevel.Name, (instance.Options?.FailureLogLevel ?? RTCVars.FailureLogLevel.DefaultValue).ToString())
                };

                config.OverrideConVars(overrides);
            });
        }

        protected virtual Task OnClientReturn(ClientIntegrationInstance client)
        {
            return OnInstanceReturn(client);
        }

        protected virtual Task OnServerReturn(ServerIntegrationInstance server)
        {
            return OnInstanceReturn(server);
        }

        [OneTimeTearDown]
        public async Task OneTimeTearDown()
        {
            foreach (var client in _clientsRunning.Keys)
            {
                await client.WaitIdleAsync();

                if (client.UnhandledException != null || !client.IsAlive)
                {
                    continue;
                }

                ClientsReady.Enqueue(client);
            }

            _clientsRunning.Clear();

            foreach (var server in _serversRunning.Keys)
            {
                await server.WaitIdleAsync();

                if (server.UnhandledException != null || !server.IsAlive)
                {
                    continue;
                }

                ServersReady.Enqueue(server);
            }

            _serversRunning.Clear();

            _notPooledInstances.ForEach(p => p.Stop());
            await Task.WhenAll(_notPooledInstances.Select(p => p.WaitIdleAsync()));
            _notPooledInstances.Clear();
        }

        /// <summary>
        ///     Provides control over a running instance of the client or server.
        /// </summary>
        /// <remarks>
        ///     The instance executes in another thread.
        ///     As such, sending commands to it purely queues them to be ran asynchronously.
        ///     To ensure that the instance is idle, i.e. not executing code and finished all queued commands,
        ///     you can use <see cref="WaitIdleAsync"/>.
        ///     This method must be used before trying to access any state like <see cref="ResolveDependency{T}"/>,
        ///     to prevent race conditions.
        /// </remarks>
        public abstract class IntegrationInstance : IDisposable
        {
            private protected Thread? InstanceThread;
            private protected IDependencyCollection DependencyCollection = default!;
            private protected IntegrationGameLoop GameLoop = default!;

            private protected readonly ChannelReader<object> _toInstanceReader;
            private protected readonly ChannelWriter<object> _toInstanceWriter;
            private protected readonly ChannelReader<object> _fromInstanceReader;
            private protected readonly ChannelWriter<object> _fromInstanceWriter;

            private int _currentTicksId = 1;
            private int _ackTicksId;

            private bool _isSurelyIdle;
            private bool _isAlive = true;
            private Exception? _unhandledException;

            public IDependencyCollection InstanceDependencyCollection => DependencyCollection;

            public virtual IntegrationOptions? Options { get; internal set; }

            /// <summary>
            ///     Whether the instance is still alive.
            ///     "Alive" indicates that it is able to receive and process commands.
            /// </summary>
            /// <exception cref="InvalidOperationException">
            ///     Thrown if you did not ensure that the instance is idle via <see cref="WaitIdleAsync"/> first.
            /// </exception>
            public bool IsAlive
            {
                get
                {
                    if (!_isSurelyIdle)
                    {
                        throw new InvalidOperationException(
                            "Cannot read this without ensuring that the instance is idle.");
                    }

                    return _isAlive;
                }
            }

            /// <summary>
            ///     If the server
            /// </summary>
            /// <exception cref="InvalidOperationException">
            ///     Thrown if you did not ensure that the instance is idle via <see cref="WaitIdleAsync"/> first.
            /// </exception>
            public Exception? UnhandledException
            {
                get
                {
                    if (!_isSurelyIdle)
                    {
                        throw new InvalidOperationException(
                            "Cannot read this without ensuring that the instance is idle.");
                    }

                    return _unhandledException;
                }
            }

            public List<string> TestsRan { get; } = new();

            private protected IntegrationInstance(IntegrationOptions? options)
            {
                Options = options;

                var toInstance = Channel.CreateUnbounded<object>(new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = true
                });

                _toInstanceReader = toInstance.Reader;
                _toInstanceWriter = toInstance.Writer;

                var fromInstance = Channel.CreateUnbounded<object>(new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = true
                });

                _fromInstanceReader = fromInstance.Reader;
                _fromInstanceWriter = fromInstance.Writer;
            }

            /// <summary>
            ///     Resolve a dependency inside the instance.
            ///     This works identical to <see cref="IoCManager.Resolve{T}"/>.
            /// </summary>
            /// <exception cref="InvalidOperationException">
            ///     Thrown if you did not ensure that the instance is idle via <see cref="WaitIdleAsync"/> first.
            /// </exception>
            [Pure]
            public T ResolveDependency<T>()
            {
                if (!_isSurelyIdle)
                {
                    throw new InvalidOperationException(
                        "Cannot resolve services without ensuring that the instance is idle.");
                }

                return DependencyCollection.Resolve<T>();
            }

            /// <summary>
            ///     Wait for the instance to go idle, either through finishing all commands or shutting down/crashing.
            /// </summary>
            /// <param name="throwOnUnhandled">
            ///     If true, throw an exception if the server dies on an unhandled exception.
            /// </param>
            /// <param name="cancellationToken"></param>
            /// <exception cref="Exception">
            ///     Thrown if <paramref name="throwOnUnhandled"/> is true and the instance shuts down on an unhandled exception.
            /// </exception>
            public Task WaitIdleAsync(bool throwOnUnhandled = true, CancellationToken cancellationToken = default)
            {
                if (Options?.Asynchronous == true)
                {
                    return WaitIdleImplAsync(throwOnUnhandled, cancellationToken);
                }

                WaitIdleImplSync(throwOnUnhandled);
                return Task.CompletedTask;
            }

            private async Task WaitIdleImplAsync(bool throwOnUnhandled, CancellationToken cancellationToken)
            {
                while (_isAlive && _currentTicksId != _ackTicksId)
                {
                    object msg = default!;
                    try
                    {
                        msg = await _fromInstanceReader.ReadAsync(cancellationToken);
                    }
                    catch(OperationCanceledException ex)
                    {
                        _unhandledException = ex;
                        _isAlive = false;
                        break;
                    }
                    switch (msg)
                    {
                        case ShutDownMessage shutDownMessage:
                        {
                            _isAlive = false;
                            _isSurelyIdle = true;
                            _unhandledException = shutDownMessage.UnhandledException;
                            if (throwOnUnhandled && _unhandledException != null)
                            {
                                ExceptionDispatchInfo.Capture(_unhandledException).Throw();
                                return;
                            }

                            break;
                        }

                        case AckTicksMessage ack:
                        {
                            _ackTicksId = ack.MessageId;
                            break;
                        }

                        case AssertFailMessage assertFailMessage:
                        {
                            // Rethrow exception without losing stack trace.
                            ExceptionDispatchInfo.Capture(assertFailMessage.Exception).Throw();
                            break; // Unreachable.
                        }
                    }
                }

                _isSurelyIdle = true;
            }

            private void WaitIdleImplSync(bool throwOnUnhandled)
            {
                var oldSyncContext = SynchronizationContext.Current;
                try
                {
                    // Set up thread-local resources for instance switch.
                    {
                        var taskMgr = DependencyCollection.Resolve<TaskManager>();
                        IoCManager.InitThread(DependencyCollection, replaceExisting: true);
                        taskMgr.ResetSynchronizationContext();
                    }

                    GameLoop.SingleThreadRunUntilEmpty();
                    _isSurelyIdle = true;

                    while (_fromInstanceReader.TryRead(out var msg))
                    {
                        switch (msg)
                        {
                            case ShutDownMessage shutDownMessage:
                            {
                                _isAlive = false;
                                _unhandledException = shutDownMessage.UnhandledException;
                                if (throwOnUnhandled && _unhandledException != null)
                                {
                                    ExceptionDispatchInfo.Capture(_unhandledException).Throw();
                                    return;
                                }

                                break;
                            }

                            case AssertFailMessage assertFailMessage:
                            {
                                // Rethrow exception without losing stack trace.
                                ExceptionDispatchInfo.Capture(assertFailMessage.Exception).Throw();
                                break; // Unreachable.
                            }
                        }
                    }

                    _isSurelyIdle = true;
                }
                finally
                {
                    // NUnit has its own synchronization context so let's *not* break everything.
                    SynchronizationContext.SetSynchronizationContext(oldSyncContext);
                }
            }

            /// <summary>
            ///     Queue for the server to run n ticks.
            /// </summary>
            /// <param name="ticks">The amount of ticks to run.</param>
            public void RunTicks(int ticks)
            {
                _isSurelyIdle = false;
                _currentTicksId += 1;
                _toInstanceWriter.TryWrite(new RunTicksMessage(ticks, _currentTicksId));
            }

            /// <summary>
            ///     <see cref="RunTicks"/> followed by <see cref="WaitIdleAsync"/>
            /// </summary>
            public async Task WaitRunTicks(int ticks)
            {
                RunTicks(ticks);
                await WaitIdleAsync();
            }

            /// <summary>
            ///     Queue for the server to be stopped.
            /// </summary>
            public void Stop()
            {
                _isSurelyIdle = false;
                // Won't get ack'd directly but the shutdown is convincing enough.
                _currentTicksId += 1;
                _toInstanceWriter.TryWrite(new StopMessage());
                _toInstanceWriter.TryComplete();
            }

            /// <summary>
            ///     Queue for a delegate to be ran inside the main loop of the instance.
            /// </summary>
            /// <remarks>
            ///     Do not run NUnit assertions inside <see cref="Post"/>. Use <see cref="Assert"/> instead.
            /// </remarks>
            public void Post(Action post)
            {
                _isSurelyIdle = false;
                _currentTicksId += 1;
                _toInstanceWriter.TryWrite(new PostMessage(post, _currentTicksId));
            }

            public async Task WaitPost(Action post)
            {
                Post(post);
                await WaitIdleAsync();
            }

            /// <summary>
            ///     Queue for a delegate to be ran inside the main loop of the instance,
            ///     rethrowing any exceptions in <see cref="WaitIdleAsync"/>.
            /// </summary>
            /// <remarks>
            ///     Exceptions raised inside this callback will be rethrown by <see cref="WaitIdleAsync"/>.
            ///     This makes it ideal for NUnit assertions,
            ///     since rethrowing the NUnit assertion directly provides less noise.
            /// </remarks>
            public void Assert(Action assertion)
            {
                _isSurelyIdle = false;
                _currentTicksId += 1;
                _toInstanceWriter.TryWrite(new AssertMessage(assertion, _currentTicksId));
            }

            public async Task WaitAssertion(Action assertion)
            {
                Assert(assertion);
                await WaitIdleAsync();
            }

            public void Dispose()
            {
                Stop();
            }
        }

        public sealed class ServerIntegrationInstance : IntegrationInstance
        {
            public ServerIntegrationInstance(ServerIntegrationOptions? options) : base(options)
            {
                ServerOptions = options;
                DependencyCollection = new DependencyCollection();
                if (options?.Asynchronous == true)
                {
                    InstanceThread = new Thread(_serverMain)
                    {
                        Name = "Server Instance Thread",
                        IsBackground = true
                    };
                    InstanceThread.Start();
                }
                else
                {
                    Init();
                }
            }

            public override IntegrationOptions? Options
            {
                get => ServerOptions;
                internal set => ServerOptions = (ServerIntegrationOptions?) value;
            }

            public ServerIntegrationOptions? ServerOptions { get; internal set; }

            public ServerIntegrationOptions? PreviousOptions { get; internal set; }

            private void _serverMain()
            {
                try
                {
                    var server = Init();
                    GameLoop.Run();
                    server.FinishMainLoop();
                    IoCManager.Clear();
                }
                catch (Exception e)
                {
                    _fromInstanceWriter.TryWrite(new ShutDownMessage(e));
                    return;
                }

                _fromInstanceWriter.TryWrite(new ShutDownMessage(null));
            }

            private BaseServer Init()
            {
                IoCManager.InitThread(DependencyCollection, replaceExisting: true);
                ServerIoC.RegisterIoC();
                IoCManager.Register<INetManager, IntegrationNetManager>(true);
                IoCManager.Register<IServerNetManager, IntegrationNetManager>(true);
                IoCManager.Register<IntegrationNetManager, IntegrationNetManager>(true);
                IoCManager.Register<ISystemConsoleManager, SystemConsoleManagerDummy>(true);
                IoCManager.Register<IModLoader, TestingModLoader>(true);
                IoCManager.Register<IModLoaderInternal, TestingModLoader>(true);
                IoCManager.Register<TestingModLoader, TestingModLoader>(true);
                IoCManager.RegisterInstance<IStatusHost>(new Mock<IStatusHost>().Object, true);
                IoCManager.Register<IRobustMappedStringSerializer, IntegrationMappedStringSerializer>(true);
                Options?.InitIoC?.Invoke();
                IoCManager.BuildGraph();
                //ServerProgram.SetupLogging();
                ServerProgram.InitReflectionManager();

                var server = DependencyCollection.Resolve<BaseServer>();

                var serverOptions = ServerOptions?.Options ?? new ServerOptions()
                {
                    LoadConfigAndUserData = false,
                    LoadContentResources = false,
                };

                // Autoregister components if options are null or we're NOT starting from content, as in that case
                // components will get auto-registered later. But either way, we will still invoke
                // BeforeRegisterComponents here.
                Options?.BeforeRegisterComponents?.Invoke();
                if (!Options?.ContentStart ?? true)
                {
                    var componentFactory = IoCManager.Resolve<IComponentFactory>();
                    componentFactory.DoAutoRegistrations();
                    componentFactory.GenerateNetIds();
                }

                if (Options?.ContentAssemblies != null)
                {
                    IoCManager.Resolve<TestingModLoader>().Assemblies = Options.ContentAssemblies;
                }

                var cfg = IoCManager.Resolve<IConfigurationManagerInternal>();

                cfg.LoadCVarsFromAssembly(typeof(RobustIntegrationTest).Assembly);

                if (Options != null)
                {
                    Options.BeforeStart?.Invoke();
                    cfg.OverrideConVars(Options.CVarOverrides.Select(p => (p.Key, p.Value)));

                    if (Options.ExtraPrototypes != null)
                    {
                        IoCManager.Resolve<IResourceManagerInternal>()
                            .MountString("/Prototypes/__integration_extra.yml", Options.ExtraPrototypes);
                    }
                }

                cfg.OverrideConVars(new[]
                {
                    ("log.runtimelog", "false"),
                    (CVars.SysWinTickPeriod.Name, "-1"),
                    (RTCVars.FailureLogLevel.Name, (Options?.FailureLogLevel ?? RTCVars.FailureLogLevel.DefaultValue).ToString())
                });

                server.ContentStart = Options?.ContentStart ?? false;
                if (server.Start(serverOptions, () => new TestLogHandler(cfg, "SERVER")))
                {
                    throw new Exception("Server failed to start.");
                }

                GameLoop = new IntegrationGameLoop(
                    DependencyCollection.Resolve<IGameTiming>(),
                    _fromInstanceWriter, _toInstanceReader);
                server.OverrideMainLoop(GameLoop);
                server.SetupMainLoop();

                GameLoop.RunInit();

                return server;
            }
        }

        public sealed class ClientIntegrationInstance : IntegrationInstance
        {
            public ClientIntegrationInstance(ClientIntegrationOptions? options) : base(options)
            {
                ClientOptions = options;
                DependencyCollection = new DependencyCollection();

                if (options?.Asynchronous == true)
                {
                    InstanceThread = new Thread(ThreadMain)
                    {
                        Name = "Client Instance Thread",
                        IsBackground = true
                    };
                    InstanceThread.Start();
                }
                else
                {
                    Init();
                }
            }

            public override IntegrationOptions? Options
            {
                get => ClientOptions;
                internal set => ClientOptions = (ClientIntegrationOptions?) value;
            }

            public ClientIntegrationOptions? ClientOptions { get; internal set; }

            public ClientIntegrationOptions? PreviousOptions { get; internal set; }

            /// <summary>
            ///     Wire up the server to connect to when <see cref="IClientNetManager.ClientConnect"/> gets called.
            /// </summary>
            public void SetConnectTarget(ServerIntegrationInstance server)
            {
                var clientNetManager = ResolveDependency<IntegrationNetManager>();
                var serverNetManager = server.ResolveDependency<IntegrationNetManager>();

                if (!serverNetManager.IsRunning)
                {
                    throw new InvalidOperationException("Server net manager is not running!");
                }

                clientNetManager.NextConnectChannel = serverNetManager.MessageChannelWriter;
            }

            public async Task CheckSandboxed(Assembly assembly)
            {
                await WaitIdleAsync();
                await WaitAssertion(() =>
                {
                    var modLoader = new ModLoader();
                    IoCManager.InjectDependencies(modLoader);
                    modLoader.SetEnableSandboxing(true);
                    modLoader.LoadGameAssembly(assembly.Location);
                });
            }

            private void ThreadMain()
            {
                try
                {
                    var client = Init();
                    GameLoop.Run();
                    client.CleanupGameThread();
                    client.CleanupWindowThread();
                }
                catch (Exception e)
                {
                    _fromInstanceWriter.TryWrite(new ShutDownMessage(e));
                    return;
                }

                _fromInstanceWriter.TryWrite(new ShutDownMessage(null));
            }

            private GameController Init()
            {
                IoCManager.InitThread(DependencyCollection, replaceExisting: true);
                ClientIoC.RegisterIoC(GameController.DisplayMode.Headless);
                IoCManager.Register<INetManager, IntegrationNetManager>(true);
                IoCManager.Register<IClientNetManager, IntegrationNetManager>(true);
                IoCManager.Register<IntegrationNetManager, IntegrationNetManager>(true);
                IoCManager.Register<IModLoader, TestingModLoader>(true);
                IoCManager.Register<IModLoaderInternal, TestingModLoader>(true);
                IoCManager.Register<TestingModLoader, TestingModLoader>(true);
                IoCManager.Register<IRobustMappedStringSerializer, IntegrationMappedStringSerializer>(true);
                Options?.InitIoC?.Invoke();
                IoCManager.BuildGraph();

                GameController.RegisterReflection();

                var client = DependencyCollection.Resolve<GameController>();

                var clientOptions = ClientOptions?.Options ?? new GameControllerOptions()
                {
                    LoadContentResources = false,
                    LoadConfigAndUserData = false,
                };

                // Autoregister components if options are null or we're NOT starting from content, as in that case
                // components will get auto-registered later. But either way, we will still invoke
                // BeforeRegisterComponents here.
                Options?.BeforeRegisterComponents?.Invoke();
                if (!Options?.ContentStart ?? true)
                {
                    var componentFactory = IoCManager.Resolve<IComponentFactory>();
                    componentFactory.DoAutoRegistrations();
                    componentFactory.GenerateNetIds();
                }

                if (Options?.ContentAssemblies != null)
                {
                    IoCManager.Resolve<TestingModLoader>().Assemblies = Options.ContentAssemblies;
                }

                var cfg = IoCManager.Resolve<IConfigurationManagerInternal>();

                cfg.LoadCVarsFromAssembly(typeof(RobustIntegrationTest).Assembly);

                if (Options != null)
                {
                    Options.BeforeStart?.Invoke();
                    cfg.OverrideConVars(Options.CVarOverrides.Select(p => (p.Key, p.Value)));

                    if (Options.ExtraPrototypes != null)
                    {
                        IoCManager.Resolve<IResourceManagerInternal>()
                            .MountString("/Prototypes/__integration_extra.yml", Options.ExtraPrototypes);
                    }
                }

                cfg.OverrideConVars(new[]
                {
                    (CVars.NetPredictLagBias.Name, "0"),

                    // Connecting to Discord is a massive waste of time.
                    // Basically just makes the CI logs a mess.
                    (CVars.DiscordEnabled.Name, "false"),

                    // Avoid preloading textures.
                    (CVars.ResTexturePreloadingEnabled.Name, "false"),

                    (RTCVars.FailureLogLevel.Name, (Options?.FailureLogLevel ?? RTCVars.FailureLogLevel.DefaultValue).ToString())
                });

                GameLoop = new IntegrationGameLoop(DependencyCollection.Resolve<IGameTiming>(),
                    _fromInstanceWriter, _toInstanceReader);

                client.OverrideMainLoop(GameLoop);
                client.ContentStart = Options?.ContentStart ?? false;
                client.StartupSystemSplash(clientOptions, () => new TestLogHandler(cfg, "CLIENT"));
                client.StartupContinue(GameController.DisplayMode.Headless);

                GameLoop.RunInit();

                return client;
            }
        }

        // Synchronization between the integration instance and the main loop is done purely through message passing.
        // The main thread sends commands like "run n ticks" and the main loop reports back the commands it has finished.
        // It also reports when it dies, of course.

        internal sealed class IntegrationGameLoop : IGameLoop
        {
            private readonly IGameTiming _gameTiming;

            private readonly ChannelWriter<object> _channelWriter;
            private readonly ChannelReader<object> _channelReader;

#pragma warning disable 67
            public event EventHandler<FrameEventArgs>? Input;
            public event EventHandler<FrameEventArgs>? Tick;
            public event EventHandler<FrameEventArgs>? Update;
            public event EventHandler<FrameEventArgs>? Render;
#pragma warning restore 67

            public bool SingleStep { get; set; }
            public bool Running { get; set; }
            public int MaxQueuedTicks { get; set; }
            public SleepMode SleepMode { get; set; }

            public IntegrationGameLoop(IGameTiming gameTiming, ChannelWriter<object> channelWriter,
                ChannelReader<object> channelReader)
            {
                _gameTiming = gameTiming;
                _channelWriter = channelWriter;
                _channelReader = channelReader;
            }

            public void Run()
            {
                // Main run method is only used when running from asynchronous thread.

                // Ack tick message 1 is implied as "init done"
                _channelWriter.TryWrite(new AckTicksMessage(1));

                while (Running)
                {
                    var readerNotDone = _channelReader.WaitToReadAsync().AsTask().GetAwaiter().GetResult();
                    if (!readerNotDone)
                    {
                        Running = false;
                        return;
                    }
                    SingleThreadRunUntilEmpty();
                }
            }

            public void RunInit()
            {
                Running = true;

                _gameTiming.InSimulation = true;
            }

            public void SingleThreadRunUntilEmpty()
            {
                while (Running && _channelReader.TryRead(out var message))
                {
                    switch (message)
                    {
                        case RunTicksMessage msg:
                            _gameTiming.InSimulation = true;
                            var simFrameEvent = new FrameEventArgs((float) _gameTiming.TickPeriod.TotalSeconds);
                            for (var i = 0; i < msg.Ticks && Running; i++)
                            {
                                Input?.Invoke(this, simFrameEvent);
                                Tick?.Invoke(this, simFrameEvent);
                                _gameTiming.CurTick = new GameTick(_gameTiming.CurTick.Value + 1);
                                Update?.Invoke(this, simFrameEvent);
                            }

                            _channelWriter.TryWrite(new AckTicksMessage(msg.MessageId));
                            break;

                        case StopMessage _:
                            Running = false;
                            break;

                        case PostMessage postMessage:
                            postMessage.Post();
                            _channelWriter.TryWrite(new AckTicksMessage(postMessage.MessageId));
                            break;

                        case AssertMessage assertMessage:
                            try
                            {
                                assertMessage.Assertion();
                            }
                            catch (Exception e)
                            {
                                _channelWriter.TryWrite(new AssertFailMessage(e));
                            }

                            _channelWriter.TryWrite(new AckTicksMessage(assertMessage.MessageId));
                            break;
                    }
                }
            }
        }

        [Virtual]
        public class ServerIntegrationOptions : IntegrationOptions
        {
            public virtual ServerOptions Options { get; set; } = new()
            {
                LoadConfigAndUserData = false,
                LoadContentResources = false,
            };
        }

        [Virtual]
        public class ClientIntegrationOptions : IntegrationOptions
        {
            public virtual GameControllerOptions Options { get; set; } = new()
            {
                LoadContentResources = false,
                LoadConfigAndUserData = false,
            };
        }

        public abstract class IntegrationOptions
        {
            public Action? InitIoC { get; set; }
            public Action? BeforeRegisterComponents { get; set; }
            public Action? BeforeStart { get; set; }
            public Assembly[]? ContentAssemblies { get; set; }
            public string? ExtraPrototypes { get; set; }
            public LogLevel? FailureLogLevel { get; set; } = RTCVars.FailureLogLevel.DefaultValue;
            public bool ContentStart { get; set; } = false;

            public Dictionary<string, string> CVarOverrides { get; } = new();
            public bool Asynchronous { get; set; } = true;
            public bool? Pool { get; set; }
        }

        /// <summary>
        ///     Sent head -> instance to tell the instance to run a few simulation ticks.
        /// </summary>
        private sealed class RunTicksMessage
        {
            public RunTicksMessage(int ticks, int messageId)
            {
                Ticks = ticks;
                MessageId = messageId;
            }

            public int Ticks { get; }
            public int MessageId { get; }
        }

        /// <summary>
        ///     Sent head -> instance to tell the instance to shut down cleanly.
        /// </summary>
        private sealed class StopMessage
        {
        }

        /// <summary>
        ///     Sent instance -> head to confirm finishing of ticks message.
        /// </summary>
        private sealed class AckTicksMessage
        {
            public AckTicksMessage(int messageId)
            {
                MessageId = messageId;
            }

            public int MessageId { get; }
        }

        private sealed class AssertFailMessage
        {
            public Exception Exception { get; }

            public AssertFailMessage(Exception exception)
            {
                Exception = exception;
            }
        }

        /// <summary>
        ///     Sent instance -> head when instance shuts down for whatever reason.
        /// </summary>
        private sealed class ShutDownMessage
        {
            public ShutDownMessage(Exception? unhandledException)
            {
                UnhandledException = unhandledException;
            }

            public Exception? UnhandledException { get; }
        }

        private sealed class PostMessage
        {
            public Action Post { get; }
            public int MessageId { get; }

            public PostMessage(Action post, int messageId)
            {
                Post = post;
                MessageId = messageId;
            }
        }

        private sealed class AssertMessage
        {
            public Action Assertion { get; }
            public int MessageId { get; }

            public AssertMessage(Action assertion, int messageId)
            {
                Assertion = assertion;
                MessageId = messageId;
            }
        }
    }
}
