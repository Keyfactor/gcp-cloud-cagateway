using Google.Protobuf.WellKnownTypes;
using Keyfactor.AnyGateway.Google;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Google.Cloud.Security.PrivateCA.V1;
using Google.Protobuf;

namespace GoogleCASandbox
{
    class Program
    {
        static void Main(string[] args)
        {
            Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS",
                @"C:\cms\concise-frame-296019-2e104088b76a.json");
        }
    }
}
