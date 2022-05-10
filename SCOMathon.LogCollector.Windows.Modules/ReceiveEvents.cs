using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Permissions;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.EnterpriseManagement.HealthService;
using Microsoft.EnterpriseManagement.Mom.Modules.DataItems;


namespace SCOMathon.LogCollector.Windows.Modules
{
  [MonitoringModule(ModuleType.WriteAction)]
  [ModuleOutput(false)]
  public class ReceiveEvents : ModuleBase<DataItemBase>
  {
    protected readonly object shutdownLock;
    protected bool shutdown;

    private string DestinationPath;

    public ReceiveEvents(ModuleHost<DataItemBase> moduleHost, XmlReader configuration, byte[] previousState) : base(moduleHost)
    {
      if (moduleHost == null)
        throw new ArgumentNullException(nameof(moduleHost));
      if (configuration == null)
        throw new ArgumentNullException(nameof(configuration));
      shutdownLock = new object();
      LoadConfiguration(configuration);
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

        configuration.ReadStartElement("DestinationPath");
        DestinationPath = configuration.ReadString();
        configuration.ReadEndElement();

        configuration.ReadEndElement();

        DestinationPath = DestinationPath.Trim().TrimEnd(new char[] { '\\' });
      }
      catch (Exception xe)
      {
        LogWriter.WriteException("Failed to load configuration.", xe);
        throw new ModuleException("Error parsing configuration XML", xe);
      }
    }

    [InputStream(0)]
    public void OnNewDataItems(DataItemBase dataItem, DataItemAcknowledgementCallback acknowledgeCallback, object acknowledgedState, DataItemProcessingCompleteCallback completionCallback, object completionState)
    {
      if (acknowledgeCallback == null && completionCallback != null)
        throw new ArgumentNullException(nameof(acknowledgeCallback), "Either both or none of completion and acknowledge callbacks must be specified.");
      if (acknowledgeCallback != null && completionCallback == null)
        throw new ArgumentNullException(nameof(completionCallback), "Either both or none of competition and acknowledge callbacks must be specified.");

      bool NeedAcknowledge = acknowledgeCallback != null;
      lock (shutdownLock)
      {
        if (shutdown)
          return;
        try
        {
          string FilePath = dataItem.QueryItem("FilePath").ToString();
          string OriginalFileName = dataItem.QueryItem("OriginalFileName").ToString();
          string subFolderName = Path.GetFileNameWithoutExtension(OriginalFileName);
          subFolderName = Path.Combine(DestinationPath, subFolderName);


          if (!Directory.Exists(subFolderName))
          {
            Directory.CreateDirectory(subFolderName);
          }
          string DestinationFilePath = Path.Combine(subFolderName, Path.GetFileNameWithoutExtension(OriginalFileName) + DateTime.Now.ToString("-yyyy-MM-dd-HH-mm-ss") + ".zip");
          File.Copy(FilePath, DestinationFilePath, true);
        }
        catch (Exception e)
        {
          LogWriter.WriteException("Generic exception while receiving file.", e);
        }
        // if no data returned OR if exception thrown in GetOutputData(dataItems)
        if (NeedAcknowledge)
        {
          acknowledgeCallback(acknowledgedState);
          completionCallback(completionState);
        }
        ModuleHost.RequestNextDataItem();
      }
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
  }
}
