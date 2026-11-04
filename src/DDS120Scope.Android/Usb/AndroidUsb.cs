using System;
using System.Collections.Generic;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Hardware.Usb;
using Android.OS;
using Dds120.Scope;

namespace Dds120.Droid.Usb
{
    // Finds the DDS120 on the USB host port and handles Android's per-device USB permission.
    // Permission belongs to one UsbDevice instance: after the firmware upload the scope re-enumerates
    // as a new device and has to be allowed again (unless "always open with this app" was ticked).
    internal static class AndroidUsb
    {
        // Give the system's own "open DDS-120 Scope?" dialog (from the attach intent filter) time to be
        // answered before showing a second permission dialog for the same device.
        const int AttachDialogGraceMs = 2000;

        static readonly object sync = new object();
        static readonly Dictionary<string, DateTime> firstSeen = new Dictionary<string, DateTime>();
        static readonly HashSet<string> requested = new HashSet<string>();
        static readonly HashSet<string> denied = new HashSet<string>();
        static Context context;
        static UsbManager manager;
        static PermissionReceiver receiver;
        static string permissionAction;

        public static event Action<string> Log;

        public static UsbManager Manager { get { return manager; } }

        // Set by the activity: permission dialogs are only shown while the app is on screen.
        public static volatile bool Foreground;

        public static bool HasUsbHost
        {
            get { return context != null && context.PackageManager.HasSystemFeature(PackageManager.FeatureUsbHost); }
        }

        public static void Init(Context ctx)
        {
            context = ctx.ApplicationContext;
            manager = (UsbManager)context.GetSystemService(Context.UsbService);
            permissionAction = context.PackageName + ".USB_PERMISSION";
            receiver = new PermissionReceiver();
            var filter = new IntentFilter(permissionAction);
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
                context.RegisterReceiver(receiver, filter, ReceiverFlags.NotExported);
            else
                context.RegisterReceiver(receiver, filter);
        }

        public static void Shutdown()
        {
            if (receiver != null)
            {
                try { context.UnregisterReceiver(receiver); } catch (Java.Lang.IllegalArgumentException) { }
                receiver = null;
            }
        }

        public static bool IsCandidate(UsbDevice d)
        {
            return d.VendorId == Dds120Device.StockVid && d.ProductId == Dds120Device.StockPid ||
                   d.VendorId == Dds120Device.FwVid && d.ProductId == Dds120Device.FwPid;
        }

        public static List<UsbDevice> FindCandidates()
        {
            var list = new List<UsbDevice>();
            if (manager == null)
                return list;
            IDictionary<string, UsbDevice> all = manager.DeviceList;
            if (all != null)
                foreach (UsbDevice d in all.Values)
                    if (IsCandidate(d))
                        list.Add(d);
            // Forget devices that are gone, so a recycled device name starts fresh.
            lock (sync)
            {
                var present = new HashSet<string>();
                foreach (UsbDevice d in list)
                    present.Add(d.DeviceName);
                foreach (string name in new List<string>(firstSeen.Keys))
                    if (!present.Contains(name))
                    {
                        firstSeen.Remove(name);
                        requested.Remove(name);
                        denied.Remove(name);
                    }
                foreach (string name in present)
                    if (!firstSeen.ContainsKey(name))
                        firstSeen[name] = DateTime.UtcNow;
            }
            return list;
        }

        public static bool HasPermission(UsbDevice d)
        {
            return manager.HasPermission(d);
        }

        public static bool WasDenied(UsbDevice d)
        {
            lock (sync)
                return denied.Contains(d.DeviceName);
        }

        // Shows the permission dialog once per device; later calls are no-ops until ForgetRequests().
        // Returns true if a request is (now or already) pending.
        public static bool RequestPermission(UsbDevice d)
        {
            lock (sync)
            {
                if (requested.Contains(d.DeviceName))
                    return !denied.Contains(d.DeviceName);
                DateTime seen;
                if (!Foreground ||
                    firstSeen.TryGetValue(d.DeviceName, out seen) && (DateTime.UtcNow - seen).TotalMilliseconds < AttachDialogGraceMs)
                    return true;
                requested.Add(d.DeviceName);
            }
            Log?.Invoke("Asking for USB permission: " + Describe(d));
            var intent = new Intent(permissionAction).SetPackage(context.PackageName);
            // The system adds the device and the result as extras, so the PendingIntent must be mutable.
            PendingIntentFlags flags = OperatingSystem.IsAndroidVersionAtLeast(31) ? PendingIntentFlags.Mutable : 0;
            PendingIntent pi = PendingIntent.GetBroadcast(context, 0, intent, flags);
            manager.RequestPermission(d, pi);
            return true;
        }

        // Lets RequestPermission ask again, e.g. after the user denied it and tapped the screen.
        public static void ForgetRequests()
        {
            lock (sync)
            {
                requested.Clear();
                denied.Clear();
                firstSeen.Clear();
            }
        }

        public static string Describe(UsbDevice d)
        {
            return string.Format("{0:X4}:{1:X4} {2}", d.VendorId, d.ProductId, d.DeviceName);
        }

        sealed class PermissionReceiver : BroadcastReceiver
        {
            public override void OnReceive(Context ctx, Intent intent)
            {
                UsbDevice d = OperatingSystem.IsAndroidVersionAtLeast(33)
                    ? (UsbDevice)intent.GetParcelableExtra(UsbManager.ExtraDevice, Java.Lang.Class.FromType(typeof(UsbDevice)))
#pragma warning disable CA1422 // the typed overload above needs API 33
                    : (UsbDevice)intent.GetParcelableExtra(UsbManager.ExtraDevice);
#pragma warning restore CA1422
                bool granted = intent.GetBooleanExtra(UsbManager.ExtraPermissionGranted, false);
                if (d == null)
                    return;
                if (!granted)
                    lock (sync)
                        denied.Add(d.DeviceName);
                Log?.Invoke("USB permission " + (granted ? "granted" : "denied") + ": " + Describe(d));
            }
        }
    }
}
