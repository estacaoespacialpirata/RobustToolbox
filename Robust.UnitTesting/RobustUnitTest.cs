using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Robust.Client.GameObjects;
using Robust.Server.Containers;
using Robust.Server.Debugging;
using Robust.Server.GameObjects;
using Robust.Server.GameStates;
using Robust.Server.Physics;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.ContentPack;
using Robust.Shared.Debugging;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Dynamics;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Reflection;
using Robust.Shared.Utility;

namespace Robust.UnitTesting
{
    public enum UnitTestProject : byte
    {
        Server,
        Client
    }

    [Parallelizable]
    public abstract partial class RobustUnitTest
    {
        public virtual UnitTestProject Project => UnitTestProject.Server;

        [OneTimeSetUp]
        public void BaseSetup()
        {
            // Clear state across tests.
            IoCManager.InitThread();
            IoCManager.Clear();

            RegisterIoC();

            var assemblies = new List<Assembly>(4);
            switch (Project)
            {
                case UnitTestProject.Client:
                    assemblies.Add(AppDomain.CurrentDomain.GetAssemblyByName("Robust.Client"));
                    break;
                case UnitTestProject.Server:
                    assemblies.Add(AppDomain.CurrentDomain.GetAssemblyByName("Robust.Server"));
                    break;
                default:
                    throw new NotSupportedException($"Unknown testing project: {Project}");
            }

            assemblies.Add(AppDomain.CurrentDomain.GetAssemblyByName("Robust.Shared"));
            assemblies.Add(Assembly.GetExecutingAssembly());

            var configurationManager = IoCManager.Resolve<IConfigurationManagerInternal>();

            configurationManager.Initialize(Project == UnitTestProject.Server);

            foreach (var assembly in assemblies)
            {
                configurationManager.LoadCVarsFromAssembly(assembly);
            }

            var contentAssemblies = GetContentAssemblies();

            foreach (var assembly in contentAssemblies)
            {
                configurationManager.LoadCVarsFromAssembly(assembly);
            }

            configurationManager.LoadCVarsFromAssembly(typeof(RobustUnitTest).Assembly);

            var systems = IoCManager.Resolve<IEntitySystemManager>();
            // Required systems
            systems.LoadExtraSystemType<EntityLookupSystem>();

            // uhhh so maybe these are the wrong system for the client, but I CBF adding sprite system and all the rest,
            // and it was like this when I found it.
            systems.LoadExtraSystemType<Robust.Server.Containers.ContainerSystem>();
            systems.LoadExtraSystemType<Robust.Server.GameObjects.TransformSystem>();

            if (Project == UnitTestProject.Client)
            {
                systems.LoadExtraSystemType<ClientMetaDataSystem>();
            }
            else
            {
                systems.LoadExtraSystemType<ServerMetaDataSystem>();
                systems.LoadExtraSystemType<PVSSystem>();
            }

            var entMan = IoCManager.Resolve<IEntityManager>();
            var mapMan = IoCManager.Resolve<IMapManager>();

            // Required components for the engine to work
            var compFactory = IoCManager.Resolve<IComponentFactory>();

            if (!compFactory.AllRegisteredTypes.Contains(typeof(MetaDataComponent)))
            {
                compFactory.RegisterClass<MetaDataComponent>();
            }

            if (!compFactory.AllRegisteredTypes.Contains(typeof(EntityLookupComponent)))
            {
                compFactory.RegisterClass<EntityLookupComponent>();
            }

            if (!compFactory.AllRegisteredTypes.Contains(typeof(SharedPhysicsMapComponent)))
            {
                compFactory.RegisterClass<PhysicsMapComponent>();
            }

            if (!compFactory.AllRegisteredTypes.Contains(typeof(BroadphaseComponent)))
            {
                compFactory.RegisterClass<BroadphaseComponent>();
            }

            if (!compFactory.AllRegisteredTypes.Contains(typeof(FixturesComponent)))
            {
                compFactory.RegisterClass<FixturesComponent>();
            }

            if (!compFactory.AllRegisteredTypes.Contains(typeof(EntityLookupComponent)))
            {
                compFactory.RegisterClass<EntityLookupComponent>();
            }

            // So by default EntityManager does its own EntitySystemManager initialize during Startup.
            // We want to bypass this and load our own systems hence we will manually initialize it here.
            entMan.Initialize();
            // RobustUnitTest is complete hot garbage.
            // This makes EventTables ignore *all* the screwed up component abuse it causes.
            entMan.EventBus.OnlyCallOnRobustUnitTestISwearToGodPleaseSomebodyKillThisNightmare();
            mapMan.Initialize();
            systems.Initialize();

            IoCManager.Resolve<IReflectionManager>().LoadAssemblies(assemblies);

            var modLoader = IoCManager.Resolve<TestingModLoader>();
            modLoader.Assemblies = contentAssemblies;
            modLoader.TryLoadModulesFrom(ResourcePath.Root, "");

            entMan.Startup();
            mapMan.Startup();
        }

        [OneTimeTearDown]
        public void BaseTearDown()
        {
            IoCManager.Clear();
        }

        /// <summary>
        /// Called after all IoC registration has been done, but before the graph has been built.
        /// This allows one to add new IoC types or overwrite existing ones if needed.
        /// </summary>
        protected virtual void OverrideIoC()
        {
        }

        protected virtual Assembly[] GetContentAssemblies()
        {
            return Array.Empty<Assembly>();
        }
    }
}
