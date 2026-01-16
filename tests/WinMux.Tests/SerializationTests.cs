using System.IO;
using System.Threading.Tasks;
using WinMux.Core.Protocol;
using Xunit;

namespace WinMux.Tests;

public class SerializationTests
{
    [Fact]
    public async Task CanRoundTrip_PingRequest()
    {
        var msg = new PingRequest();
        var ms = new MemoryStream();

        await Serialization.WriteFramedAsync<RequestMessage>(ms, msg);
        ms.Position = 0;

        var result = await Serialization.ReadFramedAsync<RequestMessage>(ms);
        Assert.IsType<PingRequest>(result);
    }

    [Fact]
    public async Task CanRoundTrip_CreateSessionRequest_WithPolymorphism()
    {
        var msg = new CreateSessionRequest("mysession", "pwsh", "C:\\", 100, 30);
        var ms = new MemoryStream();

        // Write as base type to ensure discriminator is included
        await Serialization.WriteFramedAsync<RequestMessage>(ms, msg);
        ms.Position = 0;

        var result = await Serialization.ReadFramedAsync<RequestMessage>(ms);
        var typed = Assert.IsType<CreateSessionRequest>(result);
        
        Assert.Equal("mysession", typed.Name);
        Assert.Equal("pwsh", typed.Shell);
        Assert.Equal(100, typed.Cols);
    }

    [Fact]
    public async Task CanRoundTrip_OutputEvent_LargePayload()
    {
        var data = new byte[8192];
        for (int i=0; i<data.Length; i++) data[i] = (byte)(i % 255);
        
        var msg = new OutputEvent("session-1", data);
        var ms = new MemoryStream();

        await Serialization.WriteFramedAsync<EventMessage>(ms, msg);
        ms.Position = 0;

        var result = await Serialization.ReadFramedAsync<EventMessage>(ms);
        var typed = Assert.IsType<OutputEvent>(result);
        
        Assert.Equal("session-1", typed.SessionId);
        Assert.Equal(data.Length, typed.Data.Length);
        Assert.Equal(data[100], typed.Data[100]);
    }
}
