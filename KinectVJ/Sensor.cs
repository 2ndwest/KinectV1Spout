using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Runtime.InteropServices;
using Microsoft.Kinect;
using Microsoft.Kinect.Toolkit.BackgroundRemoval;
using Spout.Interop;
using OpenGL;

namespace KinectVJ
{
    internal class Sensor : IDisposable
    {
        private bool _disposed;

        private KinectSensor _sensor;
        private BackgroundRemovedColorStream _backgroundRemovedColorStream;
        private Skeleton[] _skeletons;
        private int _currentlyTrackedSkeletonId;

        // Define stuff for OpenGL / Spout transmission
        private IntPtr _glContext;
        private static SpoutSender _spoutSender;
        private DeviceContext _deviceContext;

        // Define stuff for the separate GL thread
        private Thread _glThread;
        private BlockingCollection<byte[]> _frameQueue = new BlockingCollection<byte[]>(boundedCapacity: 2);

        // Super overkill global buffer system for queueing frames
        // The Spout C# wrapper has some memory management issues and this seems to help
        private IntPtr _frameBufferPtr1 = IntPtr.Zero;
        private IntPtr _frameBufferPtr2 = IntPtr.Zero;
        private int _bufferLength = 640 * 480 * 4; // BGRA format
        private volatile bool _useFirstBuffer = true; // which buffer to write next
        private readonly AutoResetEvent _frameReadyEvent = new AutoResetEvent(false);
        private volatile bool _stopRequested = false;  // signal for stopping GL thread

        // Iterate through known Kinects and store active.

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
            _stopRequested = true;
            _frameQueue.CompleteAdding();
            _glThread?.Join();
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

            // Free unmanaged buffers
            if (_frameBufferPtr1 != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_frameBufferPtr1);
                _frameBufferPtr1 = IntPtr.Zero;
            }
            if (_frameBufferPtr2 != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_frameBufferPtr2);
                _frameBufferPtr2 = IntPtr.Zero;
            }

            // Brute force clean the shit out of everything
            GC.Collect();
            GC.WaitForPendingFinalizers();

        }
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

            // Raise exception if no connection
            if (this._sensor == null)
                throw new InvalidOperationException("No Kinect Detected");

            return;
        }

        // Enable streaming from Kinect
        public void EnableKinect()
        { 

            _sensor.ColorStream.Enable(ColorImageFormat.RgbResolution640x480Fps30);
            _sensor.DepthStream.Enable(DepthImageFormat.Resolution640x480Fps30);
            _sensor.SkeletonStream.Enable();
            _backgroundRemovedColorStream = new BackgroundRemovedColorStream(_sensor);
            _backgroundRemovedColorStream.Enable(
                ColorImageFormat.RgbResolution640x480Fps30,
                DepthImageFormat.Resolution640x480Fps30);

            _skeletons = new Skeleton[_sensor.SkeletonStream.FrameSkeletonArrayLength];
            Console.WriteLine("Skeletons list initialized");

        }

        // Begin streaming from Kinect
        public void BeginStreaming()
        {
            Console.WriteLine("Initializing Streaming...");

            // Fire up the GL+Spout thread
            _glThread = new Thread(RunGlLoop) { IsBackground = true };
            _glThread.Start();

            // Add an event handler for when the removed background frame is ready
            _backgroundRemovedColorStream.BackgroundRemovedFrameReady += BackgroundRemovedFrameReadyHandler;

            // Add an event handler for when all frames are ready
            _sensor.AllFramesReady += AllFramesReadyHandler;

            // Initialize the buffers to be the correct size for the Kinect frames
            _frameBufferPtr1 = Marshal.AllocHGlobal(_bufferLength);
            _frameBufferPtr2 = Marshal.AllocHGlobal(_bufferLength);
            _stopRequested = false;

            _sensor.Start();
        }

        // Separately threaded process for sending frames over Spout
        private void RunGlLoop()
        {
            // create & bind GL context
            // Taken from example at https://github.com/Ruminoid/Spout.NET
            _deviceContext = DeviceContext.Create();
            _glContext = _deviceContext.CreateContext(IntPtr.Zero);
            _deviceContext.MakeCurrent(_glContext);

            // create Spout sender
            _spoutSender = new SpoutSender();
            _spoutSender.CreateSender("KinectSpoutSender", 640, 480, 0);

            Console.WriteLine("Streaming...");

            while (!_stopRequested)
            { 
                _frameReadyEvent.WaitOne(); // Wait for a new frame to be ready
                if (_stopRequested) break; // Check if we should stop
                // Choose appropriate buffer to use
                IntPtr bufferToUse = _useFirstBuffer ? _frameBufferPtr1 : _frameBufferPtr2;

                unsafe
                {
                    _deviceContext.MakeCurrent(_glContext);
                    _spoutSender.SendImage((byte*)bufferToUse, 640, 480, Gl.BGRA, false, 0);
                }
            }


            // Send frames from correct buffer
            foreach (var frame in _frameQueue.GetConsumingEnumerable())
            {
                unsafe
                {
                    fixed (byte* p = frame)
                    {
                        _deviceContext.MakeCurrent(_glContext);
                        _spoutSender.SendImage(p, 640, 480, Gl.BGRA, false, 0u);
                    }
                }
            }
        }
        
        // Process depth, color, skeleton data to remove background
        private void BackgroundRemovedFrameReadyHandler(object sender, BackgroundRemovedColorFrameReadyEventArgs e)
        {
            using (var frame = e.OpenBackgroundRemovedColorFrame())
            {
                if (frame == null) return;

                byte[] managedBuffer = new byte[_bufferLength];
                frame.CopyPixelDataTo(managedBuffer);

                // Pick the buffer *not* being used by GL thread
                IntPtr targetBuffer = _useFirstBuffer ? _frameBufferPtr2 : _frameBufferPtr1;

                // Copy managed pixels to unmanaged buffer
                Marshal.Copy(managedBuffer, 0, targetBuffer, _bufferLength);
                
                _useFirstBuffer = !_useFirstBuffer;
                _frameReadyEvent.Set(); // Tell GlThread that a new frame is ready

            }
        } 

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

        private void ChooseSkeleton()
        {
            var isTrackedSkeltonVisible = false;
            var nearestDistance = float.MaxValue;
            var nearestSkeleton = 0;

            foreach (var skel in _skeletons)
            {
                if (null == skel)
                {
                    continue;
                }

                if (skel.TrackingState != SkeletonTrackingState.Tracked)
                {
                    continue;
                }

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
