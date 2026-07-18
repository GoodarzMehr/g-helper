using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace GHelper.Helpers
{
    public class CustomWasapiLoopbackCapture : WasapiCapture
    {
        public CustomWasapiLoopbackCapture(MMDevice captureDevice, int bufferMilliseconds = 25) 
            : base(captureDevice, false, bufferMilliseconds)
        {
        }

        protected override AudioClientStreamFlags GetAudioClientStreamFlags()
        {
            return AudioClientStreamFlags.Loopback;
        }

        public override WaveFormat WaveFormat
        {
            get { return base.WaveFormat; }
            set { throw new InvalidOperationException("WaveFormat cannot be set for WASAPI Loopback Capture"); }
        }
    }
}