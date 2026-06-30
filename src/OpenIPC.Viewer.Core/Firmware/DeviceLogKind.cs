namespace OpenIPC.Viewer.Core.Firmware;

// Which on-device log the firmware maintenance dialog reads over SSH.
public enum DeviceLogKind
{
    Syslog,   // busybox logread (fallback /var/log/messages)
    Kernel,   // dmesg
    Majestic, // majestic.log / majestic-tagged syslog lines
}
