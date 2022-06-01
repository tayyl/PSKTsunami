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
        private ConcurrentQueue<(int chunkIndex, byte[] data, int chunkSize)> ChunksQueue;
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
            while ((response = ReadLine()?.TrimEnd('\n')) != "done")
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
                        AddChunksToRetransmissionQueue(argument);
                        break;
                    case "file-info":
                        SendFileInfo(argument);
                        break;
                }
            }
            WriteLine("done");
            sendingCompleted = true;

            logger.LogInfo("Finished receiving");
        }
        void SendFileInfo(string command)
        {
            var commands = command.Split(' ');
            var filename = commands.ElementAtOrDefault(0);
            var filePath = Path.Combine(Consts.ServerFilesPath, filename);
            if (string.IsNullOrEmpty(filename) || !File.Exists(filePath))
            {
                WriteLine($"No file with name {filename} found on server.");
                return;
            }

            try
            {
                logger.LogSuccess($"Received request for file info {filename} from {tcpClient.Client.RemoteEndPoint}");

                var fileInfo = new FileInfo(filePath);

                WriteLine(fileInfo.Length.ToString());
            }
            catch (Exception ex)
            {

                logger.LogError($"Failed to send file info {filename}. Exception: {ex.Message}");
            }
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
            ChunksQueue = new ConcurrentQueue<(int chunkIndex, byte[] data, int chunkSize)>();
            try
            {
                logger.LogSuccess($"Received request for file {filename} from {tcpClient.Client.RemoteEndPoint}");

                var fileInfo = new FileInfo(filePath);
                var chunkCount = Math.Ceiling(fileInfo.Length / (decimal)chunkSizeInt);

                udpClient = new UdpClient();
                var clientEndpoint = tcpClient.Client.RemoteEndPoint as IPEndPoint;
                udpClient.Connect(clientEndpoint.Address, clientPortInt);
                sendingCompleted = false;
                logger.LogInfo($"Reading file: {filename} with size of {fileInfo.Length} bytes");
                RetransmissionQueue = new ConcurrentQueue<int>(Enumerable.Range(0, (int)chunkCount));
                var reader = new Thread(() => ReadFileChunks(filename, chunkSizeInt));
                var sender = new Thread(() => SendFileChunks((int)chunkCount, chunkSizeInt));
                reader.Start();
                sender.Start();

                reader.Join();

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
                    var dataToRead = (fileStream.Position + chunkSize) > fileStream.Length ? fileStream.Length - fileStream.Position : chunkSize;
                    fileStream.Read(data, Consts.HeaderOffset, (int)dataToRead);
                    ChunksQueue.Enqueue((chunkNumber, data, (int)dataToRead));
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

                logger.LogInfo($"Sending chunk {fileChunk.chunkIndex + 1}/{chunksAmount}");
                udpClient.Send(fileChunk.data, Consts.HeaderOffset + fileChunk.chunkSize);
                if (RetransmissionQueue.IsEmpty && ChunksQueue.IsEmpty)
                {
                    logger.LogInfo("Finished sending all chunks - informing client..");
                    WriteLine("finished-sending");
                }
            }
        }
        void AddChunksToRetransmissionQueue(string chunks)
        {
            var chunksSplitted = chunks.Trim().TrimEnd().Split(' ');

            logger.LogInfo($"Requested for retransmition of chunks {chunks}");
            foreach (var chunk in chunksSplitted)
            {

                if (int.TryParse(chunk, out var chunkAsInt))
                {
                    RetransmissionQueue.Enqueue(chunkAsInt);
                }
            }

        }
    }
}
