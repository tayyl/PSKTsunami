using System;

namespace Common.Logger
{
    public class ConsoleLogger : ILogger
    {
        private void Log(ConsoleColor color, string message)
        {
            Console.ForegroundColor = color;
            Console.WriteLine(message);
            Console.ForegroundColor = ConsoleColor.White;
        }
        public void LogError(string message)
        {
            Log(ConsoleColor.Red, $"[ERROR]: {message}");
        }

        public void LogInfo(string message)
        {
            Log(ConsoleColor.Gray, $"[INFO]: {message}");
        }

        public void LogSuccess(string message)
        {
            Log(ConsoleColor.DarkGreen, $"[SUCCESS]: {message}");
        }
    }
}