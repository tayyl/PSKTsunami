using Common;
using Common.Logger;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Server
{
    public class TsunamiServer
    {
        ILogger logger;
        private TcpClient tcpClient;
        private UdpClient udpClient;
        private ConcurrentQueue<int> RetransmissionQueue;
        private ConcurrentQueue<(int chunkIndex, byte[] data)> ChunksQueue;
        private bool sendingCompleted;

        public TsunamiServer(ILogger logger)
        {
            this.logger = logger;
            if (!Directory.Exists(Consts.ServerFilesPath))
            {
                Directory.CreateDirectory(Consts.ServerFilesPath);
            }
        }

        public void Stop()
        {
            try
            {
                udpClient?.Close();
                tcpClient?.Close();
                udpClient?.Dispose();
                tcpClient?.Dispose();
            }
            catch (Exception e)
            {
                logger.LogError($"Failed to stop server. Exception: {e.Message}");
            }
        }
        public void Start()
        {
            var listener = new Thread(() => Listener());
            listener.Start();
            logger.LogInfo("Server started");
        }

        public void Listener()
        {
            var tcpListener = new TcpListener(IPAddress.Loopback, Consts.TcpPort);
            tcpListener.Start();
            while (true)
            {
                var connectedTcpClient = tcpListener.AcceptTcpClient();
                tcpClient = connectedTcpClient;
                logger.LogSuccess($"TCP Client: {tcpClient.Client.RemoteEndPoint} connected!");
                var receiver = new Thread(Receive);
                receiver.Start();
            }
        }
        private void WriteLine(string request) => tcpClient.GetStream().Write(Encoding.UTF8.GetBytes($"{request}\n"));
        private string ReadLine()
        {
            try
            {
                var stream = tcpClient.GetStream();
                var responseBytes = new byte[1024];
                var bytes = 0;
                var response = "";
                do
                {
                    bytes = stream.Read(responseBytes, 0, responseBytes.Length);
                    response += Encoding.ASCII.GetString(responseBytes, 0, bytes);
                }
                while (stream.DataAvailable);
                return response;
            }
            catch
            {
                return "done";
            }
        }

        void Receive()
        {
            var response = "";
            while ((response = ReadLine()) != "done\n")
            {
                var commands = response.Split(' ');
                var action = commands.ElementAtOrDefault(0);
                var argument = string.Join(" ", commands.Skip(1)); ;
                switch (action)
                {
                    case "get":
                        var sender = new Thread(() => SendFile(argument));
                        sender.Start();
                        break;
                    case "retransmit":
                        AddChunkToRetransmissionQueue(argument);
                        break;
                    default: WriteLine("Invalid command"); break;
                }
            }
            sendingCompleted = true;
            logger.LogInfo("Finished receiving");
        }
        void SendFile(string command)
        {
            var commands = command.Split(' ');
            var filename = commands.ElementAtOrDefault(0);
            var clientPort = commands.ElementAtOrDefault(1);
            var chunkSize = commands.ElementAtOrDefault(2);
            var filePath = Path.Combine(Consts.ServerFilesPath, filename);
            if (string.IsNullOrEmpty(filename) || !File.Exists(filePath))
            {
                WriteLine($"No file with name {filename} found on server.");
                return;
            }

            if (!int.TryParse(chunkSize, out var chunkSizeInt))
            {
                WriteLine($"Could not parse {chunkSize} to integer");
                return;
            }

            if (!int.TryParse(clientPort, out var clientPortInt))
            {
                WriteLine($"Could not parse {clientPort} to integer");
                return;
            }
            ChunksQueue = new ConcurrentQueue<(int chunkIndex, byte[] data)>();
            try
            {
                logger.LogSuccess($"Received request for file {filename} from {tcpClient.Client.RemoteEndPoint}");

                var fileInfo = new FileInfo(filePath);
                var chunkCount = Math.Ceiling(fileInfo.Length / (decimal)chunkSizeInt);

                 WriteLine(fileInfo.Length.ToString());

                udpClient = new UdpClient();
                var clientEndpoint = tcpClient.Client.RemoteEndPoint as IPEndPoint;
                udpClient.Connect(clientEndpoint.Address, clientPortInt);

                logger.LogInfo($"Reading file: {filename} with size of {fileInfo.Length} bytes");
                RetransmissionQueue = new ConcurrentQueue<int>(Enumerable.Range(0, (int)chunkCount));
                var reader = new Thread(() => ReadFileChunks(filename, chunkSizeInt));
                var sender = new Thread(() => SendFileChunks((int)chunkCount, chunkSizeInt));
                reader.Start();
                sender.Start();

                //while (!sendingCompleted) { } //??

                sender.Join();
                logger.LogSuccess($"File {filename} send to client {tcpClient.Client.RemoteEndPoint}");
            }
            catch (Exception e)
            {
                logger.LogError($"Failed to send file {filename}. Exception: {e.Message}");
            }


        }
        void ReadFileChunks(string filename, int chunkSize)
        {
            var path = Path.Combine(Consts.ServerFilesPath, filename);

            using (var fileStream = new FileStream(path, FileMode.Open))
            {
                while (!sendingCompleted)
                {
                    if (!RetransmissionQueue.TryDequeue(out var chunkNumber))
                    {
                        continue;
                    }

                    logger.LogInfo($"Reading file chunk {chunkNumber + 1}");
                    var data = new byte[Consts.HeaderOffset + chunkSize];
                    var indexHeader = BitConverter.GetBytes(chunkNumber);
                    indexHeader.CopyTo(data, 0);

                    fileStream.Position = chunkNumber * chunkSize;
                    fileStream.Read(data, Consts.HeaderOffset, chunkSize);
                    ChunksQueue.Enqueue((chunkNumber, data));
                }
            }
        }

        void SendFileChunks(int chunksAmount, int chunkSize)
        {
            while (!sendingCompleted)
            {
                if (!ChunksQueue.TryDequeue(out var fileChunk))
                {
                    continue;
                }

                logger.LogInfo($"Sending chunk {fileChunk.chunkIndex+1}/{chunksAmount}");
                udpClient.Send(fileChunk.data, Consts.HeaderOffset + chunkSize);
            }
        }
        void AddChunkToRetransmissionQueue(string chunkNumber)
        {
            if (!int.TryParse(chunkNumber, out var chunk))
            {
                WriteLine($"Invalid request. Could not parse {chunkNumber}");
                return;
            }
            if (!RetransmissionQueue.Contains(chunk))
            {
                RetransmissionQueue.Enqueue(chunk);
            }
        }
    }
}
