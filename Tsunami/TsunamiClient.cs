using Common;
using Common.Logger;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Tsunami.Client
{
    public class TsunamiClient
    {
        private readonly ILogger logger;
        private TcpClient tcpClient;
        private UdpClient udpClient;
        private bool downloadFinished;
        private bool serverFinished;
        private ConcurrentQueue<(int chunkIndex, byte[] data)> ChunksQueue;
        private ConcurrentDictionary<int, bool> receivedChunks;

        object _lock = new();
        public TsunamiClient(ILogger logger)
        {
            this.logger = logger;
        }

        public void Connect(string host, int port)
        {
            try
            {
                tcpClient = new TcpClient();
                tcpClient.Connect(host, port);
                logger.LogSuccess($"Connected to {host}:{port}");
            }
            catch (Exception e)
            {
                logger.LogError($"Failed to connect. Exception: {e.Message}");
            }
        }
        public void Disconnect()
        {
            try
            {
                tcpClient?.Close();
                tcpClient?.Dispose();
                udpClient?.Close();
                udpClient?.Dispose();

                logger.LogSuccess("Disconnected.");
            }
            catch (Exception e)
            {
                logger.LogError($"Failed to disconnect. Exception: {e.Message}");
            }
        }

        private void WriteLine(string request) => tcpClient.GetStream().Write(Encoding.UTF8.GetBytes($"{request}\n"));
        private string ReadLine()
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
        public void GetFile(string filename, int port, int chunkSize)
        {
            if (!tcpClient?.Connected != false)
            {
                logger.LogInfo("Not connected!");
                return;
            }

            udpClient = new UdpClient(port);
            WriteLine($"file-info {filename}");
            udpClient.Client.ReceiveTimeout = 100; 
            var fileRes = ReadLine();

            if (!int.TryParse(fileRes, out var fileLength))
            {
                logger.LogError($"Failed to parse {fileRes}");
                return;
            }

            logger.LogInfo($"Downloading file: {filename} with size {fileLength} bytes");

            downloadFinished = false;
            var chunksAmount = Math.Ceiling(fileLength / (decimal)chunkSize);
            receivedChunks = new ConcurrentDictionary<int, bool>(Enumerable.Range(0, (int)chunksAmount).Select(x => new KeyValuePair<int, bool>(x, false)));
            ChunksQueue = new ConcurrentQueue<(int chunkIndex, byte[] data)>();

            var downloadSW = new Stopwatch();
            var writeSW = new Stopwatch();

            var tcpReceiver = new Thread(() => TcpReceiver());

            var download = new Thread(() => FileReceiver(fileLength, chunkSize, downloadSW, port));
            var writing = new Thread(() => Writer(filename, chunkSize, writeSW));

            WriteLine($"get {filename} {port} {chunkSize}");

            downloadSW.Start();
            writeSW.Start();

            download.Start();
            writing.Start();
            tcpReceiver.Start();

            writing.Join();

            logger.LogSuccess($"File downloaded.");
            logger.LogSuccess($"Download time: {downloadSW.Elapsed:G}");
            logger.LogSuccess($"Write time: {writeSW.Elapsed:G}");
            logger.LogSuccess($"File size: {fileLength} bytes ({Math.Round(fileLength / 1024.0, 2):# ### ###} kB)");
            Disconnect();
        }
        void TcpReceiver()
        {
            while (!downloadFinished)
            {

                var commandFromServer = ReadLine();
                switch (commandFromServer.TrimEnd('\n'))
                {
                    case "finished-sending":
                        lock (_lock)
                        {
                            if (!serverFinished)
                            {
                                Monitor.Wait(_lock);
                            }
                        }
                        //jezeli serwer skonczyl i nie ma juz dostepnych danych do odbioru to ponawiam
                        var chunks = "";
                        for (var i = 0; i < receivedChunks.Count; i++)
                        {
                            //przesylam zapytanie o brakujace chunki
                            if (receivedChunks[i] == false)
                            {
                                chunks += $" {i}";
                            }
                        }
                        if (chunks.Length > 0)
                        {
                            logger.LogInfo($"Requesting retransmission of chunks {chunks}");
                            WriteLine($"retransmit {chunks}");
                        }
    
                        break;
                }
            }
        }
        void FileReceiver(int fileLength, int chunkSize, Stopwatch stopwatch, int port)
        {
            var chunksAmount = Math.Ceiling(fileLength / (decimal)chunkSize);
            try
            {
                //jeżeli nie otrzymaliśmy jeszcze wszystkich
                while (receivedChunks.Any(x => x.Value == false))
                {
                    var iPEndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port);
            
                    lock (_lock)
                    {
                        serverFinished = udpClient.Available == 0;
                        if (serverFinished)
                        {
                            Monitor.Pulse(_lock);
                        }
                    }
                    try
                    {
                        var datagram = udpClient.Receive(ref iPEndPoint);
                        var chunkNumber = BitConverter.ToInt32(datagram.AsSpan()[..Consts.HeaderOffset]);
                        logger.LogInfo($"Downloaded chunk {chunkNumber + 1}/{chunksAmount}");

                        //jezeli mialem go juz wczesniej to pomijam
                        if (receivedChunks[chunkNumber])
                        {
                            continue;
                        }

                        ChunksQueue.Enqueue((chunkIndex: chunkNumber, data: datagram[Consts.HeaderOffset..]));

                        receivedChunks[chunkNumber] = true;
                    }
                    catch (Exception e)
                    {

                        logger.LogError(e.Message);
                    }
                }
                downloadFinished = true;
                WriteLine("done");
                stopwatch.Stop();
            }
            catch (Exception e)
            {
                logger.LogError(e.Message);
            }
            finally
            {
                //if (Monitor.IsEntered(_lock))
                //{
                //    Monitor.Exit(_lock);
                //}
                udpClient.Close();
                udpClient.Dispose();
            }
        }
        void Writer(string filename, int chunkSize, Stopwatch stopwatch)
        {
            var path = Path.Combine(Consts.ClientDownloadedFilesPath, filename);
            if (!Directory.Exists(Consts.ClientDownloadedFilesPath))
            {
                Directory.CreateDirectory(Consts.ClientDownloadedFilesPath);
            }
            using (var fileStream = new FileStream(path, FileMode.OpenOrCreate))
            {
                while (ChunksQueue.Count != 0 || !downloadFinished)
                {
                    if (ChunksQueue.TryDequeue(out var chunk))
                    {
                        logger.LogInfo($"Writing chunk {chunk.chunkIndex + 1}");
                        fileStream.Position = chunk.chunkIndex * chunkSize;
                        var data = chunk.data.ToArray();
                        fileStream.Write(data, 0, data.Length);
                    }
                }
            }

            stopwatch.Stop();
        }
    }
}
