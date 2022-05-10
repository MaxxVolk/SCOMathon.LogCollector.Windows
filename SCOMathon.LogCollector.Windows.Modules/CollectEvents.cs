using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Formatters.Binary;
using System.Security.Permissions;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.IO.Compression;
using Microsoft.EnterpriseManagement.HealthService;
using Microsoft.EnterpriseManagement.Mom.Modules.DataItems;


namespace SCOMathon.LogCollector.Windows.Modules
{
  [MonitoringModule(ModuleType.ReadAction)]
  [ModuleOutput(true)]
  public class CollectEvents : ModuleBase<PropertyBagDataItem>
  {
    private string[] LogsToShip;
    private string ComputerName;

    protected readonly object shutdownLock;
    protected bool shutdown;

    public CollectEvents(ModuleHost<PropertyBagDataItem> moduleHost, XmlReader configuration, byte[] previousState) : base(moduleHost)
    {
      if (moduleHost == null)
        throw new ArgumentNullException(nameof(moduleHost));
      if (configuration == null)
        throw new ArgumentNullException(nameof(configuration));
      shutdownLock = new object();
      LoadConfiguration(configuration);
      LoadPreviousState(previousState);
    }

    private void LoadConfiguration(XmlReader configuration)
    {
      /*
       * <xsd:element minOccurs="1" name="LogsToShip" type="xsd:string" />
       * <xsd:element name="ComputerName" type="ComputerNameType" minOccurs="1" maxOccurs="1" />
       */
      try
      {
        // load parameters
        configuration.MoveToContent(); // "Configuration"
        configuration.ReadStartElement("Configuration");

        configuration.ReadStartElement("LogsToShip");
        string strLogsToShip = configuration.ReadString();
        configuration.ReadEndElement();

        configuration.ReadStartElement("ComputerName");
        ComputerName = configuration.ReadString();
        configuration.ReadEndElement();

        configuration.ReadEndElement();

        LogsToShip = strLogsToShip.Split(new char[] { ',', '|', ';' });
      }
      catch (Exception xe)
      {
        LogWriter.WriteException("Failed to load configuration.", xe);
        throw new ModuleException("Error parsing configuration XML", xe);
      }
    }

    protected object ModuleState { get; set; }

    [InputStream(0)]
    [RegistryPermission(SecurityAction.Demand)]
    public void OnNewDataItems(DataItemBase[] dataItems, bool logicallyGrouped, DataItemAcknowledgementCallback acknowledgeCallback, object acknowledgedState, DataItemProcessingCompleteCallback completionCallback, object completionState)
    {
      if (acknowledgeCallback == null && completionCallback != null)
        throw new ArgumentNullException(nameof(acknowledgeCallback), "Either both or none of completion and acknowledge callbacks must be specified.");
      if (acknowledgeCallback != null && completionCallback == null)
        throw new ArgumentNullException(nameof(completionCallback), "Either both or none of competition and acknowledge callbacks must be specified.");

      if (shutdown)
        return;
      try
      {
        Dictionary<string, EventBookmark> logBookmarks = (Dictionary<string, EventBookmark>)ModuleState;
        if (logBookmarks == null)
        {
          logBookmarks = new Dictionary<string, EventBookmark>();
          foreach (string LogName in LogsToShip)
            logBookmarks.Add(LogName, null);
        }
        string zipFileName = Path.Combine(Environment.GetEnvironmentVariable("TEMP"), $"{ComputerName}.zip");
        int totalEventsSaved = 0;
        using (EventLogSession eventLogSession = new EventLogSession(ComputerName))
        {
          using (ZipArchive zip = ZipFile.Open(zipFileName, ZipArchiveMode.Create))
          {
            foreach (string LogName in LogsToShip)
            {
              if (logBookmarks[LogName] == null)
              {
                EventLogQuery eventsQuery = new EventLogQuery(LogName, PathType.LogName)
                {
                  TolerateQueryErrors = true,
                  Session = eventLogSession,
                  ReverseDirection = true
                };
                using (EventLogReader logReader = new EventLogReader(eventsQuery))
                {
                  using (EventRecord logEvent = logReader.ReadEvent())
                    logBookmarks[LogName] = logEvent.Bookmark;
                  ModuleState = logBookmarks;
                  SavePreviousState();
                }
              }
              else
              {
                EventLogQuery eventsQuery = new EventLogQuery(LogName, PathType.LogName)
                {
                  TolerateQueryErrors = true,
                  Session = eventLogSession,
                  ReverseDirection = false
                };

                using (EventLogReader logReader = new EventLogReader(eventsQuery, logBookmarks[LogName]))
                {
                  using (MemoryStream logData = new MemoryStream())
                  {
                    using (StreamWriter stringWriter = new StreamWriter(logData))
                    {
                      EventRecord currentEvent = logReader.ReadEvent();
                      while (currentEvent != null)
                      {
                        using (currentEvent)
                        {
                          stringWriter.WriteLine(currentEvent.ToXml());
                          totalEventsSaved++;
                          logBookmarks[LogName] = currentEvent.Bookmark;
                        }
                        currentEvent = logReader.ReadEvent();
                      };
                      // finalizing everything
                      SavePreviousState();
                      stringWriter.Flush();
                      logData.Seek(0, SeekOrigin.Begin);
                      using (Stream entryStream = zip.CreateEntry($"{LogName}.xmllog").Open())
                        logData.WriteTo(entryStream);
                    }
                  }
                }
              }
            }
          }
        }
        if (File.Exists(zipFileName))
        {
          if (totalEventsSaved > 0)
          {
            // return file name to upload
            LogWriter.WriteInformation($"Sending the archive {zipFileName}, total events in this package: {totalEventsSaved}.");
            Dictionary<string, object> bagItem = new Dictionary<string, object>
                {
                  { "DataGenerated", "OK" },
                  { "ResultPath", zipFileName }
                };
            LogWriter.WriteInformation($"Returning Zip file at {zipFileName}.");
            ModuleHost.PostOutputDataItem(CreatePropertyBag(bagItem), new DataItemAcknowledgementCallback(OnAcknowledgementCallback), zipFileName);
            return;
          }
          else
          {
            // file is actually empty totalEventsSaved == 0
            try { File.Delete(zipFileName); } catch { }
            ModuleHost.RequestNextDataItem();
            return;
          }

        }
        else
        {
          LogWriter.WriteWarning("The file created doesn't exist.");
          ModuleHost.RequestNextDataItem();
          return;
        }
      }
      catch (Exception e)
      {
        LogWriter.WriteException("Collect Events action failed.", e);
        ModuleHost.RequestNextDataItem();
        return;
      }
    }

    protected void OnAcknowledgementCallback(object state)
    {
      try
      {
        LogWriter.WriteInformation("File has been sent, deleting.");
        string zipFileName = (string)state;
        File.Delete(zipFileName);
      }
      catch (Exception e)
      {
        LogWriter.WriteException("Failed to delete results file.", e);
      }
      finally
      {
        ModuleHost.RequestNextDataItem();
      }
    }

    public static PropertyBagDataItem CreatePropertyBag(Dictionary<string, object> bagItem)
    {
      Dictionary<string, Dictionary<string, object>> dictionary = new Dictionary<string, Dictionary<string, object>>
      {
        { "", bagItem }
      };
      return new PropertyBagDataItem(null, dictionary);
    }

    public sealed override void Shutdown()
    {
      lock (shutdownLock)
      {
        shutdown = true;
      }
    }

    public sealed override void Start()
    {
      lock (shutdownLock)
      {
        if (shutdown)
          return;
        ModuleHost.RequestNextDataItem();
      }
    }

    protected virtual void LoadPreviousState(byte[] previousState)
    {
      if (previousState != null)
        try
        {
          using (MemoryStream memoryStream = new MemoryStream(previousState))
          {
            BinaryFormatter binaryFormatter = new BinaryFormatter();
            try
            {
              ModuleState = binaryFormatter.Deserialize(memoryStream);
            }
            catch
            {
              ModuleState = null;
            }
          }
        }
        catch (Exception e)
        {
          try
          {
            string msg = $"Failed to load module's previous state. Empty state is used. Error: {e.Message}";
            LogWriter.WriteError(msg);
          }
          catch { } // ignore
        }
    }

    protected bool SavePreviousState()
    {
      if (ModuleState == null)
        return false;
      lock (shutdownLock)
      {
        using (MemoryStream memoryStream = new MemoryStream())
        {
          BinaryFormatter binaryFormatter = new BinaryFormatter();
          try
          {
            binaryFormatter.Serialize(memoryStream, ModuleState);
            ModuleHost.SaveState(memoryStream.GetBuffer(), (int)memoryStream.Length);
            return true;
          }
          catch (Exception e)
          {
            try
            {
              string msg = $"Failed to save module's previous state. Error: {e.Message}";
              LogWriter.WriteError(msg);
            }
            catch { } // ignore
            return false;
          }
        }
      }
    }
  }
}
