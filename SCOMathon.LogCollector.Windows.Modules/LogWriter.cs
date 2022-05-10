using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace SCOMathon.LogCollector.Windows.Modules
{
  public static class LogWriter
  {
    private const string SourceName = "SCOMathon Log Collector Module";
    private const int eventBaseID = 15100;


    static LogWriter()
    {
      if (!EventLog.SourceExists(SourceName))
      {
        EventSourceCreationData sourceInfo = new EventSourceCreationData(SourceName, "Operations Manager");
        EventLog.CreateEventSource(sourceInfo);
      }
    }

    public static void WriteInformation(string message)
    {
      logWriteEvent(message, EventLogEntryType.Information);
    }
    public static void WriteWarning(string message)
    {
      logWriteEvent(message, EventLogEntryType.Warning);
    }
    public static void WriteError(string message)
    {
      logWriteEvent(message, EventLogEntryType.Error);
    }

    public static void WriteException(string message, Exception e)
    {
      string exceptionDescription = "";
      exceptionDescription += "Message: " + (message ?? "<NULL message>") + "\r\n\r\n";
      exceptionDescription += "Exceptions:\r\n";
      Exception loopException = e;
      int ordernum = 1;
      do
      {
        exceptionDescription += ordernum.ToString() + "): Exception type: " + loopException.GetType().Namespace + "." + loopException.GetType().Name + "\r\n";
        exceptionDescription += loopException.GetType().FullName + " exception (" + loopException.Message + ")";
        exceptionDescription += loopException.StackTrace + "\r\n\r\n";
        loopException = loopException.InnerException;
        ordernum++;
      } while (loopException != null);
      WriteError(exceptionDescription);
    }

    public static void WriteException(Exception e)
    {
      StackTrace stackTrace = new StackTrace();
      MethodBase callingMethod = stackTrace.GetFrame(1).GetMethod();
      string message = "Generic exception in the " + callingMethod.Name +
        " method of the " + callingMethod.DeclaringType.Name + " class.";
      WriteException(message, e);
    }

    private static void logWriteEvent(string message, EventLogEntryType type)
    {
      // it's not referenced in DEBUG release
      try
      {
        EventLog.WriteEntry(SourceName, message, type, eventBaseID);
      }
      catch
      {
      }
    }
  }
}
