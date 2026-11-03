using System;
using System.Threading;
using System.Windows.Forms;

namespace PredatorControlApp.Tests
{
    public static class StaTestRunner
    {
        public static void Run(Action action, int timeoutMs = 15000)
        {
            Exception? caught = null;
            var thread = new Thread(() =>
            {
                try
                {
                    action();
                    Application.DoEvents();
                }
                catch (Exception ex)
                {
                    caught = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            bool finished = thread.Join(timeoutMs);
            if (!finished)
            {
                thread.Interrupt();
                throw new TimeoutException($"STA thread execution timed out after {timeoutMs}ms");
            }
            if (caught != null)
            {
                throw new AggregateException("STA execution error", caught);
            }
        }
    }
}
