using System;
using System.Runtime.InteropServices;

namespace PluginDebugger.Debugging
{
    /// <summary>
    /// An <c>IOleMessageFilter</c> that retries COM calls a busy Visual Studio rejects with
    /// RPC_E_CALL_REJECTED / SERVERCALL_RETRYLATER. Talking to the DTE automation object without
    /// one routinely throws when VS is mid-operation, so we register it around ROT enumeration
    /// and attach. Must be registered on an STA thread (the WinForms UI thread is STA).
    /// </summary>
    internal sealed class MessageFilter : IOleMessageFilter
    {
        public static void Register()
        {
            CoRegisterMessageFilter(new MessageFilter(), out _);
        }

        public static void Revoke()
        {
            CoRegisterMessageFilter(null, out _);
        }

        // Incoming calls are always handled (SERVERCALL_ISHANDLED = 0).
        int IOleMessageFilter.HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo) => 0;

        int IOleMessageFilter.RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType)
        {
            // SERVERCALL_RETRYLATER = 2: retry after a short delay (return value < 100 = retry now-ish).
            return dwRejectType == 2 ? 99 : -1;
        }

        // PENDINGMSG_WAITDEFPROCESS = 2.
        int IOleMessageFilter.MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType) => 2;

        [DllImport("ole32.dll")]
        private static extern int CoRegisterMessageFilter(IOleMessageFilter newFilter, out IOleMessageFilter oldFilter);
    }

    [ComImport]
    [Guid("00000016-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IOleMessageFilter
    {
        [PreserveSig]
        int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo);

        [PreserveSig]
        int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType);

        [PreserveSig]
        int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType);
    }
}
