// ComInterop.cs - OLE message filter installed while the CAD application is busy.
//
// Moved verbatim out of ZwcHelper.cs (plan 5.1 batch 2, 2026-11-05; pure move). Without
// this filter an incoming COM call is rejected outright while CAD is mid-command or shows
// a modal dialog; with it OLE retries every 150 ms for up to 6 s (same policy as the WPS
// helper). No logic, no visibility and no constant was changed.

using System;
using System.Runtime.InteropServices;

namespace ZwcHelper
{
    // ======================================================================================
    // OLE message filter - busy retry (port of the WPS helper, policy from dsh-office).
    // A CAD that is mid-command / showing a modal dialog rejects incoming COM calls; without
    // this the command fails instantly. With it OLE retries every 150 ms for up to 6 s.
    // ======================================================================================
    [ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IOleMessageFilter
    {
        [PreserveSig] int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo);
        [PreserveSig] int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType);
        [PreserveSig] int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType);
    }

    internal sealed class MessageFilter : IOleMessageFilter
    {
        public const int RetryAfterMs = 150;
        public const int GiveUpAfterMs = 6000;
        private const int ServerCallRetryLater = 2;
        private const int PendingWaitDefault = 2;
        private const int ServerCallHandled = 0;

        [DllImport("ole32.dll")]
        private static extern int CoRegisterMessageFilter(IOleMessageFilter newFilter, out IOleMessageFilter oldFilter);

        private static MessageFilter _installed;
        public static int LastHResult = -1;

        public static int Register()
        {
            MessageFilter filter = new MessageFilter();
            IOleMessageFilter old = null;
            int hr;
            try { hr = CoRegisterMessageFilter(filter, out old); }
            catch (Exception) { LastHResult = -1; return -1; }
            _installed = filter;
            LastHResult = hr;
            return hr;
        }

        public int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo)
        { return ServerCallHandled; }

        public int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType)
        {
            if (dwRejectType == ServerCallRetryLater && dwTickCount < GiveUpAfterMs) return RetryAfterMs;
            return -1;
        }

        public int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType)
        { return PendingWaitDefault; }
    }
}
