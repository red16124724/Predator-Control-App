using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace PredatorControlApp.Tests
{
    [Collection("NamedPipeTests")]
    public class SystemsStabilityAndRootCauseTests
    {
        [Fact]
        public void CurveFollower_Hysteresis_PreventsOscillationOnMinorDrop()
        {
            var follower = new CurveFollower();
            var curve = new List<Point>
            {
                new(30, 20),
                new(50, 40),
                new(70, 70),
                new(90, 100)
            };

            // Temperature rises to 70°C -> Fan speed 70%
            int speed1 = follower.Update(70, curve);
            Assert.Equal(70, speed1);

            // Minor temperature drop to 69°C (within 2°C hysteresis band) -> Fan speed holds at 70%
            int speed2 = follower.Update(69, curve);
            Assert.Equal(70, speed2);

            // Drop to 68.5°C (still within 2°C hysteresis band) -> Fan speed holds at 70%
            int speed3 = follower.Update(68.5, curve);
            Assert.Equal(70, speed3);

            // Drop to 67°C (more than 2°C drop from 70°C peak) -> Fan speed drops
            int speed4 = follower.Update(67, curve);
            Assert.True(speed4 < 70, $"Expected fan speed to decrease below 70%, got {speed4}");

            // Immediate reheat back to 70°C -> Fan speed jumps back up to 70%
            int speed5 = follower.Update(70, curve);
            Assert.Equal(70, speed5);
        }

        [Fact]
        public void InterpolateCurve_UnsortedPoints_SortsCorrectlyAndInterpolatesAccurately()
        {
            // Scrambled points order
            var scrambledCurve = new List<Point>
            {
                new(70, 70),
                new(30, 20),
                new(90, 100),
                new(50, 40)
            };

            int speedAt30 = Form1.InterpolateCurve(scrambledCurve, 30);
            int speedAt50 = Form1.InterpolateCurve(scrambledCurve, 50);
            int speedAt70 = Form1.InterpolateCurve(scrambledCurve, 70);
            int speedAt90 = Form1.InterpolateCurve(scrambledCurve, 90);
            int speedAt60 = Form1.InterpolateCurve(scrambledCurve, 60);

            Assert.Equal(20, speedAt30);
            Assert.Equal(40, speedAt50);
            Assert.Equal(70, speedAt70);
            Assert.Equal(100, speedAt90);
            Assert.Equal(55, speedAt60); // Midpoint of 40 and 70
        }

        [Fact]
        public async Task SecureNamedPipeIpc_ServerRecoversFromIdleTimeout_AcceptsSubsequentClients()
        {
            string pipeName = "PredatorControlPipe_TimeoutRecovery_" + Guid.NewGuid().ToString("N");
            string? received = null;
            using var server = new SecureNamedPipeIpc.PipeServer(msg => received = msg, pipeName);
            server.Start();

            await Task.Delay(100);

            // 1. Connect a client that connects but sends nothing and disconnects (or times out)
            try
            {
                using var client1 = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                using var cts1 = new CancellationTokenSource(2500);
                await client1.ConnectAsync(cts1.Token);
                // Intentionally do not send any line, let it close or read timeout elapse
            }
            catch { }

            // Allow server to process read timeout and reset
            await Task.Delay(300);

            // 2. Subsequent client must still connect and deliver its message
            bool sent = await SecureNamedPipeIpc.SendMessageWithVerificationAsync("SHOW_WINDOW", 3000, pipeName);
            Assert.True(sent, "Subsequent message send failed after previous connection timed out.");

            var sw = Stopwatch.StartNew();
            while (received == null && sw.ElapsedMilliseconds < 2500)
            {
                await Task.Delay(50);
            }

            Assert.Equal("SHOW_WINDOW", received);
        }

        [Fact]
        public void PdhLoadMonitor_SampleWithGpuDisabled_ReturnsNullGpuWithoutWakingD3Cold()
        {
            using var monitor = new PdhLoadMonitor();
            var (cpu, gpu) = monitor.Sample(sampleGpu: false);
            Assert.Null(gpu);
        }

        [Fact]
        public void WmiController_InvalidateSensorCaches_ClearsReadingsWithoutThrowing()
        {
            using var wmi = new WmiController();
            wmi.InvalidateSensorCaches();
            // Should execute without deadlock or exception
        }
    }
}
