using JetBrains.Annotations;
using Robust.Client.UserInterface;
using Robust.Shared.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.ViewVariables;
using Robust.Shared.ViewVariables.Commands;

namespace Robust.Client.ViewVariables
{
    [UsedImplicitly]
    public sealed class ViewVariablesCommand : ViewVariablesBaseCommand, IConsoleCommand
    {
        [Dependency] private readonly IClientViewVariablesManager _cvvm = default!;

        public override string Command => "vv";
        public override string Description => "Opens View Variables.";
        public override string Help => "Usage: vv <path|entity ID|guihover>";

        public override void Execute(IConsoleShell shell, string argStr, string[] args)
        {
            // If you don't provide an entity ID, it opens the test class.
            // Spooky huh.
            if (args.Length == 0)
            {
                _cvvm.OpenVV(new ViewVariablesPathSelector("/vvtest"));
                return;
            }

            if (args.Length > 1)
            {
                shell.WriteError($"Incorrect number of arguments. Did you forget to quote a path?");
                return;
            }

            var valArg = args[0];

            if (valArg.StartsWith("/c"))
            {
                // Remove "/c" before calling method.
                _cvvm.OpenVV(valArg[2..]);
                return;
            }

            if (valArg.StartsWith("/"))
            {
                var selector = new ViewVariablesPathSelector(valArg);
                _cvvm.OpenVV(selector);
                return;
            }

            if (valArg.StartsWith("guihover"))
            {
                // UI element.
                var obj = IoCManager.Resolve<IUserInterfaceManager>().CurrentlyHovered;
                if (obj == null)
                {
                    shell.WriteLine("Not currently hovering any control.");
                    return;
                }
                _cvvm.OpenVV(obj);
                return;
            }

            // Entity.
            if (!EntityUid.TryParse(args[0], out var entity))
            {
                shell.WriteLine("Invalid specifier format.");
                return;
            }

            var entityManager = IoCManager.Resolve<IEntityManager>();
            if (!entityManager.EntityExists(entity))
            {
                shell.WriteLine("That entity does not exist locally. Attempting to open remote view...");
                _cvvm.OpenVV(new ViewVariablesEntitySelector(entity));
                return;
            }

            _cvvm.OpenVV(entity);
        }
    }
}
