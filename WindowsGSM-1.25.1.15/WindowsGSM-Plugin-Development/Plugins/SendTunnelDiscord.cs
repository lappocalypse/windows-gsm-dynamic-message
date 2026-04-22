using System;
using System.Diagnostics;
using WindowsGSM.Functions;
using WindowsGSM.GameServer.Engine;

namespace WindowsGSM.Plugins
{
    public class SendTunnelDiscord
    {
        public Plugin Plugin = new Plugin
        {
            name = "SendTunnelDiscord",
            author = "Frank",
            description = "Execute pccommand.bat to send tunnel.txt to Discord",
            version = "1.0",
            url = ""
        };

        private string BatFile = @"C:\serveur\servers\47\serverfiles\pccommand.bat";

        public bool Start()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = BatFile;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;

                Process proc = Process.Start(psi);

                proc.WaitForExit();

                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}