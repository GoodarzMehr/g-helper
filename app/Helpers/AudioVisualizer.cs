using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace GHelper.Helpers
{
    public class AudioVisualizer : IMMNotificationClient
    {
        public static readonly AudioVisualizer Shared = new();
        private double RMSValue;

        private readonly HashSet<Action<double[]>> subscribers = new();

        private double[]? audioValues;
        private double[]? _hannWindow;
        private WasapiCapture? capture;
        private string? captureDeviceId;
        private MMDeviceEnumerator? enumerator;

        private readonly object _lock = new();
        private volatile bool _running;
        private volatile bool _stopping;
        private int _isProcessingFrame = 0;
        private long _lastFrameTime = 0;
        
        static double minTimeBetweenFramesMs = 1000.0 / AppConfig.Get("disco_fps", 40);

        public bool IsRunning => _running;

        public double GetRMSValue()
        {
            lock (_lock)
            {
                return RMSValue;
            }
        }

        public bool Subscribe(Action<double[]> handler)
        {
            lock (_lock)
            {
                if (subscribers.Contains(handler)) return true;
                if (subscribers.Count == 0 && !StartCapture()) return false;
                subscribers.Add(handler);
                return true;
            }
        }

        public void Unsubscribe(Action<double[]> handler)
        {
            lock (_lock)
            {
                if (!subscribers.Remove(handler)) return;
                if (subscribers.Count == 0) StopCapture();
            }
        }

        private bool StartCapture()
        {
            if (_running) return true;
            _stopping = false;

            try
            {
                enumerator = new MMDeviceEnumerator();
                enumerator.RegisterEndpointNotificationCallback(this);

                using (MMDevice device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console))
                {
                    capture = new WasapiLoopbackCapture(device);
                    captureDeviceId = device.ID;

                    var fmt = capture.WaveFormat;
                    
                    audioValues = new double[fmt.SampleRate / 100];

                    int N = audioValues.Length;
                    
                    _hannWindow = new double[N];
                    
                    for (int i = 0; i < N; i++)
                        _hannWindow[i] = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (N - 1)));

                    capture.DataAvailable += Capture_DataAvailable;
                    capture.StartRecording();
                }

                _running = true;
                Logger.WriteLine("AudioVisualizer: subscribed to default output");
                return true;
            }
            catch (Exception ex)
            {
                Logger.WriteLine("AudioVisualizer: " + ex);
                Cleanup();
                return false;
            }
        }

        private void StopCapture()
        {
            _stopping = true;
            _running = false;
            Cleanup();
            _stopping = false;
        }

        private void Cleanup()
        {
            if (enumerator is not null)
            {
                try { enumerator.UnregisterEndpointNotificationCallback(this); }
                catch (Exception ex) { Logger.WriteLine("AudioVisualizer: unregister failed: " + ex); }
            }

            if (capture is not null)
            {
                try
                {
                    capture.DataAvailable -= Capture_DataAvailable;
                    capture.StopRecording();
                    capture.Dispose();
                }
                catch (Exception ex)
                {
                    Logger.WriteLine("AudioVisualizer: dispose failed: " + ex);
                }
                capture = null;
            }

            captureDeviceId = null;

            if (enumerator is not null)
            {
                try { enumerator.Dispose(); } catch { /* ignore */ }
                enumerator = null;
            }
        }

        private void Capture_DataAvailable(object? sender, WaveInEventArgs e)
        {
            if (capture is null || audioValues is null) return;

            int bytesPerSamplePerChannel = capture.WaveFormat.BitsPerSample / 8;
            int bytesPerSample = bytesPerSamplePerChannel * capture.WaveFormat.Channels;
            int bufferSampleCount = e.Buffer.Length / bytesPerSample;
            if (bufferSampleCount > audioValues.Length) bufferSampleCount = audioValues.Length;

            if (bytesPerSamplePerChannel == 2 && capture.WaveFormat.Encoding == WaveFormatEncoding.Pcm)
                for (int i = 0; i < bufferSampleCount; i++)
                    audioValues[i] = BitConverter.ToInt16(e.Buffer, i * bytesPerSample);
            else if (bytesPerSamplePerChannel == 4 && capture.WaveFormat.Encoding == WaveFormatEncoding.Pcm)
                for (int i = 0; i < bufferSampleCount; i++)
                    audioValues[i] = BitConverter.ToInt32(e.Buffer, i * bytesPerSample);
            else if (bytesPerSamplePerChannel == 4 && capture.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat)
                for (int i = 0; i < bufferSampleCount; i++)
                    audioValues[i] = BitConverter.ToSingle(e.Buffer, i * bytesPerSample);

            double sumSq = 0;
            
            for (int i = 0; i < bufferSampleCount; i++)
                sumSq += audioValues[i] * audioValues[i];

            RMSValue = Math.Sqrt(sumSq / bufferSampleCount);
 
            double[] windowed = new double[bufferSampleCount];
            
            if (_hannWindow is not null)
                for (int i = 0; i < bufferSampleCount; i++)
                    windowed[i] = audioValues[i] * _hannWindow[i];
            else
                Array.Copy(audioValues, windowed, bufferSampleCount);

            double[] padded = FftSharp.Pad.ZeroPad(windowed);
            var fft = FftSharp.FFT.Forward(padded);
            double[] mag = FftSharp.FFT.Magnitude(fft);
            
            if (Interlocked.CompareExchange(ref _isProcessingFrame, 1, 0) == 0)
            {
                Action<double[]>[] snapshot;
                
                lock (_lock) snapshot = subscribers.ToArray();

                // Offload to a background thread so NAudio can keep listening without lag.
                Task.Run(() =>
                {
                    try
                    {
                        // Check if enough time has passed to send a new frame.
                        long now = System.Diagnostics.Stopwatch.GetTimestamp();
                        
                        double msSinceLastFrame = (now - _lastFrameTime) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

                        if (msSinceLastFrame >= minTimeBetweenFramesMs)
                        {
                            _lastFrameTime = now;
                            
                            // Actually send the data to Aura/Subscribers.
                            foreach (var sub in snapshot)
                            {
                                try { sub.Invoke(mag); }
                                catch (Exception ex) { Logger.WriteLine("AudioVisualizer: subscriber threw: " + ex); }
                            }
                        }
                    }
                    finally
                    {
                        // Release for the next frame.
                        Interlocked.Exchange(ref _isProcessingFrame, 0);
                    }
                });
            }
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (!_running || _stopping) return;
            if (flow != DataFlow.Render || role != Role.Console) return;

            var current = captureDeviceId;
            if (!string.IsNullOrEmpty(current) && current == defaultDeviceId) return;

            Logger.WriteLine("AudioVisualizer: default output changed -> " + defaultDeviceId);
            captureDeviceId = defaultDeviceId;

            Task.Delay(50).ContinueWith(_ =>
            {
                lock (_lock)
                {
                    if (subscribers.Count == 0) return;
                    StopCapture();
                    StartCapture();
                }
            });
        }
    }
}
