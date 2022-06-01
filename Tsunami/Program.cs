using Common;
using Common.Logger;
using Tsunami.Client;

namespace Tsunami
{
    internal class Program
    {
        static ILogger logger = new ConsoleLogger();
        static TsunamiClient tsunamiClient = new TsunamiClient(logger);
        static void Main(string[] args)
        {
            var rand = new Random();
            logger.LogInfo("TsunamiUDP Client");
            while (true)
            {
                var commands = Console.ReadLine()?.Split(' ');
                var action = commands?.ElementAtOrDefault(0);
                var argument = commands?.ElementAtOrDefault(1);

                switch (action)
                {
                    case "connect":
                        tsunamiClient = new TsunamiClient(logger);
                        tsunamiClient.Connect(Consts.HostAddress, Consts.TcpPort);
                        break;

                    case "get":
                        tsunamiClient.GetFile(argument, rand.Next(9000, 11000), 58000);
                        break;
                    case "disconnect":
                        tsunamiClient.Disconnect();
                        break;
                    case "help":
                        logger.LogInfo("Available commands: connect, disconnect, get 'filename'");
                        break;
                    default: 
                        logger.LogError("Not recognized command, check: help");
                        break;
                }
            }
        }
    }
}