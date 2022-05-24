using Common.Logger;

namespace Server
{
    internal class Program
    {
        static ILogger logger = new ConsoleLogger();
        static TsunamiServer tsunamiServer = new TsunamiServer(logger);
        static void Main(string[] args)
        {
            Console.WriteLine("TsunamiUDP Server");
            tsunamiServer.Start();
            while (true) { }
        }
    }
}