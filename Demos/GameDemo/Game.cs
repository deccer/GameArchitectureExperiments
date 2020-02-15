using Serilog;

namespace GameDemo
{
    public class Game : IGame
    {
        private readonly ILogger _logger;

        public Game(ILogger logger)
        {
            _logger = logger;
        }

        public void Run(string[] args)
        {
            Initialize();



            Cleanup();
        }

        private void Cleanup()
        {
            _logger.Debug("Cleaning up...");
            _logger.Debug("Cleaning up...Done.");
        }

        private void Initialize()
        {
            _logger.Debug("Initializing...");



            _logger.Debug("Initializing...Done.");
        }


    }
}