using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace RezTray
{
    /// <summary>
    /// Lets a second launch reach the copy already running. Without this, clicking the Start
    /// menu entry while RezTray is in the tray would look like nothing happened.
    /// </summary>
    internal sealed class InstanceSignal : IDisposable
    {
#if DEBUG
        private const string Name = @"Local\RezTrayDebugShow";
#else
        private const string Name = @"Local\RezTrayShow";
#endif
        private const int AnyProcess = -1;

        private readonly EventWaitHandle _event = new EventWaitHandle(false, EventResetMode.AutoReset, Name);
        private readonly RegisteredWaitHandle _wait;

        public InstanceSignal(Action onSignal)
        {
            _wait = ThreadPool.RegisterWaitForSingleObject(_event, (state, timedOut) => onSignal(), null, Timeout.Infinite, false);
        }

        /// <summary>Called by the second launch, just before it exits.</summary>
        public static void Send()
        {
            AllowSetForegroundWindow(AnyProcess);   // otherwise Windows keeps the menu behind other windows
            try
            {
                using (var running = EventWaitHandle.OpenExisting(Name))
                    running.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // The other copy is just closing, so there is nothing to show.
            }
        }

        public void Dispose()
        {
            _wait.Unregister(null);
            _event.Dispose();
        }

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(int processId);
    }
}
