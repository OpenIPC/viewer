using System;
using System.Net;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.ServiceModel.Description;
using System.ServiceModel.Dispatcher;
using System.Text;

namespace OpenIPC.Viewer.Devices.Onvif;

// Injects a preemptive HTTP "Authorization: Basic …" header on every outgoing SOAP
// request. ONVIF servers like OpenIPC's onvif_simple_server gate the device service
// behind HTTP Basic (401 "Basic realm=Authentication"); WCF's default Anonymous
// transport never sends credentials, so we add them at the message layer instead of
// reconfiguring the binding. Cameras that don't require it simply ignore the header.
internal sealed class HttpBasicAuthBehavior : IEndpointBehavior
{
    private readonly string _headerValue;

    public HttpBasicAuthBehavior(string username, string password)
    {
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
        _headerValue = "Basic " + token;
    }

    public void ApplyClientBehavior(ServiceEndpoint endpoint, ClientRuntime clientRuntime) =>
        clientRuntime.ClientMessageInspectors.Add(new Inspector(_headerValue));

    public void AddBindingParameters(ServiceEndpoint endpoint, BindingParameterCollection bindingParameters) { }
    public void ApplyDispatchBehavior(ServiceEndpoint endpoint, EndpointDispatcher endpointDispatcher) { }
    public void Validate(ServiceEndpoint endpoint) { }

    private sealed class Inspector : IClientMessageInspector
    {
        private readonly string _headerValue;

        public Inspector(string headerValue) => _headerValue = headerValue;

        public object? BeforeSendRequest(ref Message request, IClientChannel channel)
        {
            HttpRequestMessageProperty http;
            if (request.Properties.TryGetValue(HttpRequestMessageProperty.Name, out var existing))
            {
                http = (HttpRequestMessageProperty)existing;
            }
            else
            {
                http = new HttpRequestMessageProperty();
                request.Properties.Add(HttpRequestMessageProperty.Name, http);
            }

            http.Headers[HttpRequestHeader.Authorization] = _headerValue;
            return null;
        }

        public void AfterReceiveReply(ref Message reply, object? correlationState) { }
    }
}
