using System;
using System.Text;
using System.Diagnostics;
using System.Threading.Tasks;
using WindowsGSM.Functions;
using WindowsGSM.GameServer.Query;
using WindowsGSM.GameServer.Engine;
using System.IO;
using System.Linq;
using System.Net;



namespace WindowsGSM.Plugins
{
    public class PixArk : SteamCMDAgent
    {
        // - Plugin Details
        public Plugin Plugin = new Plugin
        {
            name = "WindowsGSM.sPixArk", // WindowsGSM.XXXX
            author = "frank",
            description = "WindowsGSM plugin for supporting ArkSurvivalAscended Dedicated Server",
            version = "1.0",
             url = "", // Github repository link (Best practice)
            color = "#34c9eb" // Color Hex
        };

        // - Settings properties for SteamCMD installer
        public override bool loginAnonymous => true;
        public override string AppId => "824360"; // Game server appId

        // - Standard Constructor and properties
        public PixArk(ServerConfig serverData) : base(serverData) => base.serverData = _serverData = serverData;
        private readonly ServerConfig _serverData;


        // - Game server Fixed variables
        public new string StartPath = @"closepc.bat"; // Game server start path
        public string FullName = "shutdown"; // Game server FullName
        public bool AllowsEmbedConsole = false;  // Does this server support output redirect?
        public int PortIncrements = 2; // This tells WindowsGSM how many ports should skip after installation
        public object QueryMethod = new A2S(); // Query method should be use on current server type. Accepted value: null or new A2S() or new FIVEM() or new UT3()


        // - Game server default values
        public string Port = "1"; // Default port
        public string QueryPort = "1"; // Default query port
        public string Defaultmap = "shutdown"; // Default map name
        public string Maxplayers = "10"; // Default maxplayers
        public string Additional = ""; // Additional server start parameter


        // - Create a default cfg for the game server after installation
        public async void CreateServerCFG()
        {
            //No config file seems
        }

        // - Start server function, return its Process to WindowsGSM
        public async Task<Process> Start()
        {
            string shipExePath = Functions.ServerPath.GetServersServerFiles(_serverData.ServerID, StartPath);
            if (!File.Exists(shipExePath))
            {
                Error = $"{Path.GetFileName(shipExePath)} not found ({shipExePath})";
                return null;
            }

            var param = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(_serverData.ServerMap))
                param.Append($"{_serverData.ServerMap}");

            param.Append("?listen");

            if (!string.IsNullOrWhiteSpace(_serverData.ServerName))
                param.Append($"?SessionName=\"{_serverData.ServerName}\"");

            if (!string.IsNullOrWhiteSpace(_serverData.ServerIP))
                param.Append($"?MultiHome={_serverData.ServerIP}");

            if (!string.IsNullOrWhiteSpace(_serverData.ServerPort))
                param.Append($"?Port={_serverData.ServerPort}");

            if (!string.IsNullOrWhiteSpace(_serverData.ServerQueryPort))
                param.Append($"?QueryPort={_serverData.ServerQueryPort}");

            if (!string.IsNullOrWhiteSpace(_serverData.ServerMaxPlayer))
                param.Append($"?MaxPlayers={_serverData.ServerMaxPlayer}");

            if (!string.IsNullOrWhiteSpace(_serverData.ServerParam))
                param.Append($"{_serverData.ServerParam}");

            Process p = new Process
            {
                StartInfo =
                {
                    FileName = shipExePath,
                    Arguments = param.ToString(),
                    WindowStyle = ProcessWindowStyle.Minimized,
                    UseShellExecute = true
                },
                EnableRaisingEvents = true
            };
            p.Start();

            return p;
        }


        // - Stop server function
        public async Task Stop(Process p)
        {
            await Task.Run(() =>
            {
                p.Kill();
            });
        }
    }
}
