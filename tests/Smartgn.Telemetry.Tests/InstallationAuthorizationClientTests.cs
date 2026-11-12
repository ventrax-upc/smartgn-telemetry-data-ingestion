using System.Net;
using Smartgn.Telemetry.Infrastructure;

namespace Smartgn.Telemetry.Tests;

public class InstallationAuthorizationClientTests
{
    private sealed class ResponseHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body),
            });
        }
    }

    [Fact]
    public async Task AuthorizeAsync_SendsAssociationVersionAndChecksTheResponse()
    {
        const string deviceId = "ae29a705-0a10-4d1f-b2a1-6ced6f4cc002";
        var handler = new ResponseHandler(HttpStatusCode.OK,
            """{"deviceId":"ae29a705-0a10-4d1f-b2a1-6ced6f4cc002","associationVersion":3,"authorized":true}""");
        var client = new InstallationAuthorizationClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://installation"),
        });

        var result = await client.AuthorizeAsync(deviceId, 3);

        Assert.True(result.IsAuthorized);
        Assert.Equal("/internal/v1/devices/ae29a705-0a10-4d1f-b2a1-6ced6f4cc002/authorization",
            handler.Request!.RequestUri!.AbsolutePath);
        Assert.Equal("?associationVersion=3", handler.Request.RequestUri.Query);
    }

    [Fact]
    public async Task AuthorizeAsync_MismatchedAssociationVersion_FailsClosed()
    {
        var handler = new ResponseHandler(HttpStatusCode.OK,
            """{"deviceId":"device-1","associationVersion":2,"authorized":true}""");
        var client = new InstallationAuthorizationClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://installation"),
        });

        var result = await client.AuthorizeAsync("device-1", 3);

        Assert.False(result.IsAuthorized);
        Assert.Equal("installation_contract_invalid", result.Reason);
    }
}
