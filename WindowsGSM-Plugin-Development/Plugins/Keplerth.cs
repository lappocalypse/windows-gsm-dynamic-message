using System;
using System.Text;
using System.Diagnostics;
using System.Threading.Tasks;
using WindowsGSM.Functions;
using WindowsGSM.GameServer.Engine;
using WindowsGSM.GameServer.Query;

namespace WindowsGSM.Plugins
{
    public class Keplerth : SteamCMDAgent // SteamCMDAgent is used because relies on SteamCMD for installation and update process
    {
        // - Plugin Details
        public Plugin Plugin = new Plugin
        {
            name = "WindowsGSM.Keplerth.cs", // WindowsGSM.XXXX
            author = "lappocalypse",
            description = "🧩 WindowsGSM plugin for supporting Keplerth Dedicated Server",
            version = "1.0",
            url = "https://github.com/XXXXXXXX/XXXXXXXX", // Github repository link (Best practice)
            color = "#9eff99" // Color Hex
        };


        // - Standard Constructor and properties
        public Keplerth(ServerConfig serverData) : base(serverData) => base.serverData = _serverData = serverData;
        private readonly ServerConfig _serverData; // Store server start metadata, such as start ip, port, start param, etc


        // - Settings properties for SteamCMD installer
        public override bool loginAnonymous => false; //  loginAnonymous 
        public override string AppId => "747200"; // Game server appId


        // - Game server Fixed variables
        public override string StartPath => "Keplerth.exe"; // Game server start path
        public string BackupsavePath = "Backupsave.bat"; // Game server Backupsave path        
        public string FullName = "Keplerth Dedicated Server"; // Game server FullName
        public bool AllowsEmbedConsole = false;  // Does this server support output redirect?
        public int PortIncrements = 1; // This tells WindowsGSM how many ports should skip after installation
        public object QueryMethod = null; // Query method should be use on current server type. Accepted value: null or new A2S() or new FIVEM() or new UT3()


        // - Game server default values
        public string Port = "7777"; // Default port
        public string QueryPort = "7777"; // Default query port
        public string Defaultmap = "lappocalypse a 3"; // Default map name
        public string Maxplayers = "4"; // Default maxplayers
        public string Additional = "-batchmode -nographics"; // Additional server start parameter


        // - Create a default cfg for the game server after installation
        public async void CreateServerCFG() { }


        // - Start server function, return its Process to WindowsGSM
        public async Task<Process> Start()
        {
            // Prepare start parameter
            var param = new StringBuilder();
            param.Append(string.IsNullOrWhiteSpace(_serverData.ServerPort) ? string.Empty : $" -port={_serverData.ServerPort}");
            param.Append(string.IsNullOrWhiteSpace(_serverData.ServerName) ? string.Empty : $" -name=\"{_serverData.ServerName}\"");
            param.Append(string.IsNullOrWhiteSpace(_serverData.ServerParam) ? string.Empty : $" {_serverData.ServerParam}");
 
            // Prepare Process
            var p = new Process
            {
                StartInfo =
                {
                    WindowStyle = ProcessWindowStyle.Minimized,
                    UseShellExecute = false,
                    WorkingDirectory = ServerPath.GetServersServerFiles(_serverData.ServerID),
                    FileName = ServerPath.GetServersServerFiles(_serverData.ServerID, StartPath),
                    Arguments = param.ToString()
                },
                EnableRaisingEvents = true
            };

           // Prepare Process
           var s = new Process
           {
               StartInfo =
               {
                   WindowStyle = ProcessWindowStyle.Minimized,
                   UseShellExecute = false,
                   WorkingDirectory = ServerPath.GetServersServerFiles(_serverData.ServerID),
                   FileName = ServerPath.GetServersServerFiles(_serverData.ServerID, BackupsavePath),
                   
               },
               EnableRaisingEvents = true
           };
        
            // Start Process
            try
            {
                p.Start();
                
                System.Threading.Thread.Sleep(15000);
                Functions.ServerConsole.SendMessageToMainWindow(p.MainWindowHandle, "7777");
                System.Threading.Thread.Sleep(2000);
                Functions.ServerConsole.SendMessageToMainWindow(p.MainWindowHandle, "2");
                System.Threading.Thread.Sleep(2000);

                s.Start();

                return p;
                
            }
            catch (Exception e)
            {
                base.Error = e.Message;
                return null; // return null if fail to start
            }

        }


        // - Stop server function
        public async Task Stop(Process p)
        {
            
            await Task.Run(() =>
            {
                Functions.ServerConsole.SendMessageToMainWindow(p.MainWindowHandle, "s");                
                System.Threading.Thread.Sleep(2000);
                Functions.ServerConsole.SetMainWindow(p.MainWindowHandle);
                Functions.ServerConsole.SendWaitToMainWindow("^c");
            });
            await Task.Delay(2000);
        
        } 
    } 

}

        