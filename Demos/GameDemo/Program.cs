using System;
using DryIoc;
using Serilog;

namespace GameDemo
{
    public class Program
    {
        private readonly ILogger _logger;

        private readonly IGame _game;

        public static void Main(string[] args)
        {
            var compositionRoot = CreateCompositionRoot();

            var program = compositionRoot.Resolve<Program>();
            program.Run(args);
        }

        private static IContainer CreateCompositionRoot()
        {
            var container = new Container(rules => rules.WithTrackingDisposableTransients());
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Console()
                .CreateLogger();

            container.RegisterInstance(Log.Logger);
            container.Register<Program>(Reuse.Singleton);
            container.Register<IGameWindowFactory, GameWindowFactory>(Reuse.Singleton);
            container.Register<IGameServer, GameServer>(Reuse.Singleton);
            container.Register<IGameClient, GameClient>(Reuse.Singleton);
            container.Register<IGame, Game>(Reuse.Singleton);
            return container;
        }

        public Program(ILogger logger, IGame game)
        {
            _logger = logger;
            _game = game;

            _logger.Information("Initialize Program");
        }

        private void Run(string[] args)
        {
            _game.Run(args);
        }
    }
}
