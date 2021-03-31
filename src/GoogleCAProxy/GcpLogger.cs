using CSS.Common.Logging;
using Grpc.Core.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Keyfactor.AnyGateway.Google
{
    class GcpLogger : LoggingClientBase, ILogger

    {
        public GcpLogger()
        {
            Logger.Info("Create Gcp Logger Instance");
        }
        public void Debug(string message)
        {
            Logger.Debug(message);
        }

        public void Debug(string format, params object[] formatArgs)
        {
            Logger.DebugFormat(format,formatArgs);
        }

        public void Error(string message)
        {
            Logger.Error(message);
        }

        public void Error(string format, params object[] formatArgs)
        {
            Logger.ErrorFormat(format, formatArgs);
        }

        public void Error(Exception exception, string message)
        {
            Logger.Error($"{message} | {exception}");
        }

        public ILogger ForType<T>()
        {
            return new GcpLogger();
        }

        public void Info(string message)
        {
            Logger.Info(message);
        }

        public void Info(string format, params object[] formatArgs)
        {
            Logger.InfoFormat(format, formatArgs);
        }

        public void Warning(string message)
        {
            Logger.Warn(message);
        }

        public void Warning(string format, params object[] formatArgs)
        {
            Logger.WarnFormat(format, formatArgs);
        }

        public void Warning(Exception exception, string message)
        {
            Logger.Warn($"{message} | {exception}");
        }
    }
}
