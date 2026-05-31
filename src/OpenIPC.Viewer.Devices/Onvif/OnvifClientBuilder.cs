using System;
using System.Reflection;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.ServiceModel.Description;
using System.Threading.Tasks;
using Onvif.Core.Client;
using Onvif.Core.Client.Common;
using Onvif.Core.Client.Device;
using Onvif.Core.Client.Media;
using Onvif.Core.Client.Ptz;
using Onvif.Core.Client.Security;
using OpenIPC.Viewer.Core.Onvif;

namespace OpenIPC.Viewer.Devices.Onvif;

// Replacement for Onvif.Core's OnvifClientFactory. The upstream factory builds an
// HTTP binding with AuthenticationScheme=Anonymous and only authenticates via the
// SOAP WS-UsernameToken header. OpenIPC's onvif_simple_server enforces HTTP Basic
// at the transport (401 "Basic realm=Authentication"), so the factory's first
// GetSystemDateAndTime call dies before any WS header is in play.
//
// We keep the upstream binding + WS-UsernameToken flow (so cameras that only want
// the SOAP header keep working) and additionally inject a preemptive HTTP Basic
// "Authorization" header on every request via a message inspector. A camera that
// doesn't need it ignores the extra header; OpenIPC accepts it. No transport-scheme
// negotiation, no extra round-trips.
//
// The generated DeviceClient/MediaClient/PTZClient only expose an *internal*
// (Binding, EndpointAddress) constructor, so we invoke it reflectively — the upstream
// factory is the only public way to build them and it can't be made to do HTTP auth.
internal static class OnvifClientBuilder
{
    public static async Task<DeviceClient> CreateDeviceClientAsync(OnvifEndpoint ep)
    {
        if (ep.Credentials is null)
            return await CreatePreAuthDeviceAsync(ep.DeviceServiceUri).ConfigureAwait(false);

        var creds = CameraCredentialsView.From(ep.Credentials);
        var (device, _) = await OpenAuthedDeviceAsync(ep.DeviceServiceUri, creds).ConfigureAwait(false);
        return device;
    }

    public static async Task<MediaClient> CreateMediaClientAsync(OnvifEndpoint ep)
    {
        var creds = CameraCredentialsView.From(ep.Credentials);
        var (device, shift) = await OpenAuthedDeviceAsync(ep.DeviceServiceUri, creds).ConfigureAwait(false);
        try
        {
            var caps = await device.GetCapabilitiesAsync(new[] { CapabilityCategory.Media }).ConfigureAwait(false);
            var media = Build<MediaClient>(new Uri(caps.Capabilities.Media.XAddr), creds, shift);
            await media.OpenAsync().ConfigureAwait(false);
            return media;
        }
        finally
        {
            CloseQuietly(device);
        }
    }

    public static async Task<PTZClient> CreatePtzClientAsync(OnvifEndpoint ep)
    {
        var creds = CameraCredentialsView.From(ep.Credentials);
        var (device, shift) = await OpenAuthedDeviceAsync(ep.DeviceServiceUri, creds).ConfigureAwait(false);
        try
        {
            var caps = await device.GetCapabilitiesAsync(new[] { CapabilityCategory.PTZ }).ConfigureAwait(false);
            var ptz = Build<PTZClient>(new Uri(caps.Capabilities.PTZ.XAddr), creds, shift);
            await ptz.OpenAsync().ConfigureAwait(false);
            return ptz;
        }
        finally
        {
            CloseQuietly(device);
        }
    }

    private static async Task<DeviceClient> CreatePreAuthDeviceAsync(Uri uri)
    {
        var device = Construct<DeviceClient>(CreateBinding(), uri);
        device.ChannelFactory.Endpoint.EndpointBehaviors.Clear();
        await device.OpenAsync().ConfigureAwait(false);
        return device;
    }

    // Probes GetSystemDateAndTime (Basic header injected, no WS header yet) to get the
    // clock offset the WS-UsernameToken digest needs, then builds the real device client
    // carrying both auth forms. Mirrors the upstream two-step time-shift dance.
    private static async Task<(DeviceClient device, TimeSpan shift)> OpenAuthedDeviceAsync(Uri uri, CameraCredentialsView creds)
    {
        var probe = Construct<DeviceClient>(CreateBinding(), uri);
        probe.ChannelFactory.Endpoint.EndpointBehaviors.Clear();
        AddHttpBasic(probe.ChannelFactory.Endpoint, creds);

        TimeSpan shift;
        try
        {
            shift = await probe.GetDeviceTimeShift().ConfigureAwait(false);
        }
        finally
        {
            CloseQuietly(probe);
        }

        var device = Build<DeviceClient>(uri, creds, shift);
        await device.OpenAsync().ConfigureAwait(false);
        return (device, shift);
    }

    // Constructs a client and attaches both auth behaviors: preemptive HTTP Basic on the
    // transport plus WS-UsernameToken in the SOAP header.
    private static T Build<T>(Uri uri, CameraCredentialsView creds, TimeSpan shift)
        where T : class
    {
        var client = Construct<T>(CreateBinding(), uri);
        var endpoint = EndpointOf(client);
        endpoint.EndpointBehaviors.Clear();
        AddHttpBasic(endpoint, creds);
        endpoint.EndpointBehaviors.Add(new SoapSecurityHeaderBehavior(creds.Username, creds.Password, shift));
        return client;
    }

    // Preemptive HTTP Basic only when a username is set — credential-less endpoints keep
    // their previous behavior (anonymous transport, empty WS header) untouched.
    private static void AddHttpBasic(ServiceEndpoint endpoint, CameraCredentialsView creds)
    {
        if (!string.IsNullOrEmpty(creds.Username))
            endpoint.EndpointBehaviors.Add(new HttpBasicAuthBehavior(creds.Username, creds.Password));
    }

    // ClientBase<T>.ChannelFactory is typed ChannelFactory<T>; its non-generic base
    // exposes Endpoint. Reached reflectively so Build<T> stays channel-agnostic.
    private static ServiceEndpoint EndpointOf(object client)
    {
        var factory = (ChannelFactory)client.GetType()
            .GetProperty("ChannelFactory", BindingFlags.Instance | BindingFlags.Public)!
            .GetValue(client)!;
        return factory.Endpoint;
    }

    private static Binding CreateBinding()
    {
        var binding = new CustomBinding();
        binding.Elements.Add(new TextMessageEncodingBindingElement
        {
            MessageVersion = MessageVersion.CreateVersion(EnvelopeVersion.Soap12, AddressingVersion.None),
        });
        binding.Elements.Add(new HttpTransportBindingElement
        {
            AllowCookies = true,
            MaxBufferSize = int.MaxValue,
            MaxReceivedMessageSize = int.MaxValue,
        });
        return binding;
    }

    // The generated clients only expose an internal (Binding, EndpointAddress) ctor.
    private static T Construct<T>(Binding binding, Uri uri) where T : class
    {
        var ctor = typeof(T).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            new[] { typeof(Binding), typeof(EndpointAddress) },
            modifiers: null);
        if (ctor is null)
            throw new InvalidOperationException($"{typeof(T).Name} has no (Binding, EndpointAddress) constructor");
        return (T)ctor.Invoke(new object[] { binding, new EndpointAddress(uri) });
    }

    private static void CloseQuietly(ICommunicationObject co)
    {
        try
        {
            if (co.State == CommunicationState.Faulted)
                co.Abort();
            else
                co.Close();
        }
        catch
        {
            try { co.Abort(); } catch { /* swallow */ }
        }
    }

    // Flattens nullable credentials to non-null strings (empty == anonymous, matching the
    // upstream factory's "" / "" calls for credential-less endpoints).
    private readonly struct CameraCredentialsView
    {
        public string Username { get; }
        public string Password { get; }

        private CameraCredentialsView(string username, string password)
        {
            Username = username;
            Password = password;
        }

        public static CameraCredentialsView From(Core.Entities.CameraCredentials? creds) =>
            new(creds?.Username ?? "", creds?.Password ?? "");
    }
}
