using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Kinect;
using Microsoft.Kinect.Toolkit;

namespace KinectVJ
{
    internal class Program
    {
        static void Main(string[] args)
        {
            Sensor sensor = new Sensor();

            // Hook Ctrl + c to cancel the program
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                Console.WriteLine("Ctrl + C detected, shutting down...");
                sensor.Dispose();
                Environment.Exit(0);
            };

            // Attach to Kinect - Raise error if not found
            try
            {
                sensor.AttachToKinect();
            }
            catch (InvalidOperationException err)
            {
                Console.Error.WriteLine(err.Message);
                Environment.Exit(1);
            }

            sensor.EnableKinect();
            sensor.BeginStreaming();

        }
    }
}
