using Serilog;

namespace GameDemo
{
    public class Game : IGame
    {
        private readonly ILogger _logger;

        private readonly IGameServer _gameServer;

        private readonly IGameClient _gameClient;

        public Game(ILogger logger, IGameServer gameServer, IGameClient gameClient)
        {
            _logger = logger;
            _gameServer = gameServer;
            _gameClient = gameClient;
        }

        public void Run(string[] args)
        {
            Initialize();

            _gameClient.Run();

            Cleanup();
        }

        private void Cleanup()
        {
            _gameClient.Cleanup();
            _gameServer.Shutdown();
            _logger.Debug("Cleaning up...");
            _logger.Debug("Cleaning up...Done.");
        }

        private void Initialize()
        {
            _logger.Debug("Initializing...");

            InitializeServer();

            _gameClient.Initialize();

            _logger.Debug("Initializing...Done.");
        }

        private void InitializeServer()
        {
            _gameServer.Start();
        }

    }
}