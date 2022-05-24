using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace Common
{
    public static class Consts
    {
        public const string HostAddress = "127.0.0.1";
        public const int TcpPort = 8000;
        public readonly static int HeaderOffset = sizeof(int);
        public const string ClientDownloadedFilesPath = "../../../DownloadedFiles";
        public const string ServerFilesPath = "../../../ServerFiles";
    }
}
