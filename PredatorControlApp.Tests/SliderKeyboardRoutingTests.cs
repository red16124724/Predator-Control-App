using System;
using System.Runtime.InteropServices;
using System.Threading;
using PredatorControlApp;
using Xunit;

namespace PredatorControlApp.Tests;

public class SliderKeyboardRoutingTests
{
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [Theory]
    [InlineData(0x25, 50, 45, 1)]
    [InlineData(0x26, 50, 55, 1)]
    [InlineData(0x27, 50, 55, 1)]
    [InlineData(0x28, 50, 45, 1)]
    [InlineData(0x25, 0, 0, 0)]
    [InlineData(0x27, 100, 100, 0)]
    public void ArrowMessage_ChangesAndCommitsValue(int key, int initial, int expected, int expectedCommits)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var slider = new PredatorSlider { Minimum = 0, Maximum = 100, Value = initial };
                int commits = 0;
                slider.ValueCommitted += (_, _) => commits++;
                SendMessage(slider.Handle, 0x0100, (IntPtr)key, IntPtr.Zero);
                Assert.Equal(expected, slider.Value);
                Assert.Equal(expectedCommits, commits);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw failure;
    }
}
