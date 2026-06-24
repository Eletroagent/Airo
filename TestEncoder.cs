using System;
using System.Reflection;

namespace AiroWebRTCServer
{
    class TestEncoder
    {
        public static void RunList()
        {
            var assembly = Assembly.Load("SIPSorcery");
            var type = assembly.GetType("SIPSorcery.Net.MediaStreamTrack");
            if (type != null)
            {
                foreach (var method in type.GetConstructors())
                {
                    Console.WriteLine("METHOD: " + method.ToString());
                }
            }
        }
    }
}
