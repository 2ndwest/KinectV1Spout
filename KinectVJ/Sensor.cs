using System;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;
using Microsoft.Kinect;
using Microsoft.Kinect.Toolkit.BackgroundRemoval;
using Spout.Interop;
using OpenGL;

// Whoa Putz! For use on hall next year unless I can get ahold of a Kinect v2
// If I can do that I'll write a TiXL integration and we can move past this junk
// inb4 "code is shit" stfu. Putz is committed to overdesign. If it worked 100% it wouldn't be putz
// If, for whatever reason it's past the year 2026 and this code is still in use:
// 1. Get a Kinect v2, they're cheap now. My v1 was $5 at swapfest.
// 2. Wtaf, that last sentence was autocompeted by AI, I didn't write that
// 3. Kill James Randall (me) for not upgrading yet
// 4. If I haven't graduated yet, get me to teach somebody how to maintain everything
// 5. If I have graduated, reach out to me if you have any questions
// jgrandall.73 on Signal (preferred), jgrandall@pm.me (email)

namespace KinectVJ
{
    internal class Sensor : IDisposable
    {
        private bool _disposed;

        // Basic Kinect Variables
        private KinectSensor _sensor;
        private uint width = 640;
        private uint height = 480;
        private uint bytesPerPixel = 4; // BGRA format
        private BackgroundRemovedColorStream _backgroundRemovedColorStream;
        private Skeleton[] _skeletons; // Contains data of all skeletons tracked by Kinect
        private int _currentlyTrackedSkeletonId;

        // Define stuff for OpenGL initialization
        private IntPtr _glContext;
        private static SpoutSender _spoutSender;
        private DeviceContext _deviceContext;

        // Create a separate thread for OpenGL and Spout
        private Thread _glThread;
        // Also an array for storing frames to be sent over Spout
        private BlockingCollection<byte[]> _frameQueue = new BlockingCollection<byte[]>(boundedCapacity: 2);

        // The Spout C# wrapper has some memory management issues and this seems to help
        private uint _bufferLength;
        private readonly AutoResetEvent _frameReadyEvent = new AutoResetEvent(false);

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            if (disposing)
            {
                StopStreaming();
            }
        }

        // Shut down Kinect nicely
        private void StopStreaming()
        {
            // Wait for the GL Thread to finish and rejoin
            _frameQueue.CompleteAdding();
            _glThread?.Join();

            // Get rid of everything
            if (_frameQueue != null) _frameQueue.Dispose();

            if (_backgroundRemovedColorStream != null)
            {
                _backgroundRemovedColorStream.BackgroundRemovedFrameReady -= BackgroundRemovedFrameReadyHandler;
                _backgroundRemovedColorStream.Dispose();
                _backgroundRemovedColorStream = null;
            }

            if (_sensor != null)
            { 
                _sensor.AllFramesReady -= AllFramesReadyHandler;
                if (_sensor.DepthStream.IsEnabled) _sensor.DepthStream.Disable();
                if (_sensor.ColorStream.IsEnabled) _sensor.ColorStream.Disable();
                if (_sensor.SkeletonStream.IsEnabled) _sensor.SkeletonStream.Disable();

                if (_sensor.IsRunning) _sensor.Stop();
                _sensor.Dispose();
                _sensor = null;
            }

            if (_deviceContext != null)
            {
                _deviceContext.MakeCurrent(IntPtr.Zero);

                _deviceContext.DeleteContext(_glContext);
                _glContext = IntPtr.Zero;
                _deviceContext.Dispose();
                _deviceContext = null;
            }

            if (_spoutSender != null)
            {
                _spoutSender.ReleaseSender(0u);
                _spoutSender.Dispose();
                _spoutSender = null;
            }

            // Brute force clean the shit out of everything
            // Might make the memory problem with the C# wrapper a little bit better. Not sure :|
            GC.Collect();
            GC.WaitForPendingFinalizers();

        }

        // Attachto Kinect
        public void AttachToKinect()
        {
            foreach (var potentialSensor in KinectSensor.KinectSensors)
            {
                if (potentialSensor.Status == KinectStatus.Connected)
                {
                    _sensor = potentialSensor;
                    Console.WriteLine("Kinect Detected - Attaching");
                    break;
                }
            }

            if (this._sensor == null)
                throw new InvalidOperationException("No Kinect Detected");

            return;
        }

        // Enable streaming from Kinect
        // Turns on different sensors and stuff
        public void EnableKinect()
        { 

            _sensor.ColorStream.Enable(ColorImageFormat.RgbResolution640x480Fps30);
            _sensor.DepthStream.Enable(DepthImageFormat.Resolution640x480Fps30);
            _sensor.SkeletonStream.Enable();
            _backgroundRemovedColorStream = new BackgroundRemovedColorStream(_sensor);
            _backgroundRemovedColorStream.Enable(
                ColorImageFormat.RgbResolution640x480Fps30,
                DepthImageFormat.Resolution640x480Fps30);
        }

        // Begin streaming from Kinect
        public void BeginStreaming()
        {
            // Fire up the GL+Spout thread
            _glThread = new Thread(RunGlLoop) { IsBackground = true };
            _glThread.Start();
            
            // Create the skeletons array to hold the skeleton data
            _skeletons = new Skeleton[_sensor.SkeletonStream.FrameSkeletonArrayLength];

            _bufferLength = width * height * bytesPerPixel; // 640x480 image with 4 bytes per pixel (BGRA)

            // Add an event handler for when the removed background frame is ready
            _backgroundRemovedColorStream.BackgroundRemovedFrameReady += BackgroundRemovedFrameReadyHandler;

            // Add an event handler for when all frames are ready
            _sensor.AllFramesReady += AllFramesReadyHandler;

            _sensor.Start();
        }

        // Separately threaded process for sending frames over Spout
        private void RunGlLoop()
        {
            // The following runs once when the thread starts

            // Create & bind GL context
            // Taken from example at https://github.com/Ruminoid/Spout.NET
            _deviceContext = DeviceContext.Create();
            _glContext = _deviceContext.CreateContext(IntPtr.Zero);
            _deviceContext.MakeCurrent(_glContext);

            // create Spout sender
            _spoutSender = new SpoutSender();
            _spoutSender.CreateSender("KinectSpoutSender", width, height, 0);
            
            // Create Bitmap for locking in image before sending
            Bitmap _bitmap = new Bitmap((int)width, (int)height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            Console.WriteLine("Beginning to stream - press Ctrl + C at any point to quit.");

            // The following runs indefinitely in the thread

            // Send frames from correct buffer
            foreach (var frame in _frameQueue.GetConsumingEnumerable())
            {
                // Lock bitmap in place
                Rectangle rect = new Rectangle(0, 0, (int)width, (int)height);
                var bmpData = _bitmap.LockBits(rect, ImageLockMode.WriteOnly, _bitmap.PixelFormat);
                Marshal.Copy(frame, 0, bmpData.Scan0, frame.Length);
                
                unsafe
                {
                    fixed (byte* p = frame)
                    {
                        _deviceContext.MakeCurrent(_glContext);
                        _spoutSender.SendImage((byte*)bmpData.Scan0, 640, 480, Gl.BGRA, false, 0u);
                    }
                }
                _bitmap.UnlockBits(bmpData);
            }
        }

        // Called whenever a frame has had the background removed
        // Writes it into the frame queue where it's picked up by the GL thread
        private void BackgroundRemovedFrameReadyHandler(object sender, BackgroundRemovedColorFrameReadyEventArgs e)
        {
            using (var frame = e.OpenBackgroundRemovedColorFrame())
            {
                if (frame == null) return;

                byte[] managedBuffer = new byte[_bufferLength];
                frame.CopyPixelDataTo(managedBuffer);

                try
                {
                    while (!_frameQueue.TryAdd(managedBuffer))
                        _frameQueue.TryTake(out _);
                }
                catch (InvalidOperationException) { } // Occurs during dipose, can be swallowed.
            }
        } 

        // Process depth, color, skeleton data to remove background
        private void AllFramesReadyHandler(object sender, AllFramesReadyEventArgs e)
        {
            try
            { 
                // Process depth data to remove background
                using (var depthFrame = e.OpenDepthImageFrame())
                {
                    if (null != depthFrame)
                    {
                        _backgroundRemovedColorStream.ProcessDepth(depthFrame.GetRawPixelData(), depthFrame.Timestamp);
                    }
                }
                // Process color data to remove background
                using (var colorFrame = e.OpenColorImageFrame())
                {
                    if (null != colorFrame)
                    {
                        _backgroundRemovedColorStream.ProcessColor(colorFrame.GetRawPixelData(), colorFrame.Timestamp);
                    }
                }
                // Process skeleton data to remove background
                using (var skeletonFrame = e.OpenSkeletonFrame())
                {
                    try
                    {
                         skeletonFrame.CopySkeletonDataTo(_skeletons);
                        _backgroundRemovedColorStream.ProcessSkeleton(_skeletons, skeletonFrame.Timestamp);
                    }
                    catch (ArgumentNullException) 
                    { }
                }
                ChooseSkeleton(); 
            }
            catch(InvalidOperationException) { }
        }

        // Select which skeleton to track
        // Lifted pretty much directly from the Kinect SDK example code
        private void ChooseSkeleton()
        {
            var isTrackedSkeltonVisible = false;
            var nearestDistance = float.MaxValue;
            var nearestSkeleton = 0;

            foreach (var skel in _skeletons)
            {
                if (null == skel) continue;

                if (skel.TrackingState != SkeletonTrackingState.Tracked) continue;

                if (skel.TrackingId == _currentlyTrackedSkeletonId)
                {
                    isTrackedSkeltonVisible = true;
                    break;
                }

                if (skel.Position.Z < nearestDistance)
                {
                    nearestDistance = skel.Position.Z;
                    nearestSkeleton = skel.TrackingId;
                }
            }

            if (!isTrackedSkeltonVisible && nearestSkeleton != 0)
            {
                _backgroundRemovedColorStream.SetTrackedPlayer(nearestSkeleton);
                _currentlyTrackedSkeletonId = nearestSkeleton;
            }
        }
    }
}
