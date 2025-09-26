using UnityEngine;

namespace TerrainSystem
{
    public interface ITerrainLogger
    {
        void Log(LogType type, object message, Object context = null);
    }

    public sealed class UnityTerrainLogger : ITerrainLogger
    {
        private readonly ILogger logger;

        public UnityTerrainLogger() : this(Debug.unityLogger) { }

        public UnityTerrainLogger(ILogger logger)
        {
            this.logger = logger ?? Debug.unityLogger;
        }

        public void Log(LogType type, object message, Object context = null)
        {
            logger.Log(type, message, context);
        }
    }
}