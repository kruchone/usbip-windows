using System;
using System.Runtime.InteropServices;

namespace UsbIpClient;

/**
 * Announces this PC as "_usbip._tcp" over mDNS while it shares devices, so other USB/IP apps find it like they
 * find phones. Uses the Windows DNS-SD API (dnsapi.dll, Windows 10 1809+). usbipd-win itself doesn't advertise.
 */
public sealed class MdnsAdvertiser : IDisposable
{
    private const uint DNS_QUERY_REQUEST_VERSION1 = 1;
    private const uint DNS_REQUEST_PENDING = 9506;

    [StructLayout(LayoutKind.Sequential)]
    private struct DNS_SERVICE_REGISTER_REQUEST
    {
        public uint Version;
        public uint InterfaceIndex;
        public IntPtr pServiceInstance;
        public IntPtr pRegisterCompletionCallback;
        public IntPtr pQueryContext;
        public IntPtr hCredentials;
        public int unicastEnabled;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void RegisterComplete(uint status, IntPtr context, IntPtr instance);

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DnsServiceConstructInstance(
        string serviceName, string hostName, IntPtr ip4, IntPtr ip6, ushort port, ushort priority, ushort weight,
        uint propertiesCount, string[] keys, string[] values);

    [DllImport("dnsapi.dll")]
    private static extern uint DnsServiceRegister(ref DNS_SERVICE_REGISTER_REQUEST request, IntPtr cancel);

    [DllImport("dnsapi.dll")]
    private static extern uint DnsServiceDeRegister(ref DNS_SERVICE_REGISTER_REQUEST request, IntPtr cancel);

    [DllImport("dnsapi.dll")]
    private static extern void DnsServiceFreeInstance(IntPtr instance);

    private readonly RegisterComplete _callback = (_, _, _) => { }; // kept alive for the native side
    private DNS_SERVICE_REGISTER_REQUEST _request;
    private bool _registered;

    public bool IsAdvertising => _registered;

    public void Start(ushort port = 3240)
    {
        if (_registered) return;
        var host = Environment.MachineName;
        var instance = DnsServiceConstructInstance(
            $"{host}._usbip._tcp.local", $"{host}.local", IntPtr.Zero, IntPtr.Zero, port, 0, 0,
            1, new[] { "v" }, new[] { "1" });
        if (instance == IntPtr.Zero) return;
        _request = new DNS_SERVICE_REGISTER_REQUEST
        {
            Version = DNS_QUERY_REQUEST_VERSION1,
            pServiceInstance = instance,
            pRegisterCompletionCallback = Marshal.GetFunctionPointerForDelegate(_callback),
        };
        var rc = DnsServiceRegister(ref _request, IntPtr.Zero);
        if (rc == DNS_REQUEST_PENDING) _registered = true;
        else DnsServiceFreeInstance(instance);
    }

    public void Stop()
    {
        if (!_registered) return;
        _registered = false;
        DnsServiceDeRegister(ref _request, IntPtr.Zero);
        // The instance stays allocated until process exit: the async deregistration may still reference it.
    }

    public void Dispose() => Stop();
}
