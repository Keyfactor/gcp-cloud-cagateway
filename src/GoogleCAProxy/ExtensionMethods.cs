using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Keyfactor.AnyGateway.Google
{    public static class ExtensionMethods
    {
        public static bool IsFullPathReadable(this string filePath)
        {
            try
            {
                byte[] fileContent = File.ReadAllBytes(filePath);

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
