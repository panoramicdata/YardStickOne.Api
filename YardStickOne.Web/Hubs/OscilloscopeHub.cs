namespace YardStickOne.Web.Hubs;

/// <summary>
/// SignalR hub that streams RF sample chunks to the browser oscilloscope.
/// </summary>
public sealed class OscilloscopeHub : Microsoft.AspNetCore.SignalR.Hub
{
	// Clients subscribe automatically when they load the page.
	// The server pushes "ChunkReceived" messages via IHubContext<OscilloscopeHub>.
}
